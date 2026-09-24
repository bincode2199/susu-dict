// Generates the Su-Su app and tray icons from the DESIGN 6 geometry (no image library, deterministic output).
// Path on a 64 grid: M16 16h20a8 8 0 0 1 0 16H28a8 8 0 0 0 0 16h20; stroke 9.375 % of the canvas, round caps.
// App icon: paper plate with 18.75 % corner radius and a 1-physical-pixel #C9CDD3 outline, ink stroke.
// Tray icons: stroke only - ink for light taskbars, paper for dark taskbars.
// Usage: dotnet run --project tools/Susu.IconGen -- <output folder>
using System.Buffers.Binary;

string output = args.Length > 0 ? args[0] : Path.Combine("src", "Susu.Host", "assets");
Directory.CreateDirectory(output);
(byte R, byte G, byte B) ink = (0x16, 0x18, 0x1D), paper = (0xFF, 0xFF, 0xFF), outline = (0xC9, 0xCD, 0xD3);
Write(Path.Combine(output, "susu.ico"), [16, 20, 24, 32, 40, 48, 64, 256], size => Render(size, ink, plate: true));
Write(Path.Combine(output, "tray-dark.ico"), [16, 20, 24, 32, 40, 48], size => Render(size, ink, plate: false));
Write(Path.Combine(output, "tray-light.ico"), [16, 20, 24, 32, 40, 48], size => Render(size, paper, plate: false));
Console.WriteLine($"icons written to {output}");

byte[] Render(int size, (byte R, byte G, byte B) stroke, bool plate)
{
    const int ss = 4; // 4x4 supersampling
    var pixels = new byte[size * size * 4];
    double scale = size / 64.0, half = size * 0.09375 / 2, radius = size * 0.1875;
    for (int y = 0; y < size; y++)
    for (int x = 0; x < size; x++)
    {
        int strokeHits = 0, plateHits = 0;
        for (int sy = 0; sy < ss; sy++)
        for (int sx = 0; sx < ss; sx++)
        {
            double px = x + (sx + 0.5) / ss, py = y + (sy + 0.5) / ss;
            if (PathDistance(px / scale, py / scale) * scale <= half) strokeHits++;
            if (plate && InsideRoundedRect(px, py, size, radius)) plateHits++;
        }
        double a = strokeHits / (double)(ss * ss), p = plateHits / (double)(ss * ss);
        bool border = plate && p > 0 && p < 1 || plate && p == 1 && !InsideRoundedRect(x + 0.5, y + 0.5, size, radius, inset: 1);
        var (r, g, b) = border ? outline : paper;
        double bgAlpha = plate ? p : 0;
        // Composite stroke over plate.
        double outA = a + bgAlpha * (1 - a);
        double cr = outA == 0 ? 0 : (stroke.R * a + r * bgAlpha * (1 - a)) / outA;
        double cg = outA == 0 ? 0 : (stroke.G * a + g * bgAlpha * (1 - a)) / outA;
        double cb = outA == 0 ? 0 : (stroke.B * a + b * bgAlpha * (1 - a)) / outA;
        int i = ((size - 1 - y) * size + x) * 4; // bottom-up DIB
        pixels[i] = (byte)Math.Round(cb); pixels[i + 1] = (byte)Math.Round(cg); pixels[i + 2] = (byte)Math.Round(cr); pixels[i + 3] = (byte)Math.Round(outA * 255);
    }
    return pixels;
}

static bool InsideRoundedRect(double x, double y, int size, double r, double inset = 0)
{
    double left = inset, top = inset, right = size - inset, bottom = size - inset, rr = Math.Max(0, r - inset);
    if (x < left || x > right || y < top || y > bottom) return false;
    double cx = Math.Clamp(x, left + rr, right - rr), cy = Math.Clamp(y, top + rr, bottom - rr);
    return (x - cx) * (x - cx) + (y - cy) * (y - cy) <= rr * rr;
}

static double PathDistance(double x, double y)
{
    double d = Segment(x, y, 16, 16, 36, 16);
    d = Math.Min(d, Arc(x, y, 36, 24, 8, rightHalf: true));
    d = Math.Min(d, Segment(x, y, 36, 32, 28, 32));
    d = Math.Min(d, Arc(x, y, 28, 40, 8, rightHalf: false));
    return Math.Min(d, Segment(x, y, 28, 48, 48, 48));
}

static double Segment(double x, double y, double x1, double y1, double x2, double y2)
{
    double dx = x2 - x1, dy = y2 - y1;
    double t = Math.Clamp(((x - x1) * dx + (y - y1) * dy) / (dx * dx + dy * dy), 0, 1);
    return Math.Sqrt(Math.Pow(x - (x1 + t * dx), 2) + Math.Pow(y - (y1 + t * dy), 2));
}

// Half circle: the right half (x >= cx) for the upper turn, the left half for the lower turn.
static double Arc(double x, double y, double cx, double cy, double r, bool rightHalf)
{
    bool within = rightHalf ? x >= cx : x <= cx;
    if (within) return Math.Abs(Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - r);
    return Math.Min(Math.Sqrt((x - cx) * (x - cx) + (y - cy + r) * (y - cy + r)), Math.Sqrt((x - cx) * (x - cx) + (y - cy - r) * (y - cy - r)));
}

static void Write(string path, int[] sizes, Func<int, byte[]> render)
{
    var images = sizes.Select(size => (size, Dib(size, render(size)))).ToList();
    using var file = File.Create(path);
    Span<byte> header = stackalloc byte[6];
    BinaryPrimitives.WriteUInt16LittleEndian(header[2..], 1);
    BinaryPrimitives.WriteUInt16LittleEndian(header[4..], (ushort)images.Count);
    file.Write(header);
    int offset = 6 + 16 * images.Count;
    Span<byte> entry = stackalloc byte[16];
    foreach (var (size, dib) in images)
    {
        entry.Clear();
        entry[0] = (byte)(size >= 256 ? 0 : size);
        entry[1] = (byte)(size >= 256 ? 0 : size);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[6..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(entry[8..], dib.Length);
        BinaryPrimitives.WriteInt32LittleEndian(entry[12..], offset);
        file.Write(entry);
        offset += dib.Length;
    }
    foreach (var (_, dib) in images) file.Write(dib);
}

// 32-bit BI_RGB DIB with a zero AND mask (alpha carries transparency).
static byte[] Dib(int size, byte[] bgra)
{
    int maskStride = ((size + 31) / 32) * 4;
    var dib = new byte[40 + bgra.Length + maskStride * size];
    BinaryPrimitives.WriteInt32LittleEndian(dib, 40);
    BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), size);
    BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), size * 2);
    BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), 32);
    BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(20), bgra.Length + maskStride * size);
    bgra.CopyTo(dib, 40);
    return dib;
}
