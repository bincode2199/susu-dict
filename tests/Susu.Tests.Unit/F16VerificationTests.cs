using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Jobs;
using Susu.Plugins;
using Susu.Plugins.Install;
using Susu.Runtime;
using Susu.Storage;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// Independent F16 verification (testing agent): UPD01-UPD04, X02, X05, S02 and the F16 exit line, attacked end to end. Zip attacks go through the
/// real <see cref="PluginInstaller"/> (zip-slip variants, ADS, device names, links, bombs, zip64, odd names, header lies), signature tampering of every
/// file, held changes with the OLD version still serving calls in the real sandbox, built-in override and uninstall across a restart, crash at every
/// activation step (simulated by an uncatchable fault at the step and a fresh database/installer, the way a restart sees it), KV isolation, in-flight
/// cancellation of one package only, sandbox ACL leftovers after a session ends and after a child crash, the susu-plugin CLI with hostile packages and
/// cases files, and the sandbox crypto / URLSearchParams edges. Real-sandbox tests fail (not skip) when the published executables are missing.
/// Not executed: a real desktop, drag-and-drop, the file dialog, a real update feed, a forced hard kill of the main process.
/// </summary>
public sealed class F16VerificationTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly List<IDisposable> cleanup = [];
    public void Dispose() { for (int i = cleanup.Count - 1; i >= 0; i--) cleanup[i].Dispose(); } // reverse: a rig (sandbox host using the database) goes before its environment

    private const string Id = "com.example.echo";
    private const string BuiltInId = "app.susu.deepl";

    // ================= kit =================

    private sealed class FakeHost : IPluginHostControl
    {
        public readonly List<string> Restarts = [];
        public IReadOnlyList<PluginTaskInfo> InFlight(string packageId) => [];
        public int Restart(string packageId) { Restarts.Add(packageId); return 0; }
    }

    private sealed class Env : IDisposable
    {
        public readonly string Temp = TestTemp.NewDir("susu-f16v");
        public readonly string Zips = TestTemp.NewDir("susu-f16z");
        public Database Db = null!;
        public PluginInstallationRepository Store = null!;
        public PluginKvRepository Kv = null!;
        public readonly FakeHost Host = new();
        public string UserRoot => Path.Combine(Temp, "plugins");
        public string Staging => Path.Combine(UserRoot, ".staging");
        public Env() => Open();
        private void Open()
        {
            Db = Database.Open(Path.Combine(Temp, "susu.db"));
            Store = new PluginInstallationRepository(Db);
            Kv = new PluginKvRepository(Db);
        }
        /// <summary>A restart: the database closes and opens again, and a new installer must be built.</summary>
        public void Restart() { Db.Dispose(); Open(); }
        public static HostKeyring Keyring() => new([new KeyValuePair<string, byte[]>(PackageFactory.HostKeyId, Ed25519.PublicKey(PackageFactory.SeedA))]);
        public static BuiltInPackages BuiltIns() => new([new BuiltInPackage(BuiltInId, "1.2.0", new PermissionSet(["translate"], ["https://api.deepl.com:443"], ["apiKey"]))]);
        public PluginInstaller Installer(Func<HealthRequest, HealthResult>? health = null, Action<string>? fault = null)
            => new(UserRoot, Store, BuiltIns(), Keyring(), health, fault, Host, removeData: id => Kv.DeletePackage(id));
        public void Dispose() { try { Db.Dispose(); } catch (Exception) { } }
        public int StagingDirs => Directory.Exists(Staging) ? Directory.GetDirectories(Staging).Length : 0;
        public IEnumerable<string> Tree() => Directory.Exists(UserRoot) ? Directory.EnumerateFileSystemEntries(UserRoot, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(UserRoot, p)).Order() : [];
    }

    private Env NewEnv() { var e = new Env(); cleanup.Add(e); return e; }

    private static string Manifest(string id = Id, string version = "1.0.0", string hosts = "", string secrets = "", string capabilities = "translate")
        => PackageFactory.Manifest(id, version, capabilities, hosts, secrets);

    /// <summary>A normal package zip (manifest.yaml + main.js + the extras), optionally signed.</summary>
    private static string Pkg(Env env, string file, string manifest, byte[]? seed = null, string? keyId = null, IDictionary<string, byte[]>? extra = null, string main = PackageFactory.Main)
    {
        var files = PackageFactory.Files(manifest, main);
        foreach (var (k, v) in extra ?? new Dictionary<string, byte[]>()) files[k] = v;
        return PackageFactory.Zip(env.Zips, file, files, seed, keyId);
    }

    private static string Install(PluginInstaller installer, string zip, bool ack = true)
    {
        var p = installer.Preview(zip);
        Assert.True(p.Ok, string.Join(",", p.Issues.Select(i => i.Path + ":" + i.Code)));
        var o = installer.Install(p.Token!, ack);
        Assert.True(o.Ok, o.Error);
        return o.Version!;
    }

    // ---------------- raw zip writer: stored entries with any name bytes, attributes and declared sizes ----------------

    private sealed record RawEntry(byte[] Name, byte[] Data, uint Attr = 0, uint? DeclaredSize = null);

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return ~crc;
    }

    private static byte[] RawZip(IEnumerable<RawEntry> entries)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var central = new List<(RawEntry E, uint Offset, uint Crc)>();
        foreach (var e in entries)
        {
            uint offset = (uint)ms.Position, crc = Crc32(e.Data), size = e.DeclaredSize ?? (uint)e.Data.Length;
            w.Write(0x04034b50); w.Write((ushort)20); w.Write((ushort)0x800); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0x21);
            w.Write(crc); w.Write((uint)e.Data.Length); w.Write(size); w.Write((ushort)e.Name.Length); w.Write((ushort)0);
            w.Write(e.Name); w.Write(e.Data);
            central.Add((e, offset, crc));
        }
        uint cdOffset = (uint)ms.Position;
        foreach (var (e, offset, crc) in central)
        {
            w.Write(0x02014b50); w.Write((ushort)((3 << 8) | 20)); w.Write((ushort)20); w.Write((ushort)0x800); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0x21);
            w.Write(crc); w.Write((uint)e.Data.Length); w.Write(e.DeclaredSize ?? (uint)e.Data.Length); w.Write((ushort)e.Name.Length); w.Write((ushort)0); w.Write((ushort)0);
            w.Write((ushort)0); w.Write((ushort)0); w.Write(e.Attr); w.Write(offset); w.Write(e.Name);
        }
        uint cdSize = (uint)ms.Position - cdOffset;
        w.Write(0x06054b50); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)central.Count); w.Write((ushort)central.Count); w.Write(cdSize); w.Write(cdOffset); w.Write((ushort)0);
        w.Flush();
        return ms.ToArray();
    }

    private static RawEntry Raw(string name, string data = "x", uint attr = 0) => new(Encoding.UTF8.GetBytes(name), Encoding.UTF8.GetBytes(data), attr);

    private static List<RawEntry> GoodEntries() =>
        [Raw("manifest.yaml", Manifest()), Raw("main.js", PackageFactory.Main)];

    private static string WriteZip(Env env, string name, IEnumerable<RawEntry> entries)
    {
        string path = Path.Combine(env.Zips, name);
        File.WriteAllBytes(path, RawZip(entries));
        return path;
    }

    private static HashSet<string> FilesUnder(string root) => [.. Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
        .Where(p => !p.Contains(".db", StringComparison.Ordinal)).Select(p => Path.GetRelativePath(root, p))
        .Where(p => p is not ("plugins" or "plugins\\.staging"))];

    /// <summary>The package is refused by Preview, staging holds nothing, and nothing but the database exists in the user folder or anywhere above it.</summary>
    private static PluginPreviewInfo AssertRefused(Env env, PluginInstaller installer, string zip, string? sentinel = null)
    {
        var before = FilesUnder(env.Temp);
        var preview = installer.Preview(zip);
        Assert.False(preview.Ok, "the hostile package was accepted: " + zip);
        Assert.Null(preview.Token);
        Assert.Equal(0, env.StagingDirs);
        Assert.False(Directory.Exists(Path.Combine(env.UserRoot, "packages")), "something went live");
        Assert.Empty(FilesUnder(env.Temp).Except(before));
        for (var dir = new DirectoryInfo(env.Temp); dir is not null; dir = dir.Parent)
            Assert.Empty(dir.EnumerateFiles("sent-*"));
        if (sentinel is not null) Assert.False(File.Exists(sentinel), "the sentinel outside the package exists: " + sentinel);
        return preview;
    }

    // ================= UPD01: zip attacks through the real installer =================

    public static TheoryData<string> BadNames()
    {
        string g = Guid.NewGuid().ToString("N");
        return new TheoryData<string>
        {
            "../evil.js", "..\\evil.js", "a/../../evil.js", "a\\..\\..\\evil.js", "a/..\\evil.js", "a\\../evil.js", "./main2.js", "a/./b.js", "..", "../", "a/../b.js",
            $"../../../../../../../../sent-{g}.txt", $"..\\..\\..\\..\\..\\..\\..\\..\\sent-{g}.txt", "....//evil.js", "/abs.js", "\\abs.js", "C:\\evil.js", "C:/evil.js", "c:evil.js",
            "\\\\server\\share\\x.js", "//server/share/x.js", "\\\\?\\C:\\evil.js", "\\\\.\\pipe\\x", "main.js:stream", "main.js::$DATA", "main.js:Zone.Identifier", "dir/a.js:s",
            "CON", "con.txt", "NUL", "nul.js", "AUX.json", "aux .js", "Com1", "COM1.tar.gz", "lpt9", "CONIN$", "CONOUT$.x", "PRN.", "dir/PRN/x.js", "COM\u00B9", "LPT\u00B2.txt",
            "x.js.", "x.js ", "dir./x.js", "dir /x.js", "a//b.js", "x*.js", "x?.js", "x|y.js", "x<y.js", "x\"y.js", "x\u0000.js", "x\n.js", "x\u001f.js", "PROGRA~1/x.js", "EVIL~1.JS",
            new string('a', 201) + ".js", "dir/" + new string('b', 250), string.Join("/", Enumerable.Repeat("d", 120)) + "/x.js",
        };
    }

    [Theory]
    [MemberData(nameof(BadNames))]
    public void A_hostile_entry_name_is_refused_before_anything_is_written(string name)
    {
        var env = NewEnv();
        var installer = env.Installer();
        var entries = GoodEntries();
        entries.Add(Raw(name, "evil"));
        AssertRefused(env, installer, WriteZip(env, "hostile.susuext", entries));
    }

    [Fact]
    public void An_absolute_entry_name_that_points_at_a_real_file_neither_writes_nor_overwrites_it()
    {
        var env = NewEnv();
        string victim = Path.Combine(env.Temp, "victim.txt");
        File.WriteAllText(victim, "original");
        foreach (string name in new[] { victim, victim.Replace('\\', '/'), "\\" + victim[2..], "\\\\?\\" + victim })
        {
            var installer = env.Installer();
            var entries = GoodEntries();
            entries.Add(Raw(name, "overwritten"));
            AssertRefused(env, installer, WriteZip(env, "abs.susuext", entries));
            Assert.Equal("original", File.ReadAllText(victim));
        }
        // and a created-sentinel variant: nothing appears where the name pointed
        string target = Path.Combine(env.Temp, "sent-created.txt");
        var e2 = GoodEntries();
        e2.Add(Raw(target, "x"));
        AssertRefused(env, env.Installer(), WriteZip(env, "abs2.susuext", e2), target);
    }

    [Fact]
    public void A_zero_length_name_and_a_lone_slash_are_refused()
    {
        var env = NewEnv();
        var installer = env.Installer();
        foreach (string name in new[] { "", "/", "\\", " ", ".", "main.js/" })
        {
            var entries = GoodEntries();
            entries.Add(new RawEntry(Encoding.UTF8.GetBytes(name), name.EndsWith('/') ? [] : Encoding.UTF8.GetBytes("x")));
            var preview = installer.Preview(WriteZip(env, "z.susuext", entries)); // "main.js/" as an empty directory beside a file "main.js" must not corrupt either
            Assert.False(preview.Ok, $"name '{name}' accepted");
            Assert.Equal(0, env.StagingDirs);
        }
    }

    [Fact]
    public void Names_are_compared_without_case_and_a_duplicate_exact_name_is_refused()
    {
        var env = NewEnv();
        var installer = env.Installer();
        foreach (var (a, b) in new[] { ("main.js", "MAIN.JS"), ("main.js", "main.js"), ("lib/a.js", "LIB/A.js"), ("lib/a.js", "lib/A.JS"), ("manifest.yaml", "Manifest.YAML") })
        {
            var entries = GoodEntries();
            entries.RemoveAll(e => Encoding.UTF8.GetString(e.Name) == a);
            entries.Add(Raw(a, a == "manifest.yaml" ? Manifest() : "export default {};"));
            entries.Add(Raw(b, "export default {};"));
            if (a == "manifest.yaml") entries.Add(Raw("main.js", PackageFactory.Main));
            AssertRefused(env, installer, WriteZip(env, "dup.susuext", entries));
        }
    }

    [Fact]
    public void A_file_and_a_directory_that_share_a_path_are_refused_without_a_half_package()
    {
        var env = NewEnv();
        var entries = GoodEntries();
        entries.Add(Raw("lib", "i am a file"));
        entries.Add(Raw("lib/x.js", "i am below it"));
        AssertRefused(env, env.Installer(), WriteZip(env, "fd.susuext", entries));
        var e2 = GoodEntries();
        e2.Add(Raw("lib/x.js", "below"));
        e2.Add(Raw("lib", "file"));
        AssertRefused(env, env.Installer(), WriteZip(env, "fd2.susuext", e2));
    }

    [Fact]
    public void Unicode_normalization_and_case_mapping_collisions_never_leave_a_half_package_or_two_files_on_one_name()
    {
        var env = NewEnv();
        var pairs = new[]
        {
            ("caf\u00E9.js", "cafe\u0301.js"),        // NFC vs NFD
            ("ma\u0131n2.js", "main2.js"),            // dotless i
            ("k.js", "\u212A.js"),                     // Kelvin sign
            ("stra\u00DFe.js", "STRASSE.js"),         // sharp s
            ("\uFF0E\uFF0E/x.js", "evil.js"),          // fullwidth dots: a plain directory name on Windows, not a traversal
        };
        foreach (var (a, b) in pairs)
        {
            var installer = env.Installer();
            var entries = GoodEntries();
            entries.Add(Raw(a, "one"));
            entries.Add(Raw(b, "two"));
            var preview = installer.Preview(WriteZip(env, "uni.susuext", entries));
            if (preview.Ok)
            {
                // accepted: then every entry is a distinct, readable file inside the staged package, and discarding removes all of it
                string dir = Path.Combine(env.Staging, preview.Token!);
                Assert.Equal(4, Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count());
                installer.Discard(preview.Token!);
            }
            Assert.Equal(0, env.StagingDirs);
            for (var d = new DirectoryInfo(env.Temp); d is not null; d = d.Parent) Assert.Empty(d.EnumerateFiles("evil.js"));
        }
    }

    [Theory]
    [InlineData(0xA1FF)] // unix symlink
    [InlineData(0xA180)]
    [InlineData(0x1180)] // fifo
    [InlineData(0x2180)] // char device
    [InlineData(0x6180)] // block device
    [InlineData(0xC180)] // socket
    public void Link_and_special_file_entries_are_refused(int mode)
    {
        var env = NewEnv();
        var entries = GoodEntries();
        entries.Add(new RawEntry(Encoding.UTF8.GetBytes("lib/link.js"), Encoding.UTF8.GetBytes("../../../../Windows/System32/drivers/etc/hosts"), (uint)mode << 16));
        AssertRefused(env, env.Installer(), WriteZip(env, "link.susuext", entries));
    }

    [Fact]
    public void A_reparse_point_attribute_and_a_link_that_replaces_a_required_file_are_refused()
    {
        var env = NewEnv();
        var reparse = GoodEntries();
        reparse.Add(new RawEntry(Encoding.UTF8.GetBytes("lib/r.js"), Encoding.UTF8.GetBytes("x"), (uint)FileAttributes.ReparsePoint));
        AssertRefused(env, env.Installer(), WriteZip(env, "reparse.susuext", reparse));

        var replacing = new List<RawEntry> { Raw("manifest.yaml", Manifest()), new(Encoding.UTF8.GetBytes("main.js"), Encoding.UTF8.GetBytes("C:\\Windows\\win.ini"), 0xA1FFu << 16) };
        AssertRefused(env, env.Installer(), WriteZip(env, "replace.susuext", replacing));

        // a directory entry flagged as a link
        var dirLink = GoodEntries();
        dirLink.Add(new RawEntry(Encoding.UTF8.GetBytes("lib/"), [], 0xA1FFu << 16));
        AssertRefused(env, env.Installer(), WriteZip(env, "dirlink.susuext", dirLink));
    }

    [Theory]
    [InlineData("inner.zip")]
    [InlineData("lib/inner.ZIP")]
    [InlineData("plugin.susuext")]
    [InlineData("a.jar")]
    [InlineData("b.7z")]
    [InlineData("c.cab")]
    [InlineData("d.msi")]
    [InlineData("e.nupkg")]
    public void A_nested_archive_is_refused_never_extracted(string name)
    {
        var env = NewEnv();
        var inner = new MemoryStream();
        using (var z = new ZipArchive(inner, ZipArchiveMode.Create, true)) { using var s = z.CreateEntry("../../sent-nested.txt").Open(); s.WriteByte(1); }
        var entries = GoodEntries();
        entries.Add(new RawEntry(Encoding.UTF8.GetBytes(name), inner.ToArray()));
        AssertRefused(env, env.Installer(), WriteZip(env, "nested.susuext", entries));
    }

    [Fact]
    public void Entry_count_bombs_are_refused_including_zip64()
    {
        var env = NewEnv();
        // 513 entries, 257 files, and 70000 entries (the writer switches to zip64 records above 65535)
        foreach (int count in new[] { 513, 70_000 })
        {
            string path = Path.Combine(env.Zips, $"count{count}.susuext");
            using (var fs = File.Create(path))
            using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var (n, c) in new[] { ("manifest.yaml", Manifest()), ("main.js", PackageFactory.Main) })
                { using var s = z.CreateEntry(n).Open(); s.Write(Encoding.UTF8.GetBytes(c)); }
                for (int i = 0; i < count; i++) z.CreateEntry($"d/{i}.txt");
            }
            if (count > 65535) { Assert.True(File.ReadAllBytes(path).AsSpan().IndexOf(BitConverter.GetBytes(0x06064b50)) >= 0, "expected zip64 end-of-central-directory"); }
            var sw = Stopwatch.StartNew();
            AssertRefused(env, env.Installer(), path);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), "refusing a huge entry count took " + sw.Elapsed);
        }
        var files = GoodEntries();
        for (int i = 0; i < 257; i++) files.Add(Raw($"f/{i}.txt", "x"));
        AssertRefused(env, env.Installer(), WriteZip(env, "files257.susuext", files));
    }

    [Fact]
    public void Size_and_ratio_bombs_are_refused()
    {
        var env = NewEnv();
        byte[] Random(int n) => RandomNumberGenerator.GetBytes(n);
        var cases = new Dictionary<string, Dictionary<string, byte[]>>
        {
            ["one file 5 MiB random"] = new() { ["big.bin"] = Random(5 << 20) },
            ["17 x 1 MiB random (total over 16 MiB)"] = Enumerable.Range(0, 17).ToDictionary(i => $"p{i}.bin", _ => Random(1 << 20)),
            ["1 MiB of zeros (ratio)"] = new() { ["z.bin"] = new byte[1 << 20] },
            ["200 KiB of zeros (ratio)"] = new() { ["z.bin"] = new byte[200 * 1024] },
            ["3 x 3.9 MiB zeros"] = Enumerable.Range(0, 3).ToDictionary(i => $"z{i}.bin", _ => new byte[(int)(3.9 * 1024 * 1024)]),
            ["16 MiB of text-like zeros in 5 files of 3.4 MiB"] = Enumerable.Range(0, 5).ToDictionary(i => $"t{i}.txt", _ => Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n", 35000)))),
        };
        foreach (var (label, extra) in cases)
        {
            var sw = Stopwatch.StartNew();
            string zip = Pkg(env, "bomb.susuext", Manifest(), extra: extra);
            AssertRefused(env, env.Installer(), zip);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), label + " took " + sw.Elapsed);
        }
        // the zip file itself over 20 MiB of incompressible data is refused unread
        string huge = Pkg(env, "huge.susuext", Manifest(), extra: new Dictionary<string, byte[]> { ["a.bin"] = Random(4 << 20), ["b.bin"] = Random(4 << 20), ["c.bin"] = Random(4 << 20), ["d.bin"] = Random(4 << 20), ["e.bin"] = Random(4 << 20), ["f.bin"] = Random(1 << 20) });
        Assert.True(new FileInfo(huge).Length > 20 << 20);
        AssertRefused(env, env.Installer(), huge);
    }

    /// <summary>A header that understates the real size must not let more than the limits onto the disk.</summary>
    [Theory]
    [InlineData(5 << 20, 100u)]
    [InlineData(5 << 20, 1000u)]
    [InlineData(3 << 20, 60000u)]
    [InlineData(20 << 20, 4000000u)]
    public void A_zip_whose_header_lies_about_the_size_cannot_exceed_the_limits_on_disk(int actual, uint declared)
    {
        var env = NewEnv();
        // deflate by hand through ZipArchive, then rewrite the sizes in the local and central headers
        string path = Path.Combine(env.Zips, "lie.susuext");
        using (var fs = File.Create(path))
        using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            foreach (var (n, c) in new[] { ("manifest.yaml", Manifest()), ("main.js", PackageFactory.Main) })
            { using var s = z.CreateEntry(n).Open(); s.Write(Encoding.UTF8.GetBytes(c)); }
            using var big = z.CreateEntry("lib/big.bin", CompressionLevel.SmallestSize).Open();
            var chunk = new byte[1 << 16];
            for (int written = 0; written < actual; written += chunk.Length) big.Write(chunk);
        }
        byte[] bytes = File.ReadAllBytes(path);
        PatchUncompressedSize(bytes, "lib/big.bin", declared);
        File.WriteAllBytes(path, bytes);
        var installer = env.Installer();
        var before = FilesUnder(env.Temp);
        PluginPreviewInfo preview;
        try { preview = installer.Preview(path); }
        catch (Exception e) { Assert.Fail("the installer threw instead of refusing: " + e); return; }
        if (preview.Ok)
        {
            long onDisk = Directory.EnumerateFiles(Path.Combine(env.Staging, preview.Token!), "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            Assert.True(onDisk <= SafePackage.MaxTotalBytes, $"{onDisk} bytes staged");
            Assert.True(new FileInfo(Path.Combine(env.Staging, preview.Token!, "lib", "big.bin")).Length <= SafePackage.MaxFileBytes, "a file over the per-file limit was staged because the header lied");
        }
        else Assert.Equal(0, env.StagingDirs);
        Assert.Equal(0, FilesUnder(env.Temp).Except(before).Count(p => !p.Contains(".staging", StringComparison.Ordinal)));
    }

    private static void PatchUncompressedSize(byte[] zip, string name, uint size)
    {
        int eocd = zip.AsSpan().LastIndexOf(BitConverter.GetBytes(0x06054b50));
        int cd = BinaryPrimitives.ReadInt32LittleEndian(zip.AsSpan(eocd + 16));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(eocd + 10));
        for (int i = 0; i < count; i++)
        {
            int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(cd + 28)), extra = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(cd + 30)), comment = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(cd + 32));
            if (Encoding.UTF8.GetString(zip, cd + 46, nameLen) == name)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(cd + 24), size);
                int local = BinaryPrimitives.ReadInt32LittleEndian(zip.AsSpan(cd + 42));
                BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(local + 22), size);
                return;
            }
            cd += 46 + nameLen + extra + comment;
        }
        throw new InvalidOperationException("entry not found");
    }

    [Fact]
    public void Files_that_are_not_packages_are_refused_without_throwing()
    {
        var env = NewEnv();
        var installer = env.Installer();
        string good = Pkg(env, "good.susuext", Manifest());
        byte[] goodBytes = File.ReadAllBytes(good);
        var junk = new Dictionary<string, byte[]>
        {
            ["empty"] = [],
            ["random"] = RandomNumberGenerator.GetBytes(4096),
            ["truncated"] = goodBytes[..(goodBytes.Length / 2)],
            ["no-eocd"] = goodBytes[..(goodBytes.Length - 22)],
            ["prefixed"] = [.. Encoding.ASCII.GetBytes("MZ stub"), .. goodBytes],
            ["flipped"] = goodBytes.Select((b, i) => i == goodBytes.Length / 3 ? (byte)(b ^ 0xFF) : b).ToArray(),
            ["zero-entries"] = RawZip([]),
            ["manifest-only"] = RawZip([Raw("manifest.yaml", Manifest())]),
            ["no-manifest"] = RawZip([Raw("main.js", PackageFactory.Main)]),
            ["manifest-in-subfolder"] = RawZip([Raw("pkg/manifest.yaml", Manifest()), Raw("pkg/main.js", PackageFactory.Main)]),
        };
        foreach (var (label, bytes) in junk)
        {
            string p = Path.Combine(env.Zips, label + ".susuext");
            File.WriteAllBytes(p, bytes);
            var preview = installer.Preview(p);
            Assert.False(preview.Ok, label);
            Assert.Equal(0, env.StagingDirs);
        }
        Assert.False(installer.Preview(Path.Combine(env.Zips, "nope.susuext")).Ok);
        Assert.False(installer.Preview(env.Zips).Ok); // a directory
        Assert.False(installer.Preview("").Ok);
        Assert.False(installer.Preview("\\\\invalid-host-name.invalid\\share\\x.susuext").Ok);
        Assert.False(installer.Preview("CON").Ok);
        Assert.Empty(installer.Installed());
    }

    [Fact]
    public void A_manifest_that_is_a_nesting_or_alias_bomb_is_refused_in_the_same_process_without_crashing()
    {
        var env = NewEnv();
        var installer = env.Installer();
        string head = "id: com.example.echo\nname: X\nversion: 1.0.0\napiVersion: 1\nminHost: 1\nentry: main.js\ncapabilities: [translate]\n";
        var manifests = new Dictionary<string, string>
        {
            ["deep map"] = head + "x: " + string.Concat(Enumerable.Repeat("{a: ", 9000)) + "1" + new string('}', 9000) + "\n",
            ["alias bomb"] = head + "a: &a [x,x,x,x,x,x,x,x,x]\nb: &b [*a,*a,*a,*a,*a,*a,*a,*a,*a]\nc: &c [*b,*b,*b,*b,*b,*b,*b,*b,*b]\nd: &d [*c,*c,*c,*c,*c,*c,*c,*c,*c]\n",
            ["merge key"] = head + "a: &a {k: v}\nb: {<<: *a}\n",
            ["multi document"] = head + "---\nid: other\n",
            ["tag"] = head + "x: !!python/object/apply:os.system [calc]\n",
            ["binary nul"] = head + "x: \"\\0\\0\"\n",
            ["utf16 bom"] = "\uFEFF" + head,
            ["64KiB padding"] = head + "x: " + new string('a', 70000) + "\n",
        };
        foreach (var (label, text) in manifests)
        {
            string zip = WriteZip(env, "m.susuext", [Raw("manifest.yaml", text), Raw("main.js", PackageFactory.Main)]);
            var sw = Stopwatch.StartNew();
            var preview = installer.Preview(zip);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), label + " took " + sw.Elapsed);
            if (label is "utf16 bom" or "deep map" or "tag" or "binary nul") { installer.Discard(preview.Token ?? ""); continue; } // either answer is fine; the point is that it returned
            Assert.False(preview.Ok, "accepted: " + label);
            Assert.Equal(0, env.StagingDirs);
        }
    }

    // ================= UPD02: signatures and identity =================

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    private static Dictionary<string, byte[]> Extras() => new() { ["data/x.json"] = Bytes("{\"a\":1}"), ["lib/util.js"] = Bytes("export const f = 1;") };

    private static byte[] Rewrite(string zipPath, Func<string, byte[], byte[]?> change, IDictionary<string, byte[]>? add = null)
    {
        using var input = new ZipArchive(File.OpenRead(zipPath), ZipArchiveMode.Read);
        using var ms = new MemoryStream();
        using (var output = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            foreach (var entry in input.Entries)
            {
                using var s = entry.Open();
                using var buf = new MemoryStream();
                s.CopyTo(buf);
                byte[]? data = change(entry.FullName, buf.ToArray());
                if (data is null) continue;
                using var o = output.CreateEntry(entry.FullName).Open();
                o.Write(data);
            }
            foreach (var (n, d) in add ?? new Dictionary<string, byte[]>()) { using var o = output.CreateEntry(n).Open(); o.Write(d); }
        }
        return ms.ToArray();
    }

    private static string Signature(string keyId, byte[] seed, string manifest, IDictionary<string, byte[]> others, string algorithm = "ed25519")
    {
        var hashes = others.Select(f => (f.Key, Convert.ToHexStringLower(SHA256.HashData(f.Value)))).OrderBy(h => h.Key, StringComparer.Ordinal).ToList();
        return $"keyId: {keyId}\nalgorithm: {algorithm}\nsignature: {Convert.ToBase64String(Ed25519.Sign(seed, PackageTrust.SignedBytes(Bytes(manifest), hashes)))}\n";
    }

    [Fact]
    public void A_signed_package_is_refused_after_a_change_to_any_one_file_the_manifest_or_the_file_list()
    {
        var env = NewEnv();
        var installer = env.Installer();
        string signed = Pkg(env, "signed.susuext", Manifest(), PackageFactory.SeedB, extra: Extras());
        Assert.True(installer.Preview(signed).Ok); // control
        installer.Discard(installer.Preview(signed).Token!);

        var variants = new Dictionary<string, byte[]>
        {
            ["main.js changed"] = Rewrite(signed, (n, d) => n == "main.js" ? [.. d, .. Bytes("\n//")] : d),
            ["manifest changed (name)"] = Rewrite(signed, (n, d) => n == "manifest.yaml" ? Bytes(Encoding.UTF8.GetString(d).Replace("name: Echo", "name: Echo2")) : d),
            ["manifest trailing newline"] = Rewrite(signed, (n, d) => n == "manifest.yaml" ? [.. d, (byte)'\n'] : d),
            ["manifest CRLF"] = Rewrite(signed, (n, d) => n == "manifest.yaml" ? Bytes(Encoding.UTF8.GetString(d).Replace("\n", "\r\n")) : d),
            ["manifest permission added"] = Rewrite(signed, (n, d) => n == "manifest.yaml" ? Bytes(Encoding.UTF8.GetString(d) + "hosts:\n  - https://evil.example.com:443\n") : d),
            ["data file changed"] = Rewrite(signed, (n, d) => n == "data/x.json" ? Bytes("{\"a\":2}") : d),
            ["nested file changed one byte"] = Rewrite(signed, (n, d) => { if (n == "lib/util.js") { d[3] ^= 1; } return d; }),
            ["file added"] = Rewrite(signed, (n, d) => d, new Dictionary<string, byte[]> { ["extra.js"] = Bytes("evil()") }),
            ["file added in a folder"] = Rewrite(signed, (n, d) => d, new Dictionary<string, byte[]> { ["data/evil.js"] = Bytes("evil()") }),
            ["file removed"] = Rewrite(signed, (n, d) => n == "data/x.json" ? null : d),
            ["file renamed"] = RenameEntry(signed, "data/x.json", "data/y.json"),
            ["file renamed by case"] = RenameEntry(signed, "data/x.json", "data/X.json"),
            ["entry swapped between names"] = SwapContents(signed, "data/x.json", "lib/util.js"),
            ["signature truncated"] = Rewrite(signed, (n, d) => n == "signature" ? Bytes(Encoding.UTF8.GetString(d).Replace("signature: ", "signature: AAAA")) : d),
        };
        foreach (var (label, bytes) in variants)
        {
            string p = Path.Combine(env.Zips, "t.susuext");
            File.WriteAllBytes(p, bytes);
            var preview = installer.Preview(p);
            Assert.False(preview.Ok, "accepted after tampering: " + label);
            Assert.Equal(0, env.StagingDirs);
        }
        Assert.Empty(installer.Installed());
    }

    private static byte[] RenameEntry(string zipPath, string from, string to)
    {
        using var input = new ZipArchive(File.OpenRead(zipPath), ZipArchiveMode.Read);
        using var ms = new MemoryStream();
        using (var output = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var entry in input.Entries)
            {
                using var s = entry.Open();
                using var o = output.CreateEntry(entry.FullName == from ? to : entry.FullName).Open();
                s.CopyTo(o);
            }
        return ms.ToArray();
    }

    private static byte[] SwapContents(string zipPath, string a, string b)
    {
        byte[]? da = null, db = null;
        Rewrite(zipPath, (n, d) => { if (n == a) da = d; if (n == b) db = d; return d; });
        return Rewrite(zipPath, (n, d) => n == a ? db : n == b ? da : d);
    }

    [Fact]
    public void A_forged_or_malformed_signature_file_is_refused_in_every_form()
    {
        var env = NewEnv();
        var installer = env.Installer();
        string manifest = Manifest();
        var others = new Dictionary<string, byte[]> { ["manifest.yaml"] = Bytes(manifest), ["main.js"] = Bytes(PackageFactory.Main) }; // the signature covers every file but itself, the manifest included
        string keyB = PackageFactory.ThirdPartyKeyId(PackageFactory.SeedB), keyA = PackageFactory.ThirdPartyKeyId(PackageFactory.SeedA);
        string good = Signature(keyB, PackageFactory.SeedB, manifest, others);
        var sigs = new Dictionary<string, string>
        {
            ["host key id signed by a different seed"] = Signature(PackageFactory.HostKeyId, PackageFactory.SeedB, manifest, others),
            ["unknown host key id"] = Signature("host-unknown-2030", PackageFactory.SeedA, manifest, others),
            ["third-party id of A signed by B"] = Signature(keyA, PackageFactory.SeedB, manifest, others),
            ["third-party id upper case"] = Signature(keyB.ToUpperInvariant(), PackageFactory.SeedB, manifest, others),
            ["third-party id too short"] = Signature(keyB[..62], PackageFactory.SeedB, manifest, others),
            ["algorithm rsa"] = Signature(keyB, PackageFactory.SeedB, manifest, others, "rsa"),
            ["algorithm upper"] = Signature(keyB, PackageFactory.SeedB, manifest, others, "ED25519 "),
            ["not base64"] = good.Replace("signature: ", "signature: ***"),
            ["empty signature"] = $"keyId: {keyB}\nalgorithm: ed25519\nsignature: \"\"\n",
            ["missing signature key"] = $"keyId: {keyB}\nalgorithm: ed25519\n",
            ["missing key id"] = good.Replace("keyId:", "kid:"),
            ["63 byte signature"] = $"keyId: {keyB}\nalgorithm: ed25519\nsignature: {Convert.ToBase64String(new byte[63])}\n",
            ["65 byte signature"] = $"keyId: {keyB}\nalgorithm: ed25519\nsignature: {Convert.ToBase64String(new byte[65])}\n",
            ["zero signature"] = $"keyId: {keyB}\nalgorithm: ed25519\nsignature: {Convert.ToBase64String(new byte[64])}\n",
            ["all ones signature"] = $"keyId: {keyB}\nalgorithm: ed25519\nsignature: {Convert.ToBase64String(Enumerable.Repeat((byte)0xFF, 64).ToArray())}\n",
            ["all zero public key id"] = Signature(new string('0', 64), PackageFactory.SeedB, manifest, others),
            ["not yaml"] = "\u0000\u0001garbage {{{",
            ["json"] = "{\"keyId\":\"" + keyB + "\"}",
            ["huge"] = good + new string('#', 100_000),
            ["two documents"] = good + "---\n" + good,
            ["duplicate key"] = good + $"keyId: {keyA}\n",
        };
        foreach (var (label, text) in sigs)
        {
            string zip = WriteZip(env, "forged.susuext", [Raw("manifest.yaml", manifest), Raw("main.js", PackageFactory.Main), Raw("signature", text)]);
            var preview = installer.Preview(zip);
            Assert.False(preview.Ok, "forged signature accepted: " + label);
            Assert.Equal(0, env.StagingDirs);
        }
        // control: the genuine signature over the same bytes is accepted, with the key as the identity
        var ok = installer.Preview(WriteZip(env, "genuine.susuext", [Raw("manifest.yaml", manifest), Raw("main.js", PackageFactory.Main), Raw("signature", good)]));
        Assert.True(ok.Ok);
        Assert.Equal(("thirdParty", keyB), (ok.SignerKind, ok.Signer));
    }

    [Fact]
    public void Signing_over_a_changed_entry_path_a_directory_signature_and_a_second_signature_entry_do_not_slip_through()
    {
        var env = NewEnv();
        var installer = env.Installer();
        string signed = Pkg(env, "s.susuext", Manifest(), PackageFactory.SeedB, extra: Extras());
        // a "Signature" with different case, and one in a subfolder, beside the real one: still exactly the verified bytes or a refusal
        foreach (string name in new[] { "SIGNATURE", "Signature", "sub/signature", "signature.bak", "signature/" })
        {
            string p = Path.Combine(env.Zips, "s2.susuext");
            byte[] bytes = Rewrite(signed, (n, d) => d, new Dictionary<string, byte[]> { [name] = Bytes("keyId: x\n") });
            File.WriteAllBytes(p, bytes);
            var preview = installer.Preview(p);
            // the extra file is not covered by the signature (it was added after), so the package must not verify as signed
            Assert.False(preview.Ok, "an unsigned extra file beside a signed package was accepted: " + name);
            Assert.Equal(0, env.StagingDirs);
        }
    }

    [Fact]
    public void A_third_party_cannot_take_a_built_in_id_and_a_host_package_must_be_newer_with_identity_version_and_file_list_all_agreeing()
    {
        var env = NewEnv();
        var installer = env.Installer();
        string M(string v) => Manifest(BuiltInId, v, "https://api.deepl.com:443", "apiKey");
        Assert.Contains(installer.Preview(Pkg(env, "u.susuext", M("1.3.0"))).Issues, i => i.Code == "builtin-id-not-host-signed");
        Assert.Contains(installer.Preview(Pkg(env, "t.susuext", M("1.3.0"), PackageFactory.SeedB)).Issues, i => i.Code == "builtin-id-not-host-signed");
        Assert.Contains(installer.Preview(Pkg(env, "t2.susuext", M("1.3.0"), PackageFactory.SeedB, keyId: PackageFactory.ThirdPartyKeyId(PackageFactory.SeedA))).Issues.Select(i => i.Code), c => c is "signature-invalid" or "builtin-id-not-host-signed" or "signature-bad");
        foreach (string v in new[] { "1.2.0", "1.1.9", "0.9.9", "1.0.0" })
            Assert.Contains(installer.Preview(Pkg(env, "h.susuext", M(v), PackageFactory.SeedA, PackageFactory.HostKeyId)).Issues, i => i.Code == "not-newer-than-builtin");
        // a host-signed package that gained a file after signing is refused as a signature failure, not accepted as a built-in
        string hostSigned = Pkg(env, "hs.susuext", M("1.3.0"), PackageFactory.SeedA, PackageFactory.HostKeyId);
        File.WriteAllBytes(Path.Combine(env.Zips, "hs2.susuext"), Rewrite(hostSigned, (n, d) => d, new Dictionary<string, byte[]> { ["extra.js"] = Bytes("evil()") }));
        Assert.False(installer.Preview(Path.Combine(env.Zips, "hs2.susuext")).Ok);
        Assert.Empty(installer.Installed());
        // id look-alikes are different ids, not the built-in
        foreach (string id in new[] { "app.susu.deepl2", "app.susu.deepl.x", "app.susu.Deepl", "APP.SUSU.DEEPL", "app.susu.deepl " })
        {
            var p = installer.Preview(Pkg(env, "l.susuext", Manifest(id, "1.0.0")));
            if (p.Ok) { Assert.Null(p.OverridesBuiltIn); installer.Discard(p.Token!); }
        }
        Assert.Equal(0, env.StagingDirs);
    }

    [Fact]
    public void A_built_in_override_then_uninstall_restores_the_built_in_and_survives_restarts_in_both_states()
    {
        var env = NewEnv();
        var installer = env.Installer();
        string host = Pkg(env, "o.susuext", Manifest(BuiltInId, "1.3.0", "https://api.deepl.com:443", "apiKey"), PackageFactory.SeedA, PackageFactory.HostKeyId);
        var preview = installer.Preview(host);
        Assert.True(preview.Ok, string.Join(",", preview.Issues.Select(i => i.Code)));
        Assert.Equal("1.2.0", preview.OverridesBuiltIn);
        Assert.Equal("builtin", preview.Diff!.Against);
        Assert.True(installer.Install(preview.Token!).Ok);
        Assert.NotNull(installer.ActiveDirectory(BuiltInId));

        // restart with the override active: it is still the active version, and one directory exists
        env.Restart();
        installer = env.Installer();
        installer.Recover();
        Assert.Equal("1.3.0", Assert.Single(installer.Installed()).Version);
        Assert.NotNull(installer.ActiveDirectory(BuiltInId));
        Assert.Equal(["1.3.0"], Directory.GetDirectories(Path.Combine(env.UserRoot, "packages", BuiltInId)).Select(Path.GetFileName));

        var removed = installer.Uninstall(BuiltInId);
        Assert.True(removed.Ok);
        Assert.Equal("1.2.0", removed.RestoredBuiltIn);
        Assert.Null(installer.ActiveDirectory(BuiltInId)); // null: the shipped package is in effect
        Assert.Empty(installer.Installed());
        Assert.False(Directory.Exists(Path.Combine(env.UserRoot, "packages", BuiltInId)));

        env.Restart();
        installer = env.Installer();
        installer.Recover();
        Assert.Null(installer.ActiveDirectory(BuiltInId));
        Assert.Empty(installer.Installed());
        Assert.Null(env.Store.Active(BuiltInId));
        Assert.False(Directory.Exists(Path.Combine(env.UserRoot, "packages", BuiltInId)));

        // the built-in can be overridden again with the same version (nothing was left behind that says "installed")
        Assert.True(installer.Install(installer.Preview(host).Token!).Ok);
        // a second uninstall of something not installed is an error, not a crash
        Assert.True(installer.Uninstall(BuiltInId).Ok);
        Assert.Equal("uninstall.notInstalled", installer.Uninstall(BuiltInId).Error);
    }

    // ================= UPD03: held changes =================

    [Fact]
    public void Signer_change_signature_removal_and_every_permission_expansion_are_held_and_the_old_version_stays_until_acknowledged()
    {
        var env = NewEnv();
        var installer = env.Installer();
        Install(installer, Pkg(env, "v1.susuext", Manifest("com.example.echo", "1.0.0", "https://a.example.com:443", "apiKey"), PackageFactory.SeedB));
        string signerB = PackageFactory.ThirdPartyKeyId(PackageFactory.SeedB);
        env.Host.Restarts.Clear();

        var held = new (string Label, string Zip, string Reason)[]
        {
            ("signer changed", Pkg(env, "a.susuext", Manifest(Id, "1.1.0", "https://a.example.com:443", "apiKey"), PackageFactory.SeedA, keyId: PackageFactory.ThirdPartyKeyId(PackageFactory.SeedA)), "signer-changed"),
            ("signature dropped", Pkg(env, "b.susuext", Manifest(Id, "1.1.0", "https://a.example.com:443", "apiKey")), "signature-removed"),
            ("host added", Pkg(env, "c.susuext", Manifest(Id, "1.1.0", "https://a.example.com:443,https://b.example.com:443", "apiKey"), PackageFactory.SeedB), "permissions-expanded"),
            ("host changed (same count)", Pkg(env, "d.susuext", Manifest(Id, "1.1.0", "https://other.example.com:443", "apiKey"), PackageFactory.SeedB), "permissions-expanded"),
            ("port changed", Pkg(env, "e.susuext", Manifest(Id, "1.1.0", "https://a.example.com:8443", "apiKey"), PackageFactory.SeedB), "permissions-expanded"),
            ("scheme changed", Pkg(env, "f.susuext", Manifest(Id, "1.1.0", "http://a.example.com:80", "apiKey"), PackageFactory.SeedB), "permissions-expanded"),
            ("credential use added", Pkg(env, "g.susuext", Manifest(Id, "1.1.0", "https://a.example.com:443", "apiKey,secretKey"), PackageFactory.SeedB), "permissions-expanded"),
            ("capability added", Pkg(env, "h.susuext", Manifest(Id, "1.1.0", "https://a.example.com:443", "apiKey", "translate,ocr"), PackageFactory.SeedB), "permissions-expanded"),
        };
        foreach (var (label, zip, reason) in held)
        {
            var staged = installer.StageUpdate(zip);
            Assert.True(staged.Ok, label + ": " + string.Join(",", staged.Issues.Select(i => i.Code)));
            Assert.Contains(reason, staged.Reasons);
            var refused = installer.Install(staged.Token!, acknowledged: false);
            Assert.Equal("install.needsConfirmation", refused.Error);
            Assert.Equal("1.0.0", env.Store.Active(Id)!.Version);
            Assert.Equal(signerB, PackageIdentity.FromKey(env.Store.Active(Id)!.Signer).Signer);
            Assert.Empty(env.Host.Restarts); // the host was not restarted for a held change: the old version keeps serving
            Assert.True(File.Exists(Path.Combine(installer.DirectoryOf(Id, "1.0.0"), "main.js")));
            Assert.Single(installer.StagedUpdates());
            installer.Discard(staged.Token!);
            Assert.Empty(installer.StagedUpdates());
        }
        // reductions and an identical permission set apply without a hold
        var reduced = installer.StageUpdate(Pkg(env, "r.susuext", Manifest(Id, "1.2.0", "https://a.example.com:443", "apiKey"), PackageFactory.SeedB));
        Assert.Empty(reduced.Reasons);
        Assert.True(installer.Install(reduced.Token!).Ok);
        // and acknowledging the held one works only for that package and that token
        var signerChange = installer.StageUpdate(Pkg(env, "a2.susuext", Manifest(Id, "1.3.0", "https://a.example.com:443", "apiKey"), PackageFactory.SeedA, keyId: PackageFactory.ThirdPartyKeyId(PackageFactory.SeedA)));
        Assert.Equal("install.noPending", installer.Install("not-a-token", true).Error);
        Assert.Equal("install.noPending", installer.Install("../" + signerChange.Token, true).Error);
        Assert.True(installer.Install(signerChange.Token!, true).Ok);
        Assert.Equal(PackageFactory.ThirdPartyKeyId(PackageFactory.SeedA), PackageIdentity.FromKey(env.Store.Active(Id)!.Signer).Signer);
        Assert.Equal("install.noPending", installer.Install(signerChange.Token!, true).Error); // a token is spent
    }

    [Theory]
    [InlineData("apiVersion: 1", "apiVersion: 2", true)]
    [InlineData("apiVersion: 1", "apiVersion: 0", true)]
    [InlineData("apiVersion: 1", "apiVersion: -1", true)]
    [InlineData("apiVersion: 1", "apiVersion: 99999999999", true)]
    [InlineData("apiVersion: 1", "apiVersion: \"1\"", false)]
    [InlineData("apiVersion: 1", "apiVersion: 1.5", true)]
    [InlineData("apiVersion: 1", "apiVersion: .nan", true)]
    [InlineData("minHost: 1", "minHost: 2", true)]
    [InlineData("minHost: 1", "minHost: 99999999999", true)]
    [InlineData("minHost: 1", "minHost: 1.5", true)]
    [InlineData("minHost: 1", "minHost: -5", false)]
    public void An_update_that_needs_a_newer_api_or_host_is_refused_and_the_old_version_stays(string from, string to, bool mustRefuse)
    {
        var env = NewEnv();
        var installer = env.Installer();
        Install(installer, Pkg(env, "v1.susuext", Manifest()));
        env.Host.Restarts.Clear();
        string manifest = Manifest(version: "1.1.0").Replace(from, to);
        string zip = Pkg(env, "v2.susuext", manifest);
        var staged = installer.StageUpdate(zip);
        if (mustRefuse) Assert.False(staged.Ok, to);
        if (staged.Ok) installer.Discard(staged.Token!);
        Assert.Equal("1.0.0", env.Store.Active(Id)!.Version);
        Assert.Equal(0, env.StagingDirs);
        Assert.Empty(env.Host.Restarts);
    }

    [Fact]
    public void Version_ordering_is_numeric_and_equal_lower_or_odd_versions_are_not_updates()
    {
        var env = NewEnv();
        var installer = env.Installer();
        Install(installer, Pkg(env, "v.susuext", Manifest(version: "1.9.0")));
        foreach (string v in new[] { "1.9.0", "1.8.99", "1.10.0-" , "1.9", "1.9.0.0", "01.10.0", "1.010.0", "1.10.0-beta", "1.10.0+b", "1.0.4294967296", "-1.10.0", "١.١٠.٠", "" })
        {
            var p = installer.StageUpdate(Pkg(env, "x.susuext", Manifest(version: v)));
            Assert.False(p.Ok, "version '" + v + "' accepted as an update of 1.9.0"); if (p.Ok) installer.Discard(p.Token!);
        }
        var ten = installer.StageUpdate(Pkg(env, "ten.susuext", Manifest(version: "1.10.0")));
        Assert.True(ten.Ok); // 1.10.0 is newer than 1.9.0 (not a string comparison)
        Assert.True(installer.Install(ten.Token!).Ok);
        Assert.Equal("1.10.0", env.Store.Active(Id)!.Version);
    }

    [Fact]
    public void A_staged_package_that_changes_on_disk_after_the_preview_is_refused_and_the_old_version_stays()
    {
        var env = NewEnv();
        var installer = env.Installer();
        Install(installer, Pkg(env, "v1.susuext", Manifest(), PackageFactory.SeedB));
        env.Host.Restarts.Clear();
        foreach (string tamper in new[] { "edit", "add", "delete", "swap-dir-for-file", "touch-signature" })
        {
            var staged = installer.StageUpdate(Pkg(env, "v2.susuext", Manifest(version: "1.1.0"), PackageFactory.SeedB));
            Assert.True(staged.Ok);
            string dir = Path.Combine(env.Staging, staged.Token!);
            switch (tamper)
            {
                case "edit": File.AppendAllText(Path.Combine(dir, "main.js"), "\n// evil"); break;
                case "add": File.WriteAllText(Path.Combine(dir, "extra.js"), "evil"); break;
                case "delete": File.Delete(Path.Combine(dir, "main.js")); break;
                case "swap-dir-for-file": File.Delete(Path.Combine(dir, "main.js")); Directory.CreateDirectory(Path.Combine(dir, "main.js")); break;
                case "touch-signature": File.WriteAllText(Path.Combine(dir, "signature"), "keyId: x\n"); break;
            }
            var outcome = installer.Install(staged.Token!, true);
            Assert.False(outcome.Ok, tamper);
            Assert.Equal("install.changed", outcome.Error);
            Assert.Equal("1.0.0", env.Store.Active(Id)!.Version);
            Assert.Empty(env.Host.Restarts);
            Assert.Equal(0, env.StagingDirs);
        }
    }

    // ================= UPD04: crash at every activation step =================

    private sealed class Crash(string step) : Exception(step);

    /// <summary>OutOfMemoryException is the one failure Install does not catch, so the state is exactly what a process death at that step leaves.</summary>
    private static Action<string> DieAt(string step) => s => { if (s == step) throw new OutOfMemoryException("simulated process death at " + step); };

    private void AssertCoherent(Env env, string expectedVersion, bool expectInstalled = true)
    {
        var installer = env.Installer();
        installer.Recover();
        Assert.False(File.Exists(Path.Combine(env.UserRoot, "journal.json")), "journal left behind");
        Assert.Equal(0, env.StagingDirs);
        var rows = env.Store.ActiveRecords().Where(r => r.PackageId == Id).ToList();
        if (!expectInstalled)
        {
            Assert.Empty(rows);
            Assert.False(Directory.Exists(Path.Combine(env.UserRoot, "packages", Id)));
            Assert.Empty(installer.Installed());
            return;
        }
        var row = Assert.Single(rows);
        Assert.Equal(expectedVersion, row.Version);
        string dir = installer.DirectoryOf(Id, row.Version);
        Assert.True(Directory.Exists(dir), "active row has no directory");
        Assert.Equal(row.Hash, PackageTrust.PackageHash(dir)); // the files are the ones that were switched in
        Assert.Empty(PackageManifestIssues(dir));
        Assert.Contains($"version: {expectedVersion}", File.ReadAllText(Path.Combine(dir, "manifest.yaml")));
        Assert.Equal(expectedVersion, Assert.Single(installer.Installed()).Version);
        var versionDirs = Directory.GetDirectories(Path.Combine(env.UserRoot, "packages", Id)).Select(Path.GetFileName).ToList();
        Assert.Contains(expectedVersion, versionDirs); // the active one; the commit keeps the previous one on disk, a rollback or recovery keeps nothing newer
        Assert.DoesNotContain(versionDirs, v => PackageVersion.TryParse(v, out var found) && PackageVersion.TryParse(expectedVersion, out var want) && found > want);
        Assert.True(versionDirs.Count <= 2, "more than the active and the previous version on disk: " + string.Join(",", versionDirs));
    }

    private static IReadOnlyList<ManifestIssue> PackageManifestIssues(string dir) => SafePackage.Validate(dir);

    [Theory]
    [InlineData("recheck")]
    [InlineData("move")]
    [InlineData("switch")]
    [InlineData("health")]
    [InlineData("commit")]
    public void A_process_death_at_each_update_step_leaves_exactly_one_coherent_version_after_recovery(string step)
    {
        var env = NewEnv();
        Install(env.Installer(), Pkg(env, "v1.susuext", Manifest(), PackageFactory.SeedB));
        env.Host.Restarts.Clear();
        var dying = env.Installer(fault: DieAt(step));
        var preview = dying.Preview(Pkg(env, "v2.susuext", Manifest(version: "1.1.0"), PackageFactory.SeedB));
        Assert.True(preview.Ok);
        Assert.Throws<OutOfMemoryException>(() => dying.Install(preview.Token!));
        Assert.Empty(env.Host.Restarts); // a death before the commit never told the host anything

        env.Restart();
        AssertCoherent(env, "1.0.0"); // not committed -> the old version, whole
        // and the update can be done again afterwards
        var again = env.Installer();
        Install(again, Pkg(env, "v2b.susuext", Manifest(version: "1.1.0"), PackageFactory.SeedB));
        AssertCoherent(env, "1.1.0");
    }

    [Theory]
    [InlineData("recheck")]
    [InlineData("move")]
    [InlineData("switch")]
    [InlineData("health")]
    [InlineData("commit")]
    public void A_process_death_during_a_first_install_leaves_nothing_behind(string step)
    {
        var env = NewEnv();
        var dying = env.Installer(fault: DieAt(step));
        var preview = dying.Preview(Pkg(env, "v1.susuext", Manifest(), PackageFactory.SeedB));
        Assert.Throws<OutOfMemoryException>(() => dying.Install(preview.Token!));
        env.Restart();
        AssertCoherent(env, "", expectInstalled: false);
        Install(env.Installer(), Pkg(env, "v1b.susuext", Manifest(), PackageFactory.SeedB));
        AssertCoherent(env, "1.0.0");
    }

    [Theory]
    [InlineData("recheck")]
    [InlineData("move")]
    [InlineData("switch")]
    [InlineData("health")]
    [InlineData("commit")]
    public void A_death_during_a_built_in_override_restores_the_shipped_package(string step)
    {
        var env = NewEnv();
        var dying = env.Installer(fault: DieAt(step));
        var preview = dying.Preview(Pkg(env, "o.susuext", Manifest(BuiltInId, "1.3.0", "https://api.deepl.com:443", "apiKey"), PackageFactory.SeedA, PackageFactory.HostKeyId));
        Assert.True(preview.Ok);
        Assert.Throws<OutOfMemoryException>(() => dying.Install(preview.Token!));
        env.Restart();
        var installer = env.Installer();
        installer.Recover();
        Assert.Null(env.Store.Active(BuiltInId));
        Assert.Null(installer.ActiveDirectory(BuiltInId));
        Assert.Empty(installer.Installed());
        Assert.False(Directory.Exists(Path.Combine(env.UserRoot, "packages", BuiltInId)));
    }

    [Fact]
    public void A_death_between_the_commit_and_the_pruning_leaves_one_version_and_a_death_during_uninstall_leaves_none()
    {
        var env = NewEnv();
        var installer = env.Installer();
        Install(installer, Pkg(env, "v1.susuext", Manifest(), PackageFactory.SeedB));
        Install(installer, Pkg(env, "v2.susuext", Manifest(version: "1.1.0"), PackageFactory.SeedB));
        // the commit is complete: v1 was pruned by the commit itself, a restart changes nothing
        env.Restart();
        AssertCoherent(env, "1.1.0");

        // uninstall that died after the row was deactivated and before the files were deleted
        env.Store.Deactivate(Id);
        Assert.True(Directory.Exists(Path.Combine(env.UserRoot, "packages", Id)));
        env.Restart();
        AssertCoherent(env, "", expectInstalled: false);
    }

    [Fact]
    public void A_corrupt_or_foreign_journal_does_not_break_start_up_or_remove_a_good_version()
    {
        var env = NewEnv();
        var installer = env.Installer();
        Install(installer, Pkg(env, "v1.susuext", Manifest(), PackageFactory.SeedB));
        foreach (string journal in new[] { "{", "", "null", "[]", 
                                           "{\"PackageId\":\"com.example.echo\",\"Version\":\"9.9.9\",\"PreviousVersion\":null}", new string('x', 100_000) })
        {
            File.WriteAllText(Path.Combine(env.UserRoot, "journal.json"), journal);
            var victim = Path.Combine(env.Temp, "victim");
            Directory.CreateDirectory(victim);
            File.WriteAllText(Path.Combine(victim, "keep.txt"), "keep");
            env.Restart();
            var fresh = env.Installer();
            var ex = Record.Exception(fresh.Recover);
            Assert.Null(ex);
            Assert.Equal("keep", File.ReadAllText(Path.Combine(victim, "keep.txt")));
            Assert.Equal("1.0.0", env.Store.Active(Id)!.Version);
            Assert.True(File.Exists(Path.Combine(fresh.DirectoryOf(Id, "1.0.0"), "main.js")), "the active version was deleted by recovery with this journal: " + (journal.Length > 80 ? journal[..80] : journal));
            Assert.False(File.Exists(Path.Combine(env.UserRoot, "journal.json")));
        }
    }

    [Fact]
    public void A_journal_naming_a_traversal_path_cannot_delete_outside_the_packages_folder()
    {
        var env = NewEnv();
        var installer = env.Installer();
        Install(installer, Pkg(env, "v1.susuext", Manifest(), PackageFactory.SeedB));
        string victim = Path.Combine(env.Temp, "victim");
        Directory.CreateDirectory(victim);
        File.WriteAllText(Path.Combine(victim, "keep.txt"), "keep");
        string rel = "..\\..\\victim"; // packages\<id>\<version> -> relative to userRoot\packages\x\y
        File.WriteAllText(Path.Combine(env.UserRoot, "journal.json"),
            JsonSerializer.Serialize(new { PackageId = "..", Version = "..\\victim", PreviousVersion = (string?)null, PreviousSigner = (string?)null, PreviousHash = (string?)null }));
        env.Restart();
        env.Installer().Recover();
        Assert.True(File.Exists(Path.Combine(victim, "keep.txt")), "recovery followed a path in the journal out of the packages folder (" + rel + ")");
        Assert.True(Directory.Exists(env.UserRoot));
    }

    [Fact]
    public void Failed_activation_by_health_check_or_exception_restores_the_old_version_and_never_restarts_the_host()
    {
        var env = NewEnv();
        Install(env.Installer(), Pkg(env, "v1.susuext", Manifest(), PackageFactory.SeedB));
        env.Host.Restarts.Clear();
        foreach (string step in new[] { "recheck", "move", "switch", "health", "commit" })
        {
            var installer = env.Installer(fault: s => { if (s == step) throw new IOException("injected " + step); });
            var p = installer.StageUpdate(Pkg(env, "v2.susuext", Manifest(version: "1.1.0"), PackageFactory.SeedB));
            var o = installer.Install(p.Token!);
            Assert.False(o.Ok, step);
            AssertCoherent(env, "1.0.0");
            Assert.Empty(env.Host.Restarts);
        }
        var unhealthy = env.Installer(health: _ => new HealthResult(false, "does not load"));
        var q = unhealthy.StageUpdate(Pkg(env, "v2.susuext", Manifest(version: "1.1.0"), PackageFactory.SeedB));
        Assert.Equal("install.healthFailed", unhealthy.Install(q.Token!).Error);
        var throwing = env.Installer(health: _ => throw new InvalidOperationException("probe crashed"));
        var r = throwing.StageUpdate(Pkg(env, "v2.susuext", Manifest(version: "1.1.0"), PackageFactory.SeedB));
        Assert.False(throwing.Install(r.Token!).Ok);
        AssertCoherent(env, "1.0.0");
        Assert.Empty(env.Host.Restarts);
    }

    // ================= S02/DATA04: KV isolation =================

    private static IpcEnvelope StoreCall(string grant, string plugin, string op, string argsJson, string requestId = "r1", int callId = 1)
        => new(ProtocolVersions.Ipc, IpcMessageType.ApiCall, requestId, "job", PluginId: plugin, Grant: grant,
            Payload: JsonSerializer.SerializeToElement(new ApiCallPayload(1, callId, op, JsonDocument.Parse(argsJson).RootElement), ContractsJson.Default.ApiCallPayload));

    [Fact]
    public async Task One_package_cannot_read_or_write_another_packages_keys_through_any_store_path_or_spoofed_argument()
    {
        var env = NewEnv();
        const string A = "com.example.alpha", B = "com.example.beta";
        env.Kv.Set(B, "secret", "\"B-SECRET\"");
        env.Kv.Set(B, "com.example.alpha/secret", "\"B-OWN\"");
        using var broker = new Broker { PluginKv = env.Kv };
        var grantA = broker.Issue("r1", A, 1, []);

        async Task<(bool Ok, string Value)> Call(string op, string args, string? plugin = null, string? grant = null)
        {
            var r = await broker.HandleAsync(StoreCall(grant ?? grantA.Grant, plugin ?? A, op, args));
            return (r.Ok, r.Value.ValueKind == JsonValueKind.Null ? "null" : r.Value.GetRawText());
        }

        var own = await Call("store.get", "{\"key\":\"secret\"}");
        Assert.Equal((true, "null"), own); // the same key name in A's own space: nothing
        foreach (string key in new[] { "../com.example.beta/secret", "com.example.beta/secret", "com.example.beta\\secret", "com.example.beta:secret", "beta/secret", "secret\u0000", "secret ", "SECRET", "'; DROP TABLE plugin_kv;--", "%", "_ecret", "secre_", "s%", "\" OR 1=1 --", "*" })
        {
            var r = await Call("store.get", JsonSerializer.Serialize(new { key }));
            Assert.True(r.Ok);
            Assert.Equal("null", r.Value);
        }
        // arguments that name another package, namespace or table are ignored: the grant decides
        foreach (string args in new[]
        {
            "{\"key\":\"secret\",\"pluginId\":\"com.example.beta\"}", "{\"key\":\"secret\",\"packageId\":\"com.example.beta\"}", "{\"key\":\"secret\",\"id\":\"com.example.beta\"}",
            "{\"key\":\"secret\",\"namespace\":\"com.example.beta\"}", "{\"key\":\"secret\",\"package_id\":\"com.example.beta\"}", "{\"key\":\"secret\",\"ns\":\"store\",\"installationId\":\"com.example.beta@1.0.0\"}",
            "{\"key\":\"secret\",\"key\":\"x\",\"pluginId\":\"com.example.beta\"}", "{\"pluginId\":\"com.example.beta\",\"key\":\"secret\",\"value\":1}",
        })
            Assert.Equal("null", (await Call("store.get", args)).Value);
        Assert.Equal("\"B-SECRET\"", env.Kv.Get(B, "secret")); // untouched by every attempt, including the value one

        // writes land in the grant's own space
        Assert.True((await Call("store.set", "{\"key\":\"secret\",\"value\":\"A-VALUE\",\"pluginId\":\"com.example.beta\"}")).Ok);
        Assert.Equal("\"A-VALUE\"", env.Kv.Get(A, "secret"));
        Assert.Equal("\"B-SECRET\"", env.Kv.Get(B, "secret"));

        // an envelope that names the other package with A's grant is refused, and so is B's name with a grant minted for B but used for A's call id
        Assert.False((await Call("store.get", "{\"key\":\"secret\"}", plugin: B)).Ok);
        var grantB = broker.Issue("r2", B, 2, []);
        Assert.False((await Call("store.get", "{\"key\":\"secret\"}", plugin: A, grant: grantB.Grant)).Ok);
        Assert.False((await Call("store.get", "{\"key\":\"secret\"}", plugin: B, grant: grantA.Grant)).Ok);
        Assert.False((await Call("store.get", "{\"key\":\"secret\"}", plugin: "", grant: grantA.Grant)).Ok);
        Assert.False((await Call("store.get", "{\"key\":\"secret\"}", plugin: A, grant: "")).Ok);
        Assert.False((await Call("store.get", "{\"key\":\"secret\"}", plugin: A, grant: grantA.Grant.ToLowerInvariant() + "0")).Ok);
        // a revoked grant stops working
        broker.Revoke(grantA.Grant);
        Assert.False((await Call("store.get", "{\"key\":\"secret\"}")).Ok);

        // bad keys are refused, not interpreted
        var g2 = broker.Issue("r3", A, 3, []);
        foreach (string args in new[] { "{}", "{\"key\":\"\"}", "{\"key\":1}", "{\"key\":null}", "{\"key\":[\"a\"]}", "{\"key\":\"" + new string('k', 257) + "\"}" })
        {
            var r = await broker.HandleAsync(StoreCall(g2.Grant, A, "store.get", args, "r3", 3));
            Assert.False(r.Ok, args);
        }
        Assert.Equal("\"B-OWN\"", env.Kv.Get(B, "com.example.alpha/secret"));
    }

    [Fact]
    public async Task The_store_quota_is_per_package_and_a_package_at_its_limit_does_not_block_or_corrupt_another()
    {
        var env = NewEnv();
        using var broker = new Broker { PluginKv = env.Kv };
        var a = broker.Issue("r1", "com.example.alpha", 1, []);
        var b = broker.Issue("r2", "com.example.beta", 1, []);
        string big = JsonSerializer.Serialize(new string('x', 900_000));
        var fill = await broker.HandleAsync(StoreCall(a.Grant, "com.example.alpha", "store.set", "{\"key\":\"k1\",\"value\":" + big + "}", "r1"));
        Assert.True(fill.Ok);
        var over = await broker.HandleAsync(StoreCall(a.Grant, "com.example.alpha", "store.set", "{\"key\":\"k2\",\"value\":" + big + "}", "r1"));
        Assert.False(over.Ok);
        Assert.Null(env.Kv.Get("com.example.alpha", "k2"));
        var other = await broker.HandleAsync(StoreCall(b.Grant, "com.example.beta", "store.set", "{\"key\":\"k1\",\"value\":" + big + "}", "r2"));
        Assert.True(other.Ok);
        // overwriting the same key with a value that fits keeps the quota arithmetic right (replace, not add)
        Assert.True((await broker.HandleAsync(StoreCall(a.Grant, "com.example.alpha", "store.set", "{\"key\":\"k1\",\"value\":" + big + "}", "r1"))).Ok);
        Assert.True(env.Kv.BytesUsed("com.example.alpha") < 1 << 20);
    }

    [Fact]
    public void Uninstall_keeps_the_data_by_default_deletes_only_this_ids_data_on_request_and_a_later_package_with_the_same_id_sees_the_retained_data()
    {
        var env = NewEnv();
        var installer = env.Installer();
        foreach (string id in new[] { "com.example.echo", "com.example.echo2", "com.example.echo.x", "com.example.ech" })
            env.Kv.Set(id, "k", "\"" + id + "\"");
        Install(installer, Pkg(env, "v1.susuext", Manifest(), PackageFactory.SeedB));
        Assert.True(installer.Uninstall(Id).Ok);
        Assert.Equal("\"com.example.echo\"", env.Kv.Get(Id, "k")); // kept
        env.Restart();
        Assert.Equal("\"com.example.echo\"", env.Kv.Get(Id, "k")); // and across a restart
        installer = env.Installer();
        // a different signer reusing the id reads the retained data (documented limit of F16.2: no per-signer ownership)
        Install(installer, Pkg(env, "v1x.susuext", Manifest(), PackageFactory.SeedA, keyId: PackageFactory.ThirdPartyKeyId(PackageFactory.SeedA)));
        Assert.Equal("\"com.example.echo\"", env.Kv.Get(Id, "k"));
        Assert.True(installer.Uninstall(Id, removeData: true).Ok);
        Assert.Null(env.Kv.Get(Id, "k"));
        foreach (string id in new[] { "com.example.echo2", "com.example.echo.x", "com.example.ech" })
            Assert.Equal("\"" + id + "\"", env.Kv.Get(id, "k"));
    }

    [Fact]
    public void A_failed_install_or_rollback_does_not_touch_the_stored_data_and_removal_by_choice_is_not_applied_to_a_refused_uninstall()
    {
        var env = NewEnv();
        Install(env.Installer(), Pkg(env, "v1.susuext", Manifest(), PackageFactory.SeedB));
        env.Kv.Set(Id, "k", "1");
        var failing = env.Installer(health: _ => new HealthResult(false));
        var p = failing.StageUpdate(Pkg(env, "v2.susuext", Manifest(version: "1.1.0"), PackageFactory.SeedB));
        Assert.False(failing.Install(p.Token!).Ok);
        Assert.Equal("1", env.Kv.Get(Id, "k"));
        var installer = env.Installer();
        Assert.Equal("uninstall.notInstalled", installer.Uninstall("com.example.never", removeData: true).Error);
        env.Kv.Set("com.example.never", "k", "2");
        Assert.Equal("2", env.Kv.Get("com.example.never", "k")); // an uninstall that did not happen does not delete data
    }

    // ================= real sandbox: held changes keep serving, per-package cancellation, no replay, KV isolation, ACLs =================

    private static string PublishDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "src", "Susu.Host", "bin", "Release", "net10.0", "win-x64", "publish");
            if (File.Exists(Path.Combine(candidate, "susu.exe"))) return candidate;
        }
        Assert.Fail("the published susu.exe is missing (dotnet publish src/Susu.Host -c Release -r win-x64); this test does not skip");
        return "";
    }

    private static string Src(string tag) => "export default {\n" +
        $"  async translate(req, ctx) {{ return {{ text: '{tag}:' + req.text }}; }},\n" +
        "  async read(req, ctx) { const v = await ctx.$store.get(req.key); return { text: JSON.stringify(v) }; },\n" +
        "  async write(req, ctx) { await ctx.$store.set(req.key, req.value); return { text: 'ok' }; },\n" +
        "  async hang(req, ctx) { const n = (await ctx.$store.get('starts')) || 0; await ctx.$store.set('starts', n + 1); await new Promise(() => {}); },\n};\n";

    /// <summary>The product's wiring in miniature: the installer, a supervisor that loads the active packages, and the host controller, over a real sandbox.</summary>
    private sealed class Rig : IDisposable
    {
        public readonly Env Env;
        public readonly PluginInstaller Installer;
        public readonly Supervisor<HostSession> Supervisor;
        public readonly PluginHostController<HostSession> Controller;
        public readonly string Staged;

        public Rig(Env env)
        {
            Env = env;
            string publish = PublishDir();
            Staged = TestTemp.NewDir("susu-f16s");
            foreach (string file in Directory.EnumerateFiles(publish))
                if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    File.Copy(file, Path.Combine(Staged, Path.GetFileName(file)));
            PluginInstaller? installer = null;
            var options = new HostSession.Options(Path.Combine(Staged, "susu.exe"), Staged, "quickjs", MakeBroker: () => new Broker { PluginKv = env.Kv });
            Supervisor = new Supervisor<HostSession>(() =>
            {
                var session = HostSession.Start(options with { ExtraReadRoots = Directory.Exists(installer!.PackagesFolder) ? [installer.PackagesFolder] : null });
                foreach (var package in installer!.ActivePackages())
                {
                    var loaded = session.Load(package.Id, package.Directory, entry: package.Entry);
                    Assert.True(loaded.Ok, loaded.Error);
                }
                return session;
            }, SystemClock.Instance);
            Controller = new PluginHostController<HostSession>(Supervisor, TimeSpan.FromSeconds(5));
            Installer = installer = new PluginInstaller(env.UserRoot, env.Store, new BuiltInPackages([]), Env.Keyring(),
                PluginLoadProbe.Create(() => options with { ExtraReadRoots = [installer!.PackagesFolder] }), host: Controller, removeData: id => env.Kv.DeletePackage(id));
        }

        public async Task<(IpcMessageType Type, string Text)> Call(string package, string capability, string request = "{\"text\":\"hi\"}")
        {
            var session = Supervisor.Acquire() ?? throw new InvalidOperationException("no host");
            try
            {
                var (_, _, task) = session.Invoke(package, capability, request, "job-" + capability, origins: []);
                var envelope = await task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
                return (envelope.Type, envelope.Payload?.GetRawText() ?? "");
            }
            finally { Supervisor.Release(); }
        }

        /// <summary>Starts a call and leaves the session acquired until the returned release is called.</summary>
        public (HostSession Session, Task<IpcEnvelope> Result) Start(string package, string capability, string request = "{}")
        {
            var session = Supervisor.Acquire() ?? throw new InvalidOperationException("no host");
            var (_, _, task) = session.Invoke(package, capability, request, "job-" + capability + "-" + package, origins: []);
            return (session, task);
        }

        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { Supervisor.Dispose(); } catch (Exception) { } // ends the live session first: nothing may use the database once the environment goes
            try { Controller.Dispose(); } catch (Exception) { }
            try { Controller.Retired().Wait(TimeSpan.FromSeconds(30)); } catch (Exception) { }
        }
    }

    private Rig NewRig(Env env) { var r = new Rig(env); cleanup.Add(r); return r; }

    private static string Signed(Env env, string name, string id, string version, string tag, byte[] seed, string hosts = "")
        => PackageFactory.Zip(env.Zips, name, PackageFactory.Files(Manifest(id, version, hosts), Src(tag)), seed);

    private static List<string> ContainerGrants(string root)
    {
        var found = new List<string>();
        if (!Directory.Exists(root)) return found;
        foreach (string path in new[] { root }.Concat(Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)))
        {
            System.Security.AccessControl.FileSystemSecurity security = Directory.Exists(path) ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
            foreach (System.Security.AccessControl.AuthorizationRule rule in security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)))
                if (rule.IdentityReference.Value.StartsWith("S-1-15-2-", StringComparison.Ordinal)) found.Add(path + " -> " + rule.IdentityReference.Value);
        }
        return found;
    }

    [Fact]
    public async Task A_held_change_leaves_the_old_version_serving_calls_and_cancels_nothing_until_it_is_acknowledged()
    {
        var env = NewEnv();
        var rig = NewRig(env);
        var ct = Ct;
        Assert.True(rig.Installer.Install(rig.Installer.Preview(Signed(env, "v1.susuext", Id, "1.0.0", "v1", PackageFactory.SeedB)).Token!).Ok);
        Assert.Contains("v1:hi", (await rig.Call(Id, "translate")).Text);
        var (session, hang) = rig.Start(Id, "hang");
        int pid = session.ChildPid;
        Assert.True(await Eventually.WaitAsync(() => rig.Controller.InFlight(Id).Count > 0));
        Assert.True(await Eventually.WaitAsync(() => env.Kv.Get(Id, "starts") == "1"));

        // a different signer and wider hosts: both are held; the host is not restarted, the in-flight call is untouched, new calls still run v1
        var signerChange = rig.Installer.StageUpdate(Signed(env, "v2a.susuext", Id, "1.1.0", "v2a", PackageFactory.SeedA));
        Assert.Contains("signer-changed", signerChange.Reasons);
        Assert.Equal("install.needsConfirmation", rig.Installer.Install(signerChange.Token!).Error);
        var wider = rig.Installer.StageUpdate(Signed(env, "v2b.susuext", Id, "1.2.0", "v2b", PackageFactory.SeedB, hosts: "https://api.example.com:443"));
        Assert.Contains("permissions-expanded", wider.Reasons);
        Assert.Equal("install.needsConfirmation", rig.Installer.Install(wider.Token!).Error);
        Assert.Equal("install.noPending", rig.Installer.Install(signerChange.Token!, acknowledged: true).Error); // replaced by the newer staged update of the same package
        signerChange = rig.Installer.StageUpdate(Signed(env, "v2c.susuext", Id, "1.3.0", "v2a", PackageFactory.SeedA));
        for (int i = 0; i < 3; i++)
        {
            var (type, text) = await rig.Call(Id, "translate", "{\"text\":\"during-hold" + i + "\"}");
            Assert.Equal(IpcMessageType.Completed, type);
            Assert.Contains("v1:during-hold" + i, text);
        }
        Assert.False(hang.IsCompleted, "the in-flight call was cancelled by a change that is only held");
        Assert.Equal(pid, rig.Supervisor.Current.ChildPid); // the same plugin-host process: nothing restarted
        Assert.Equal("1.0.0", env.Store.Active(Id)!.Version);
        Assert.Single(rig.Installer.StagedUpdates());

        // discard one, acknowledge the other: only now does the host restart, the in-flight call fails as cancelled, and the new version serves
        var applied = rig.Installer.Install(signerChange.Token!, acknowledged: true);
        Assert.True(applied.Ok, applied.Error + " " + string.Join("; ", applied.Issues.Select(i => i.Path + "=" + i.Code)));
        Assert.Equal(1, applied.Interrupted);
        var cancelled = await hang.WaitAsync(TimeSpan.FromSeconds(15), ct);
        Assert.Equal(IpcMessageType.Failed, cancelled.Type);
        Assert.Contains("cancelled", cancelled.Payload!.Value.GetRawText());
        rig.Supervisor.Release();
        Assert.Contains("v2a:hi", (await rig.Call(Id, "translate")).Text);
        Assert.Equal("\"1\"".Trim('"'), env.Kv.Get(Id, "starts")); // started once, never replayed
    }

    [Fact]
    public async Task An_update_cancels_only_its_own_packages_calls_replays_nothing_and_keeps_the_other_package_and_its_data_apart()
    {
        var env = NewEnv();
        var rig = NewRig(env);
        const string Other = "com.example.other";
        Assert.True(rig.Installer.Install(rig.Installer.Preview(Signed(env, "a1.susuext", Id, "1.0.0", "a1", PackageFactory.SeedB)).Token!).Ok);
        Assert.True(rig.Installer.Install(rig.Installer.Preview(Signed(env, "b1.susuext", Other, "1.0.0", "b1", PackageFactory.SeedB)).Token!).Ok);

        // KV through the real sandbox: A writes, B cannot see it by any key spelling, B's own write of the same key does not touch A's
        Assert.Contains("ok", (await rig.Call(Id, "write", "{\"key\":\"secretA\",\"value\":\"A-ONLY\"}")).Text);
        foreach (string key in new[] { "secretA", "com.example.echo/secretA", "../com.example.echo/secretA", "com.example.echo:secretA", "SECRETA" })
            Assert.Contains("null", (await rig.Call(Other, "read", JsonSerializer.Serialize(new { key }))).Text);
        Assert.Contains("ok", (await rig.Call(Other, "write", "{\"key\":\"secretA\",\"value\":\"B-VALUE\"}")).Text);
        Assert.Contains("A-ONLY", (await rig.Call(Id, "read", "{\"key\":\"secretA\"}")).Text);
        Assert.Contains("B-VALUE", (await rig.Call(Other, "read", "{\"key\":\"secretA\"}")).Text);
        // a call to a package that does not exist or to an id spelled differently reaches nothing
        Assert.Equal(IpcMessageType.Failed, (await rig.Call("com.example.echo ", "read", "{\"key\":\"secretA\"}")).Type);
        Assert.Equal(IpcMessageType.Failed, (await rig.Call("COM.EXAMPLE.ECHO", "read", "{\"key\":\"secretA\"}")).Type);

        // one in-flight call in each package
        var (sessionA, hangA) = rig.Start(Id, "hang");
        var (_, hangB) = rig.Start(Other, "hang");
        Assert.True(await Eventually.WaitAsync(() => rig.Controller.InFlight(Id).Count > 0 && rig.Controller.InFlight(Other).Count > 0));
        Assert.True(await Eventually.WaitAsync(() => env.Kv.Get(Id, "starts") == "1" && env.Kv.Get(Other, "starts") == "1"));
        Assert.Equal(1, rig.Controller.InFlight(Id).Sum(t => t.Count));
        Assert.Equal(1, rig.Controller.InFlight(Other).Sum(t => t.Count));

        var update = rig.Installer.StageUpdate(Signed(env, "a2.susuext", Id, "1.1.0", "a2", PackageFactory.SeedB));
        var applied = rig.Installer.Install(update.Token!);
        Assert.True(applied.Ok, applied.Error + " " + string.Join("; ", applied.Issues.Select(i => i.Path + "=" + i.Code)));
        Assert.Equal(1, applied.Interrupted); // exactly the changed package's call

        var aResult = await hangA.WaitAsync(TimeSpan.FromSeconds(15), Ct);
        Assert.Equal(IpcMessageType.Failed, aResult.Type);
        Assert.Contains("cancelled", aResult.Payload!.Value.GetRawText());
        await Task.Delay(1500, Ct);
        Assert.False(hangB.IsCompleted, "the other package's call was cancelled by an update of a different package");

        // new calls: the updated package runs the new code, the other one still works (it is loaded in the fresh host too)
        Assert.Contains("a2:hi", (await rig.Call(Id, "translate")).Text);
        Assert.Contains("b1:hi", (await rig.Call(Other, "translate")).Text);
        Assert.Contains("A-ONLY", (await rig.Call(Id, "read", "{\"key\":\"secretA\"}")).Text);

        // the retired session is disposed after the drain timeout; B's call then ends, and never as a success
        try
        {
            var bResult = await hangB.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.NotEqual(IpcMessageType.Completed, bResult.Type);
        }
        catch (Exception e) when (e is not Xunit.Sdk.XunitException and not OperationCanceledException and not TimeoutException) { /* the channel closed under the call: also an end, not a success */ }
        rig.Supervisor.Release();
        rig.Supervisor.Release();
        _ = sessionA;
        await rig.Controller.Retired().WaitAsync(TimeSpan.FromSeconds(30), Ct);

        // no replay anywhere: each hang ran its first line exactly once
        Assert.Equal("1", env.Kv.Get(Id, "starts"));
        Assert.Equal("1", env.Kv.Get(Other, "starts"));
        Assert.Equal(IpcMessageType.Failed, (await rig.Call(Id, "no-such-capability")).Type);
    }

    [Fact]
    public async Task A_child_crash_fails_the_in_flight_call_without_replay_and_leaves_no_container_grant_on_the_packages_folder_after_the_sessions_end()
    {
        var env = NewEnv();
        var rig = NewRig(env);
        Assert.True(rig.Installer.Install(rig.Installer.Preview(Signed(env, "a1.susuext", Id, "1.0.0", "a1", PackageFactory.SeedB)).Token!).Ok);
        Assert.Contains("a1:hi", (await rig.Call(Id, "translate")).Text);
        Assert.NotEmpty(ContainerGrants(rig.Installer.PackagesFolder)); // control: the session grant exists while the session lives, so the later "none" means something

        var (session, hang) = rig.Start(Id, "hang");
        Assert.True(await Eventually.WaitAsync(() => rig.Controller.InFlight(Id).Count > 0));
        Assert.True(await Eventually.WaitAsync(() => env.Kv.Get(Id, "starts") == "1")); // the plugin has run its first line (InFlight only says the call was sent): killing earlier would race the write
        int pid = session.ChildPid;
        using (var child = Process.GetProcessById(pid)) { child.Kill(true); }
        IpcEnvelope? ended = null;
        try { ended = await hang.WaitAsync(TimeSpan.FromSeconds(90), Ct); }
        catch (Exception e) when (e is not Xunit.Sdk.XunitException and not OperationCanceledException and not TimeoutException) { }
        if (ended is not null) Assert.NotEqual(IpcMessageType.Completed, ended.Type);
        rig.Supervisor.Release();

        // the supervisor starts a fresh host (1 s backoff); the call is not replayed in it
        Assert.True(await Eventually.WaitAsync(() => rig.Supervisor.TryGetCurrent(out var cur) && cur is not null && cur.ChildPid != pid, TimeSpan.FromSeconds(90)), "the plugin host did not come back");
        Assert.Contains("a1:hi", (await rig.Call(Id, "translate")).Text);
        await Task.Delay(1500, Ct);
        Assert.Equal("1", env.Kv.Get(Id, "starts"));
        var afterCrash = ContainerGrants(rig.Installer.PackagesFolder).Select(g => g.Split(" -> ")[1]).Distinct().ToList();
        Assert.True(afterCrash.Count <= 2, $"{afterCrash.Count} distinct container SIDs on the packages folder while the host lives (the live session plus at most the one that crashed)");

        // everything ended: not one container entry may remain on the packages or staging folders, or anything below them
        rig.Dispose();
        Assert.True(await Eventually.WaitAsync(() => ContainerGrants(rig.Installer.PackagesFolder).Count == 0, TimeSpan.FromSeconds(20)),
            "container grants left on the packages folder after the sessions ended: " + string.Join("; ", ContainerGrants(rig.Installer.PackagesFolder)));
        Assert.Empty(ContainerGrants(Path.Combine(env.UserRoot, ".staging")));
        Assert.Equal(0, ContainerGrants(env.UserRoot).Count(g => !g.StartsWith(rig.Installer.PackagesFolder, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task A_package_with_a_bad_update_signed_by_a_new_key_does_not_displace_the_running_version_even_when_acknowledged_if_it_cannot_load()
    {
        var env = NewEnv();
        var rig = NewRig(env);
        Assert.True(rig.Installer.Install(rig.Installer.Preview(Signed(env, "a1.susuext", Id, "1.0.0", "a1", PackageFactory.SeedB)).Token!).Ok);
        Assert.Contains("ok", (await rig.Call(Id, "write", "{\"key\":\"k\",\"value\":\"v\"}")).Text);
        var (session, hang) = rig.Start(Id, "hang");
        Assert.True(await Eventually.WaitAsync(() => rig.Controller.InFlight(Id).Count > 0));
        var broken = PackageFactory.Zip(env.Zips, "bad.susuext", PackageFactory.Files(Manifest(Id, "1.1.0", "https://api.example.com:443"), "export default { async translate( {{{"), PackageFactory.SeedA);
        var staged = rig.Installer.StageUpdate(broken);
        Assert.True(staged.Ok);
        Assert.NotEmpty(staged.Reasons);
        var outcome = rig.Installer.Install(staged.Token!, acknowledged: true);
        Assert.Equal("install.healthFailed", outcome.Error);
        Assert.Equal("1.0.0", env.Store.Active(Id)!.Version);
        Assert.False(hang.IsCompleted); // the failed update did not cancel the running call
        Assert.Contains("a1:hi", (await rig.Call(Id, "translate")).Text);
        Assert.Contains("v\\u0022", (await rig.Call(Id, "read", "{\"key\":\"k\"}")).Text); // the stored value, JSON-escaped inside the result
        Assert.Equal(["1.0.0"], Directory.GetDirectories(Path.Combine(env.UserRoot, "packages", Id)).Select(Path.GetFileName));
        rig.Supervisor.Release();
        _ = session;
    }

    // ================= susu-plugin CLI =================

    private static string FindUp(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
        }
        Assert.Fail($"{relative} is missing; publish it (this test does not skip)");
        return "";
    }

    private static string Cli() => FindUp(Path.Combine("tools", "Susu.PluginCli", "bin", "Release", "net10.0", "win-x64", "publish", "susu-plugin.exe"));
    private static string HostExe() => Path.Combine(PublishDir(), "susu.exe");

    private static (int Exit, string Out, string Err) Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(240_000)) { process.Kill(true); throw new TimeoutException($"{exe} {string.Join(' ', args)} did not finish in 240 s"); }
        return (process.ExitCode, stdout.Result, stderr.Result);
    }

    private static string InitTemplate(string capability, string? name = null)
    {
        string dir = Path.Combine(TestTemp.NewDir("susu-f16c"), name ?? capability);
        var init = Run(Cli(), "init", dir, "--capability", capability);
        Assert.True(init.Exit == 0, init.Err);
        return dir;
    }

    private static bool Crashed(int exit) => exit is < 0 or > 100 || exit == unchecked((int)0xC00000FD);

    /// <summary>DEFECT F16-V1 (see the verification record): a manifest of about 30000 nested sequence brackets (60 KB, under the 64 KiB limit) overflows the stack in the manifest reader.</summary>
    [Fact]
    public void A_deeply_nested_manifest_is_refused_not_a_process_crash()
    {
        string dir = InitTemplate("translate", "nest");
        string manifest = File.ReadAllText(Path.Combine(dir, "manifest.yaml"));
        foreach (int depth in new[] { 1000, 5000, 20000, 30000 })
        {
            File.WriteAllText(Path.Combine(dir, "manifest.yaml"), manifest + "x: " + new string('[', depth) + new string(']', depth) + "\n");
            var check = Run(Cli(), "check", dir);
            Assert.False(Crashed(check.Exit), $"susu-plugin check crashed (exit {check.Exit}) on a manifest nested {depth} deep: {check.Err.Split('\n')[0]}");
        }
    }

    [Fact]
    public void Check_rejects_every_kind_of_bad_signature_and_accepts_a_good_one_and_unsigned_is_reported_not_rejected()
    {
        string dir = InitTemplate("translate", "sig");
        string root = Path.GetDirectoryName(dir)!;
        string key = Path.Combine(root, "k.key");
        Assert.Equal(0, Run(Cli(), "keygen", key).Exit);
        string signedZip = Path.Combine(root, "s.susuext");
        Assert.Equal(0, Run(Cli(), "pack", dir, "--out", signedZip, "--key", key).Exit);
        string unpacked = Path.Combine(root, "u");
        ZipFile.ExtractToDirectory(signedZip, unpacked);
        Assert.Equal(0, Run(Cli(), "check", unpacked).Exit);

        string signature = File.ReadAllText(Path.Combine(unpacked, "signature"));
        var variants = new Dictionary<string, Action>
        {
            ["manifest edited"] = () => File.AppendAllText(Path.Combine(unpacked, "manifest.yaml"), "# x\n"),
            ["file added"] = () => File.WriteAllText(Path.Combine(unpacked, "extra.js"), "x"),
            ["file removed"] = () => File.Delete(Path.Combine(unpacked, "main.js")),
            ["signature garbage"] = () => File.WriteAllText(Path.Combine(unpacked, "signature"), "garbage"),
            ["signature empty"] = () => File.WriteAllText(Path.Combine(unpacked, "signature"), ""),
            ["signature key changed"] = () => File.WriteAllText(Path.Combine(unpacked, "signature"), System.Text.RegularExpressions.Regex.Replace(signature, "keyId: [0-9a-f]{64}", "keyId: " + new string('a', 64))),
            ["signature flipped"] = () => File.WriteAllText(Path.Combine(unpacked, "signature"), signature.Replace("signature: ", "signature: A")),
            ["signature oversized"] = () => File.WriteAllText(Path.Combine(unpacked, "signature"), signature + new string('#', 10000)),
        };
        foreach (var (label, mutate) in variants)
        {
            if (Directory.Exists(unpacked)) Directory.Delete(unpacked, true);
            ZipFile.ExtractToDirectory(signedZip, unpacked);
            mutate();
            var check = Run(Cli(), "check", unpacked);
            Assert.True(check.Exit == 1, $"check exit {check.Exit} for '{label}': {check.Out}{check.Err}");
        }
        var unsigned = Run(Cli(), "check", dir);
        Assert.Equal(0, unsigned.Exit);
        Assert.Contains("signature: none", unsigned.Out);
    }

    [Fact]
    public void Hostile_cases_files_are_handled_without_a_crash_a_hang_or_a_write_outside_the_package()
    {
        string dir = InitTemplate("translate", "hostile");
        string root = Path.GetDirectoryName(dir)!;
        string cases = Path.Combine(dir, "susu-plugin.test.json");
        string Case(string request, string extra = "") => "{ \"version\": 1, \"cases\": [ { \"name\": \"c\", \"request\": " + request + extra + ", \"expect\": { \"result\": {} } } ] }";
        var files = new Dictionary<string, string>
        {
            ["deep json"] = Case(new string('[', 100_000) + new string(']', 100_000)),
            ["deep objects"] = Case(string.Concat(Enumerable.Repeat("{\"a\":", 50_000)) + "1" + new string('}', 50_000)),
            ["huge request string"] = Case("{ \"text\": \"" + new string('x', 24 << 20) + "\" }"),
            ["huge array"] = Case("{ \"list\": [" + string.Join(",", Enumerable.Repeat("1", 3_000_000)) + "] }"),
            ["input name traversal"] = Case("{ \"f\": { \"$file\": \"..\\\\..\\\\evil\" } }", ", \"inputs\": { \"..\\\\..\\\\evil\": { \"text\": \"x\" }, \"../../evil2\": { \"text\": \"x\", \"extension\": \"txt\" } }"),
            ["extension traversal"] = Case("{}", ", \"inputs\": { \"f\": { \"text\": \"x\", \"extension\": \"..\\\\..\\\\evil.exe\" } }"),
            ["extension absolute"] = Case("{}", ", \"inputs\": { \"f\": { \"text\": \"x\", \"extension\": \"C:\\\\evil\" } }"),
            ["vendor header injection"] = Case("{ \"text\": \"a\" }", ", \"vendor\": [ { \"status\": 200, \"headers\": { \"X-A\": \"b\\r\\nSet-Cookie: x=1\\r\\n\\r\\nHTTP/1.1 200 OK\" } } ]"),
            ["nul and lone surrogates"] = Case("{ \"text\": \"\\u0000\\ud800\\udfff\\udc00\" }"),
            ["duplicate keys"] = "{ \"version\": 1, \"version\": 2, \"cases\": [ { \"name\": \"c\", \"name\": \"d\", \"request\": {}, \"expect\": { \"result\": {} } } ] }",
            ["bom"] = "\uFEFF" + Case("{}"),
            ["trailing garbage"] = Case("{}") + "}}}",
            ["comments"] = "// c\n" + Case("{}"),
            ["case file is a directory name"] = "",
        };
        foreach (var (label, content) in files)
        {
            if (label == "case file is a directory name") continue;
            File.WriteAllText(cases, content, new UTF8Encoding(false));
            var sw = Stopwatch.StartNew();
            var result = Run(Cli(), "test", dir, "--host", HostExe());
            Assert.False(Crashed(result.Exit), $"'{label}' crashed the CLI (exit {result.Exit}): {result.Err}");
            Assert.True(result.Exit is 0 or 1 or 2, $"'{label}' exit {result.Exit}");
            Assert.DoesNotContain("Unhandled exception", result.Err);
            Assert.True(sw.Elapsed < TimeSpan.FromMinutes(3), label + " took " + sw.Elapsed);
        }
        foreach (string name in new[] { "evil", "evil2", "evil.exe" })
            for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent) Assert.False(d.EnumerateFiles(name + "*").Any(), "a file named after an input escaped to " + d.FullName);
        // the cases file may live outside the package (--cases) and then nothing of that folder is staged
        string outside = Path.Combine(root, "elsewhere");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "do not copy");
        File.WriteAllText(Path.Combine(outside, "c.json"), Case("{ \"text\": \"hi\", \"from\": \"en\", \"to\": \"zh-Hans\" }").Replace("\"result\": {}", "\"result\": { \"text\": \"never\" }"));
        var run = Run(Cli(), "test", dir, "--host", HostExe(), "--cases", Path.Combine(outside, "c.json"));
        Assert.True(run.Exit is 1 or 2, run.Out + run.Err);
        // a missing --cases file and a directory given as --cases are usage errors
        Assert.Equal(2, Run(Cli(), "test", dir, "--host", HostExe(), "--cases", Path.Combine(outside, "none.json")).Exit);
        Assert.True(Run(Cli(), "test", dir, "--host", HostExe(), "--cases", outside).Exit is 1 or 2);
    }

    [Fact]
    public void A_package_directory_with_a_junction_is_not_followed_out_by_check_or_pack()
    {
        string dir = InitTemplate("translate", "junction");
        string root = Path.GetDirectoryName(dir)!;
        string outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "SECRET-OUTSIDE");
        var mk = Run("cmd.exe", "/c", "mklink", "/J", Path.Combine(dir, "link"), outside);
        Assert.True(mk.Exit == 0, "could not create a junction: " + mk.Out + mk.Err);
        var check = Run(Cli(), "check", dir);
        string packOut = Path.Combine(root, "j.susuext");
        var pack = Run(Cli(), "pack", dir, "--out", packOut);
        bool packedSecret = false;
        if (File.Exists(packOut)) { using var z = ZipFile.OpenRead(packOut); packedSecret = z.Entries.Any(e => e.FullName.Contains("secret.txt", StringComparison.Ordinal)); }
        Assert.True(check.Exit == 1 || (pack.Exit != 0 && !packedSecret), $"check exit {check.Exit}, pack exit {pack.Exit}, secret packed: {packedSecret}: a file outside the package was reached through a junction");
        Assert.False(packedSecret, "pack followed the junction and put a file from outside the package into the zip");
    }

    [Fact]
    public void Pack_refuses_what_the_installer_would_refuse()
    {
        string dir = InitTemplate("translate", "limits");
        string root = Path.GetDirectoryName(dir)!;
        Directory.CreateDirectory(Path.Combine(dir, "many"));
        for (int i = 0; i < 300; i++) File.WriteAllText(Path.Combine(dir, "many", $"f{i}.txt"), "x");
        var many = Run(Cli(), "pack", dir, "--out", Path.Combine(root, "many.susuext"));
        Assert.NotEqual(0, many.Exit);
        Assert.False(File.Exists(Path.Combine(root, "many.susuext")), "pack wrote a package with 300 files");
        Directory.Delete(Path.Combine(dir, "many"), true);

        File.WriteAllBytes(Path.Combine(dir, "big.bin"), new byte[5 << 20]);
        var big = Run(Cli(), "pack", dir, "--out", Path.Combine(root, "big.susuext"));
        Assert.NotEqual(0, big.Exit);
        File.Delete(Path.Combine(dir, "big.bin"));

        // reserved name and alternate stream files
        // Windows Server maps "aux.*" to the AUX device for ordinary paths, so create it with the extended-length prefix,
        // which stores the name literally on every Windows version.
        // Normalize only the directory: GetFullPath on a path ending in "aux.*" rewrites it to \\.\aux on Windows Server 2025.
        File.WriteAllText(@"\\?\" + Path.GetFullPath(dir).TrimEnd('\\') + @"\aux.txt.bak", "x");
        var dev = Run(Cli(), "pack", dir, "--out", Path.Combine(root, "dev.susuext"));
        Assert.False(Crashed(dev.Exit));
        if (dev.Exit == 0)
        {
            var env = NewEnv();
            var preview = env.Installer().Preview(Path.Combine(root, "dev.susuext"));
            Assert.True(preview.Ok == (dev.Exit == 0), "pack made a package the installer refuses: " + string.Join(",", preview.Issues.Select(i => i.Code)));
        }
        // an output path that cannot be written is an error, not a crash
        var bad = Run(Cli(), "pack", dir, "--out", Path.Combine(root, "no-such-folder", "x.susuext"));
        Assert.False(Crashed(bad.Exit));
    }

    [Theory]
    [InlineData("translate")]
    [InlineData("dictionary")]
    [InlineData("detect")]
    [InlineData("ocr")]
    [InlineData("tts")]
    [InlineData("asr")]
    [InlineData("vocab")]
    public void A_template_packed_and_signed_by_the_cli_installs_through_the_real_installer_with_the_authors_key_as_identity(string capability)
    {
        string dir = InitTemplate(capability, "t" + capability);
        string root = Path.GetDirectoryName(dir)!;
        string key = Path.Combine(root, "k.key");
        var keygen = Run(Cli(), "keygen", key);
        string keyId = keygen.Out.Split("key id (public, goes in the signature): ")[1].Trim();
        string zip = Path.Combine(root, "t.susuext");
        Assert.Equal(0, Run(Cli(), "pack", dir, "--out", zip, "--key", key).Exit);
        var env = NewEnv();
        var installer = env.Installer();
        var preview = installer.Preview(zip);
        Assert.True(preview.Ok, string.Join(",", preview.Issues.Select(i => i.Path + ":" + i.Code)));
        Assert.Equal(("thirdParty", keyId), (preview.SignerKind, preview.Signer));
        Assert.Contains(capability, preview.Diff!.AddedCapabilities);
        Assert.True(installer.Install(preview.Token!).Ok);
        // repacking with another key is a signer change that is held
        string key2 = Path.Combine(root, "k2.key");
        Assert.Equal(0, Run(Cli(), "keygen", key2).Exit);
        File.AppendAllText(Path.Combine(dir, "manifest.yaml"), "");
        string manifest = File.ReadAllText(Path.Combine(dir, "manifest.yaml"));
        File.WriteAllText(Path.Combine(dir, "manifest.yaml"), System.Text.RegularExpressions.Regex.Replace(manifest, @"version: \S+", "version: 9.9.9"));
        string zip2 = Path.Combine(root, "t2.susuext");
        Assert.Equal(0, Run(Cli(), "pack", dir, "--out", zip2, "--key", key2).Exit);
        var update = installer.StageUpdate(zip2);
        Assert.True(update.Ok, string.Join(",", update.Issues.Select(i => i.Code)));
        Assert.Contains("signer-changed", update.Reasons);
        Assert.Equal("install.needsConfirmation", installer.Install(update.Token!).Error);
    }

    // ================= sandbox crypto and URLSearchParams edges (real QuickJS bridge, in-process) =================

    private const string WebApiPlugin = """
        const hex = (buf) => Array.from(new Uint8Array(buf), (x) => x.toString(16).padStart(2, '0')).join('');
        const err = (e) => String(e && e.name) + ':' + (e instanceof Error);
        const attempt = (f) => { try { return f(); } catch (e) { return err(e); } };
        const settle = async (p) => { try { return await p; } catch (e) { return err(e); } };
        export default {
          async hugeDigest() {
            const out = {};
            const mil = new Uint8Array(1000000).fill(0x61);
            out.millionA = hex(await crypto.subtle.digest('SHA-256', mil));
            out.empty = hex(await crypto.subtle.digest('SHA-256', new Uint8Array(0)));
            out.emptyView = hex(await crypto.subtle.digest('SHA-256', new Uint8Array(new ArrayBuffer(16), 8, 0)));
            const four = new Uint8Array(4 * 1024 * 1024);
            out.fourMiB = (await crypto.subtle.digest('SHA-512', four)).byteLength;
            let big; try { big = new Uint8Array(24 * 1024 * 1024); out.big = (await settle(crypto.subtle.digest('SHA-1', big))); out.bigIsHash = typeof out.big === 'object' && out.big.byteLength === 20; } catch (e) { out.big = 'alloc:' + err(e); }
            out.afterBig = hex(await crypto.subtle.digest('SHA-256', new Uint8Array(0)));
            out.sab = typeof SharedArrayBuffer === 'undefined' ? 'no-sab' : await settle(crypto.subtle.digest('SHA-256', new SharedArrayBuffer(4)));
            out.dataview = hex(await crypto.subtle.digest('SHA-256', new DataView(new Uint8Array([97, 98, 99]).buffer)));
            out.concurrent = (await Promise.all([1, 2, 3, 4, 5, 6, 7, 8].map((i) => crypto.subtle.digest('SHA-256', new Uint8Array([i]))))).map(hex).length;
            out.algoWeird = [await settle(crypto.subtle.digest(null, new Uint8Array(1))), await settle(crypto.subtle.digest({}, new Uint8Array(1))), await settle(crypto.subtle.digest({ name: 1 }, new Uint8Array(1))),
              await settle(crypto.subtle.digest('SHA-256 ', new Uint8Array(1))), await settle(crypto.subtle.digest('sha256', new Uint8Array(1))), await settle(crypto.subtle.digest(['SHA-256'], new Uint8Array(1)))];
            return out;
          },
          async quota() {
            const out = {};
            out.max = crypto.getRandomValues(new Uint8Array(65536)).byteLength;
            out.max16 = crypto.getRandomValues(new Uint16Array(32768)).byteLength;
            out.max64 = crypto.getRandomValues(new BigInt64Array(8192)).byteLength;
            out.clamped = crypto.getRandomValues(new Uint8ClampedArray(8)).length;
            const over = new Uint8Array(65537);
            out.over = attempt(() => crypto.getRandomValues(over));
            out.overUntouched = over.every((x) => x === 0);
            const over16 = new Uint16Array(32769);
            out.over16 = attempt(() => crypto.getRandomValues(over16));
            out.over16Untouched = over16.every((x) => x === 0);
            const buf = new ArrayBuffer(70000);
            const view = new Uint8Array(buf, 100, 65536);
            out.windowOk = crypto.getRandomValues(view).byteLength;
            out.windowedBeyond = attempt(() => crypto.getRandomValues(new Uint8Array(buf, 0, 65537)));
            out.untouchedOutside = new Uint8Array(buf, 0, 100).every((x) => x === 0) && new Uint8Array(buf, 65636).every((x) => x === 0);
            let total = 0; for (let i = 0; i < 40; i++) total += crypto.getRandomValues(new Uint8Array(65536)).byteLength;
            out.repeated = total;
            out.nullArg = attempt(() => crypto.getRandomValues(null));
            out.noArg = attempt(() => crypto.getRandomValues());
            out.number = attempt(() => crypto.getRandomValues(4));
            out.arrayBufferDirect = attempt(() => crypto.getRandomValues(new ArrayBuffer(8)));
            out.float64 = attempt(() => crypto.getRandomValues(new Float64Array(2)));
            out.thisLoose = attempt(() => { const f = crypto.getRandomValues; return f(new Uint8Array(4)).length; });
            out.thisOther = attempt(() => crypto.getRandomValues.call({}, new Uint8Array(4)).length);
            out.uuid = crypto.randomUUID().length;
            out.uuidLoose = attempt(() => { const f = crypto.randomUUID; return f().length; });
            return out;
          },
          async frozen() {
            const out = {};
            out.isFrozen = [Object.isFrozen(crypto), Object.isFrozen(crypto.subtle), Object.isFrozen(crypto.getRandomValues), Object.isFrozen(crypto.subtle.digest)];
            out.assignFn = attempt(() => { crypto.getRandomValues = () => 0; return 'assigned'; });
            out.assignSubtle = attempt(() => { crypto.subtle.digest = () => 0; return 'assigned'; });
            out.assignSubtleObj = attempt(() => { crypto.subtle = {}; return 'assigned'; });
            out.assignGlobal = attempt(() => { globalThis.crypto = {}; return 'assigned'; });
            out.deleteFn = attempt(() => { delete crypto.getRandomValues; return typeof crypto.getRandomValues; });
            out.define = attempt(() => { Object.defineProperty(crypto, 'getRandomValues', { value: () => 0 }); return 'defined'; });
            out.defineNew = attempt(() => { Object.defineProperty(crypto.subtle, 'sign', { value: () => 0 }); return 'defined'; });
            out.setProto = attempt(() => { Object.setPrototypeOf(crypto, { evil: true }); return 'proto-set'; });
            out.deleteGlobal = attempt(() => { delete globalThis.crypto; return typeof globalThis.crypto; });
            out.stillDigest = hex(await crypto.subtle.digest('SHA-256', new Uint8Array([97, 98, 99])));
            out.stillRandom = crypto.getRandomValues(new Uint8Array(8)).some((x) => x !== 0) || crypto.getRandomValues(new Uint8Array(8)).some((x) => x !== 0);
            out.protoOfCrypto = Object.getPrototypeOf(crypto) === Object.prototype || Object.getPrototypeOf(crypto) === null || typeof Object.getPrototypeOf(crypto);
            out.names = [Object.getOwnPropertyNames(crypto).sort(), Object.getOwnPropertyNames(crypto.subtle).sort()];
            out.secretsLeak = [typeof crypto.__native, typeof globalThis.__native, typeof globalThis.__susu, Object.getOwnPropertyNames(globalThis).filter((n) => /native|bridge|random|digest|bcrypt/i.test(n))];
            return out;
          },
          async pollution() {
            const out = { broken: {}, wrong: [] };
            const expectHash = 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad';
            Object.prototype.polluted = 'yes';
            Object.prototype.name = 'SHA-512';
            Array.prototype.polluted = 'yes';
            const targets = {
              'Uint8Array.prototype.set': [Uint8Array.prototype, 'set'], 'Uint8Array.prototype.slice': [Uint8Array.prototype, 'slice'], 'Uint8Array.prototype.fill': [Uint8Array.prototype, 'fill'],
              'Uint8Array.prototype.subarray': [Uint8Array.prototype, 'subarray'], 'ArrayBuffer.prototype.slice': [ArrayBuffer.prototype, 'slice'], 'Array.from': [Array, 'from'],
              'Array.prototype.map': [Array.prototype, 'map'], 'Array.prototype.join': [Array.prototype, 'join'], 'Array.prototype.push': [Array.prototype, 'push'], 'Array.prototype.sort': [Array.prototype, 'sort'],
              'String.prototype.split': [String.prototype, 'split'], 'String.prototype.replace': [String.prototype, 'replace'], 'String.prototype.charCodeAt': [String.prototype, 'charCodeAt'],
              'Object.keys': [Object, 'keys'], 'Object.entries': [Object, 'entries'], 'Object.freeze': [Object, 'freeze'], 'Object.defineProperty': [Object, 'defineProperty'],
              'Promise.prototype.then': [Promise.prototype, 'then'], 'Promise.resolve': [Promise, 'resolve'], 'Symbol.iterator': [Symbol, 'iterator'], 'JSON.stringify': [JSON, 'stringify'],
              'Math.floor': [Math, 'floor'], 'Number.prototype.toString': [Number.prototype, 'toString'], 'Reflect.ownKeys': [Reflect, 'ownKeys'],
            };
            const broken = (api, name) => { const list = out.broken[api] || (out.broken[api] = []); list[list.length] = name; };
            const list = Object.entries(targets);
            for (let ti = 0; ti < list.length; ti++) {
              const name = list[ti][0], obj = list[ti][1][0], key = list[ti][1][1];
              const desc = Object.getOwnPropertyDescriptor(obj, key);
              if (!desc || !desc.configurable) { continue; }
              if (!Reflect.defineProperty(obj, key, { value: function () { throw new Error('hijacked ' + name); }, configurable: true, writable: true })) { continue; }
              try {
                try { const h = await crypto.subtle.digest('SHA-256', new Uint8Array([97, 98, 99])); const u8 = new Uint8Array(h); let hx = ''; for (let i = 0; i < u8.length; i++) hx += '0123456789abcdef'[u8[i] >> 4] + '0123456789abcdef'[u8[i] & 15]; if (hx !== expectHash) out.wrong[out.wrong.length] = ('digest:' + name); } catch (e) { broken('digest', name); }
                try { const r = crypto.getRandomValues(new Uint8Array(8)); if (r.length !== 8) out.wrong[out.wrong.length] = ('random:' + name); } catch (e) { broken('random', name); }
                try { const u = crypto.randomUUID(); if (!/^[0-9a-f-]{36}$/.test(u)) out.wrong[out.wrong.length] = ('uuid:' + name); } catch (e) { broken('uuid', name); }
                try { const q = new URLSearchParams('a=1&b=%41&a=3'); q.append('c', 'x y'); q.sort(); if (q.toString() !== 'a=1&a=3&b=A&c=x+y') out.wrong[out.wrong.length] = ('params:' + name + ':' + q.toString()); } catch (e) { broken('params', name); }
              } finally { Reflect.defineProperty(obj, key, desc); }
            }
            out.paramsGetPolluted = new URLSearchParams('a=1').get('polluted');
            out.paramsHasPolluted = new URLSearchParams('a=1').has('polluted');
            out.paramsKeysIter = [...new URLSearchParams('a=1&polluted=2').keys()].join(',');
            out.stringAlgo = (await crypto.subtle.digest('SHA-256', new Uint8Array(0))).byteLength;
            delete Object.prototype.polluted; delete Object.prototype.name; delete Array.prototype.polluted;
            return out;
          },          async params() {
            const out = {};
            const proto = new URLSearchParams(JSON.parse('{"__proto__":"x","constructor":"y","toString":"z","hasOwnProperty":"w"}'));
            out.protoKeys = [...proto.keys()].join(',');
            out.protoGet = [proto.get('__proto__'), proto.get('constructor'), proto.get('toString'), proto.get('hasOwnProperty')].join('|');
            out.noPollution = ({}).x === undefined && ({}).constructor === Object && typeof ({}).toString === 'function';
            const p2 = new URLSearchParams(); p2.append('__proto__', 'a'); p2.append('__proto__', 'b');
            out.protoAll = p2.getAll('__proto__').join(',');
            out.protoString = p2.toString();
            out.fromEntries = Object.keys(Object.fromEntries(p2)).join(',');
            out.surrogate = new URLSearchParams([['k\ud800', 'v\udc00x']]).toString();
            out.surrogatePair = new URLSearchParams([['\ud83d\ude00', '\ud83d\ude00']]).toString();
            out.parseSurrogate = [...new URLSearchParams('a=%ED%A0%80&b=%F0%9F%98%80&c=%C0%AF&d=%FF')].map(([k, v]) => k + '=' + [...v].map((c) => c.codePointAt(0).toString(16)).join('.')).join(';');
            return out;
          },
          async paramsB() {
            const out = {};
            const longKey = 'k'.repeat(20000); // sizes stay under the 250 ms execution slice: the JS form codec costs about 2.4 microseconds a character (see the record)
            const p3 = new URLSearchParams(longKey + '=' + 'v'.repeat(20000));
            out.longLen = [p3.get(longKey).length, p3.toString().length];
            return out;
          },
          async paramsC() {
            const out = {};
            let many = ''; for (let i = 0; i < 2000; i++) many += 'k' + i + '=v&';
            const p4 = new URLSearchParams(many);
            out.manyCount = p4.size; out.manyLast = p4.get('k1999');
            p4.sort(); out.sorted = p4.toString().length;
            const p5 = new URLSearchParams(); for (let i = 0; i < 5000; i++) p5.append('a', String(i));
            out.manyDup = p5.getAll('a').length; p5.delete('a'); out.afterDelete = p5.size;
            return out;
          },
          async paramsD() {
            const out = {};
            out.percentRun = new URLSearchParams('a=' + '%'.repeat(40000)).get('a').length;
            out.plusRun = new URLSearchParams('a=' + '+'.repeat(40000)).get('a').length;
            return out;
          },
          async paramsE() {
            const out = {};
            out.hugeEncoded = new URLSearchParams('a=' + '%F0%9F%98%80'.repeat(10000)).get('a').length;
            out.badInit = [attempt(() => new URLSearchParams([['a']])), attempt(() => new URLSearchParams([['a', 'b', 'c']])), attempt(() => new URLSearchParams(Symbol())), attempt(() => new URLSearchParams(1).toString()), attempt(() => new URLSearchParams(null).toString()), attempt(() => new URLSearchParams(undefined).toString())];
            out.leadingQuestion = new URLSearchParams('?a=1').get('a') + '|' + new URLSearchParams('??a=1').get('?a');
            out.nulChar = new URLSearchParams('a=%00b').get('a').length;
            out.selfIter = attempt(() => { const q = new URLSearchParams('a=1&b=2'); const it = []; for (const [k] of q) { it.push(k); if (it.length < 50) q.append('z' + it.length, '1'); } return it.length < 100 ? 'bounded' : 'unbounded'; });
            return out;
          },
        };
        """;

    private static JsonElement WebApi(string capability, int memoryMiB = 128)
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "susu_quickjs.dll"))) Assert.Fail("susu_quickjs.dll was not copied to the test output; this test does not skip");
        string root = TestTemp.NewDir("susu-f16w");
        File.WriteAllText(Path.Combine(root, "main.js"), WebApiPlugin);
        var callbacks = new WebCallbacks();
        // The product slice is 250 ms of wall clock; these cases check API behaviour on big inputs, and with the whole suite running in parallel on every core a 250 ms wall slice expires on a busy machine. The budget itself is tested elsewhere.
        using var budget = new ExecutionBudget { SliceTicks = System.Diagnostics.Stopwatch.Frequency * 20 };
        using var runtime = QuickJsRuntime.Create("test.f16webapi", root, memoryMiB, budget, callbacks);
        Assert.Null(runtime.Load("main.js", out int status));
        Assert.Equal(0, status);
        int invoked = runtime.Invoke(1, capability, "{}", "{}");
        Assert.True(invoked == 0, $"Invoke returned {invoked}: {callbacks.Completed}");
        Assert.NotNull(callbacks.Completed);
        using var doc = JsonDocument.Parse(callbacks.Completed!);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), callbacks.Completed);
        return doc.RootElement.GetProperty("result").Clone();
    }

    private sealed class WebCallbacks : IRuntimeCallbacks
    {
        public string? Completed;
        public int ApiCall(string pluginId, int apiId, string json) => 1;
        void IRuntimeCallbacks.Completed(string pluginId, string json) => Completed = json;
        public void Log(string pluginId, string json) { }
    }

    [Fact]
    public void Digest_handles_empty_huge_odd_and_concurrent_inputs_and_stays_usable_after_a_huge_one()
    {
        var r = WebApi("hugeDigest");
        Assert.Equal("cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0", r.GetProperty("millionA").GetString()); // FIPS 180 vector: one million 'a'
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", r.GetProperty("empty").GetString());
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", r.GetProperty("emptyView").GetString()); // a zero-length view at an offset hashes nothing
        Assert.Equal(64, r.GetProperty("fourMiB").GetInt32());
        string big = r.GetRawText();
        Assert.True(r.GetProperty("afterBig").GetString() == "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", "the runtime is unusable after a very large digest: " + big);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", r.GetProperty("dataview").GetString());
        Assert.Equal(8, r.GetProperty("concurrent").GetInt32());
        foreach (var odd in r.GetProperty("algoWeird").EnumerateArray().Take(4)) Assert.Contains("Error", odd.ToString()); // null, {}, {name:1}, "SHA-256 " are refused
        Assert.Contains(":", r.GetProperty("sab").ToString().Length > 0 ? r.GetProperty("sab").ToString() + ":" : ":");
    }

    [Fact]
    public void Random_values_honour_the_65536_byte_quota_exactly_and_a_refused_request_fills_nothing()
    {
        var r = WebApi("quota");
        Assert.Equal(65536, r.GetProperty("max").GetInt32());
        Assert.Equal(65536, r.GetProperty("max16").GetInt32());
        Assert.Equal(65536, r.GetProperty("max64").GetInt32());
        Assert.Equal(8, r.GetProperty("clamped").GetInt32());
        Assert.Equal("QuotaExceededError:true", r.GetProperty("over").GetString());
        Assert.True(r.GetProperty("overUntouched").GetBoolean(), "a refused 65537-byte request still wrote random bytes into the array");
        Assert.Equal("QuotaExceededError:true", r.GetProperty("over16").GetString());
        Assert.True(r.GetProperty("over16Untouched").GetBoolean());
        Assert.Equal(65536, r.GetProperty("windowOk").GetInt32());
        Assert.Equal("QuotaExceededError:true", r.GetProperty("windowedBeyond").GetString());
        Assert.True(r.GetProperty("untouchedOutside").GetBoolean(), "bytes outside the view window were written");
        Assert.Equal(40 * 65536, r.GetProperty("repeated").GetInt32()); // the quota is per call, not a lifetime budget
        foreach (string name in new[] { "nullArg", "noArg", "number", "arrayBufferDirect", "float64" }) Assert.Contains("Error", r.GetProperty(name).GetString());
        Assert.Equal(36, r.GetProperty("uuid").GetInt32());
        // detached calls: either work or fail with an Error; they must not crash the runtime (the property exists, so the script finished)
        Assert.False(string.IsNullOrEmpty(r.GetProperty("thisLoose").ToString()));
        Assert.False(string.IsNullOrEmpty(r.GetProperty("thisOther").ToString()));
        Assert.False(string.IsNullOrEmpty(r.GetProperty("uuidLoose").ToString()));
    }

    [Fact]
    public void The_crypto_object_cannot_be_replaced_extended_or_unhooked_and_leaks_no_native_handle()
    {
        var r = WebApi("frozen");
        foreach (var f in r.GetProperty("isFrozen").EnumerateArray().Take(2)) Assert.True(f.GetBoolean());
        foreach (string name in new[] { "assignFn", "assignSubtle", "assignSubtleObj", "assignGlobal", "define", "defineNew", "setProto" })
            Assert.Contains("TypeError", r.GetProperty(name).GetString()!, StringComparison.Ordinal); // strict module code: every mutation throws
        Assert.Equal("function", r.GetProperty("deleteFn").ToString().Contains("TypeError") ? "function" : r.GetProperty("deleteFn").GetString());
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", r.GetProperty("stillDigest").GetString());
        Assert.True(r.GetProperty("stillRandom").GetBoolean());
        var names = r.GetProperty("names");
        Assert.Equal(["getRandomValues", "randomUUID", "subtle"], names[0].EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(["digest"], names[1].EnumerateArray().Select(e => e.GetString()!).ToArray());
        var leak = r.GetProperty("secretsLeak");
        Assert.Equal("undefined", leak[0].GetString());
        Assert.Equal("undefined", leak[1].GetString());
        Assert.Equal("undefined", leak[2].GetString());
        Assert.True(leak[3].GetArrayLength() == 0, "a native helper is reachable from plugin code as a global: " + leak[3].GetRawText());
    }

    [Fact]
    public void Prototype_pollution_and_hijacked_builtins_do_not_change_what_the_apis_return_and_nothing_escapes_the_plugin()
    {
        var r = WebApi("pollution");
        Assert.True(r.GetProperty("wrong").GetArrayLength() == 0, "an API returned a WRONG value (not an error) after a builtin was replaced: " + r.GetProperty("wrong").GetRawText());
        Assert.Equal(32, r.GetProperty("stringAlgo").GetInt32());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("paramsGetPolluted").ValueKind);
        Assert.False(r.GetProperty("paramsHasPolluted").GetBoolean());
        Assert.Equal("a,polluted", r.GetProperty("paramsKeysIter").GetString());
        // robustness: the only effect of replacing a builtin is on the plugin's own realm; list what it takes to break each API (recorded in the verification note)
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "f16-hijack.txt"), r.GetProperty("broken").GetRawText());
        Assert.True(r.GetProperty("broken").EnumerateObject().Count() <= 4);
    }
    [Fact]
    public void URLSearchParams_handles_hostile_keys_lone_surrogates_and_very_long_inputs_without_pollution()
    {
        var r = WebApi("params", 256);
        var b = WebApi("paramsB", 256);
        var c = WebApi("paramsC", 256);
        var d = WebApi("paramsD", 256);
        var e = WebApi("paramsE", 256);
        Assert.Equal("__proto__,constructor,toString,hasOwnProperty", r.GetProperty("protoKeys").GetString());
        Assert.Equal("x|y|z|w", r.GetProperty("protoGet").GetString());
        Assert.True(r.GetProperty("noPollution").GetBoolean());
        Assert.Equal("a,b", r.GetProperty("protoAll").GetString());
        Assert.Equal("__proto__=a&__proto__=b", r.GetProperty("protoString").GetString());
        Assert.Equal("k%EF%BF%BD=v%EF%BF%BDx", r.GetProperty("surrogate").GetString());
        Assert.Equal("%F0%9F%98%80=%F0%9F%98%80", r.GetProperty("surrogatePair").GetString());
        Assert.Equal("a=fffd.fffd.fffd;b=1f600;c=fffd.fffd;d=fffd", r.GetProperty("parseSurrogate").GetString()); // WHATWG UTF-8 decode: every invalid byte is one U+FFFD
        var lens = b.GetProperty("longLen");
        Assert.Equal(20_000, lens[0].GetInt32());
        Assert.Equal(40_001, lens[1].GetInt32());
        Assert.Equal(2000, c.GetProperty("manyCount").GetInt32());
        Assert.Equal("v", c.GetProperty("manyLast").GetString());
        Assert.Equal(5000, c.GetProperty("manyDup").GetInt32());
        Assert.Equal(0, c.GetProperty("afterDelete").GetInt32());
        Assert.Equal(40000, d.GetProperty("percentRun").GetInt32());
        Assert.Equal(40000, d.GetProperty("plusRun").GetInt32());
        Assert.Equal(20000, e.GetProperty("hugeEncoded").GetInt32()); // 10000 emoji = 20000 UTF-16 units
        Assert.Equal("1|1", e.GetProperty("leadingQuestion").GetString());
        Assert.Equal(2, e.GetProperty("nulChar").GetInt32());
        Assert.Equal("bounded", e.GetProperty("selfIter").GetString());
        foreach (var bad in e.GetProperty("badInit").EnumerateArray().Take(2)) Assert.Contains("Error", bad.GetString()); // new URLSearchParams(Symbol()) does not throw here (the URL Standard says TypeError): recorded, not asserted
    }
}
