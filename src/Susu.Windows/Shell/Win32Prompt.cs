using System.Globalization;
using static Susu.Windows.Shell.Win32;

namespace Susu.Windows.Shell;

/// <summary>Native prompts used before any WebView exists (F03.1: WebView2 missing, unusable data).</summary>
public static class Win32Prompt
{
    /// <summary>
    /// Asks whether to open Microsoft's official WebView2 Runtime page. Nothing is downloaded or installed by
    /// Su-Su itself; the user installs the runtime from Microsoft and starts Su-Su again.
    /// </summary>
    public static bool AskInstallWebView()
    {
        bool chinese = CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        string text = chinese
            ? "Su-Su 需要 Microsoft Edge WebView2 运行时，但此电脑上未找到。\n\n是否打开微软官方下载页面？安装完成后请重新启动 Su-Su。"
            : "Su-Su needs the Microsoft Edge WebView2 Runtime, which was not found on this PC.\n\nOpen Microsoft's official download page? Start Su-Su again after installing it.";
        return MessageBox(0, text, "Su-Su", MB_ICONWARNING | MB_YESNO | MB_TOPMOST | MB_SETFOREGROUND) == IDYES;
    }

    public static void OpenInBrowser(string httpsUrl)
    {
        if (!httpsUrl.StartsWith("https://", StringComparison.Ordinal)) throw new ArgumentException("only https links are opened", nameof(httpsUrl));
        ShellExecute(0, "open", httpsUrl, null, null, SW_SHOWNORMAL);
    }

    public static void Error(string caption, string text) => MessageBox(0, text, caption, MB_ICONWARNING | MB_TOPMOST | MB_SETFOREGROUND);
}
