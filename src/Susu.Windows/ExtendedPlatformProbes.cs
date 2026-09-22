using System.Runtime.InteropServices;

namespace Susu.Windows;

public static partial class ExtendedPlatformProbes
{
    [LibraryImport("susu_windows_probe", EntryPoint = "susu_els_probe")]
    private static partial int Els();
    [LibraryImport("susu_windows_probe", EntryPoint = "susu_webview_probe", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int WebView(string userData, double* samples, int count, out int exited);
    [LibraryImport("susu_windows_probe", EntryPoint = "susu_measure_windows", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int Measure(string folder, string userData, int warmMilliseconds, int idleMilliseconds,int memoryMode);

    public static void MeasureWindows(string folder, string userData, bool fullDuration,int memoryMode=0)
        => Marshal.ThrowExceptionForHR(Measure(folder, userData, fullDuration ? 600000 : 1000, fullDuration ? 300000 : 1000,memoryMode));

    public static void LanguageDetection() => Marshal.ThrowExceptionForHR(Els());
    public static unsafe double[] WebViewRoundTrips(string isolatedUserData)
    {
        double[] samples = new double[30];
        fixed (double* pointer = samples)
        {
            Marshal.ThrowExceptionForHR(WebView(isolatedUserData, pointer, samples.Length, out int exited));
            if (exited != 1) throw new InvalidOperationException("BrowserProcessExited was not observed after controller close.");
        }
        return samples;
    }
}
