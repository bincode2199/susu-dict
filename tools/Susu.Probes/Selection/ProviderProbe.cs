using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Susu.Probes.Selection;

/// <summary>
/// Harness-only child modes for the non-interactive SEL01 run: select the marker through UIA
/// Select()/IA2 setSelection and read selections back by searching providers in the window.
/// Not the product path (which follows keyboard focus at hotkey time).
/// </summary>
internal static partial class ProviderProbe
{
    [LibraryImport("susu_windows_probe", EntryPoint = "susu_test_select_marker", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SelectMarker(nint top, string marker, out int method, out int providers);
    [LibraryImport("susu_windows_probe", EntryPoint = "susu_test_read_window")]
    private static unsafe partial int ReadWindow(nint top, int forceIa2, char* text, uint capacity, out int source, out int passwordElements);

    internal sealed record Output(string Kind, int Method, int Providers, string Text, int PasswordElements, double Ms, string? Error);

    public static int Run(string mode, nint hwnd, string marker)
    {
        var timer = Stopwatch.StartNew();
        Output output;
        try
        {
            if (mode == "select")
            {
                Marshal.ThrowExceptionForHR(SelectMarker(hwnd, marker, out int method, out int providers));
                output = new Output("select", method, providers, "", 0, timer.Elapsed.TotalMilliseconds, null);
            }
            else
            {
                char[] buffer = new char[65537];
                int source, passwords;
                unsafe { fixed (char* p = buffer) Marshal.ThrowExceptionForHR(ReadWindow(hwnd, mode == "read-ia2" ? 1 : 0, p, (uint)buffer.Length, out source, out passwords)); }
                output = new Output(mode, source, 0, new string(buffer, 0, Array.IndexOf(buffer, '\0')), passwords, timer.Elapsed.TotalMilliseconds, null);
            }
        }
        catch (Exception error) { output = new Output(mode, 0, 0, "", 0, timer.Elapsed.TotalMilliseconds, $"{error.GetType().Name} 0x{error.HResult:X8}"); }
        Console.WriteLine(JsonSerializer.Serialize(output, ProviderJson.Default.Output));
        return 0;
    }

    public static (Output? Result, double TotalMs) Invoke(string mode, nint hwnd, string marker, int deadlineMs)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            using var child = SelectionTests.Start("--sel-provider", mode, hwnd.ToString(System.Globalization.CultureInfo.InvariantCulture), marker);
            string text = SelectionTests.ReadBounded(child, deadlineMs);
            return (JsonSerializer.Deserialize(text, ProviderJson.Default.Output), timer.Elapsed.TotalMilliseconds);
        }
        catch (Exception error) { return (new Output(mode, 0, 0, "", 0, 0, $"{error.GetType().Name}: {error.Message}"), timer.Elapsed.TotalMilliseconds); }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(Output))]
    internal partial class ProviderJson : JsonSerializerContext;
}
