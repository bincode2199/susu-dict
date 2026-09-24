using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Susu.Windows.Shell;

internal static unsafe partial class NativeWebView
{
    private const string Dll = "susu_native.dll";
    [LibraryImport(Dll)] public static partial int susu_wv_runtime_version(char* buffer, int length);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int susu_wv_env_create(string userData, string language, delegate* unmanaged[Stdcall]<nint, int, int, char*, void> callback, nint context, out nint handle);
    [LibraryImport(Dll)] public static partial void susu_wv_env_release(nint handle);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int susu_wv_create(nint environment, nint parent, string host, string folder, string path, int devTools, delegate* unmanaged[Stdcall]<nint, int, int, char*, void> callback, nint context, out nint handle);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf16)] public static partial int susu_wv_post(nint handle, string json);
    [LibraryImport(Dll)] public static partial int susu_wv_bounds(nint handle, int x, int y, int width, int height);
    [LibraryImport(Dll)] public static partial int susu_wv_visible(nint handle, int visible);
    [LibraryImport(Dll)] public static partial int susu_wv_suspend(nint handle);
    [LibraryImport(Dll)] public static partial int susu_wv_focus(nint handle);
    [LibraryImport(Dll)] public static partial int susu_wv_parent_moved(nint handle);
    [LibraryImport(Dll)] public static partial void susu_wv_close(nint handle);

    public const int ViewCreated = 1, NavigationCompleted = 2, WebMessage = 3, ProcessFailed = 4, NavigationBlocked = 5, Suspended = 6, EnvironmentCreated = 101, BrowserExited = 102;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    public static void OnEvent(nint context, int @event, int hr, char* text)
    {
        try
        {
            if (GCHandle.FromIntPtr(context).Target is IWebViewSink sink) sink.OnEvent(@event, hr, text is null ? null : new string(text));
        }
        catch (Exception e) { WindowClasses.Unhandled?.Invoke(e); }
    }
}

internal interface IWebViewSink
{
    void OnEvent(int @event, int hr, string? text);
}

/// <summary>WebView2 Runtime detection (F03.1): a missing runtime is reported, never silently worked around.</summary>
public static unsafe class WebViewRuntime
{
    public static string? InstalledVersion()
    {
        char* buffer = stackalloc char[128];
        try { return NativeWebView.susu_wv_runtime_version(buffer, 128) >= 0 ? new string(buffer) : null; }
        catch (DllNotFoundException) { return null; }
    }

    /// <summary>Official Evergreen Runtime page (Microsoft). Opened in the user's browser only after they agree.</summary>
    public const string DownloadPage = "https://developer.microsoft.com/microsoft-edge/webview2/";
}

/// <summary>The single WebView2 environment shared by all windows (ARCHITECTURE 9).</summary>
internal sealed unsafe class WebViewEnvironment : IWebViewSink
{
    private GCHandle self;
    private nint handle;
    private readonly List<Action> whenReady = [];

    public WebViewEnvironment(string userDataFolder, string language)
    {
        self = GCHandle.Alloc(this);
        Directory.CreateDirectory(userDataFolder);
        int hr = NativeWebView.susu_wv_env_create(userDataFolder, language, &NativeWebView.OnEvent, GCHandle.ToIntPtr(self), out handle);
        if (hr < 0) { self.Free(); Marshal.ThrowExceptionForHR(hr); }
    }

    public bool Ready { get; private set; }
    public int Status { get; private set; } = 1;
    public nint Handle => handle;
    public event Action? BrowserExited;

    public void WhenReady(Action action) { if (Ready) action(); else whenReady.Add(action); }

    public void OnEvent(int @event, int hr, string? text)
    {
        if (@event == NativeWebView.EnvironmentCreated)
        {
            Ready = hr >= 0;
            Status = hr;
            foreach (var action in whenReady) action();
            whenReady.Clear();
        }
        else if (@event == NativeWebView.BrowserExited)
        {
            BrowserExited?.Invoke();
            if (handle == 0 && self.IsAllocated) self.Free(); // released and exited: nothing will call back any more
        }
    }

    /// <summary>Drops the environment; BrowserExited still arrives after every controller is closed.</summary>
    public void Release()
    {
        if (handle == 0) return;
        NativeWebView.susu_wv_env_release(handle);
        handle = 0;
    }
}

/// <summary>One controller bound to one window and one trusted origin.</summary>
internal sealed unsafe class WebViewControl : IWebViewSink
{
    private GCHandle self;
    private nint handle;
    private readonly List<string> outbox = [];

    public WebViewControl(WebViewEnvironment environment, nint parent, string host, string folder, string path, bool devTools)
    {
        self = GCHandle.Alloc(this);
        int hr = NativeWebView.susu_wv_create(environment.Handle, parent, host, folder, path, devTools ? 1 : 0, &NativeWebView.OnEvent, GCHandle.ToIntPtr(self), out handle);
        if (hr < 0) { self.Free(); Marshal.ThrowExceptionForHR(hr); }
    }

    public bool Created { get; private set; }
    public bool Loaded { get; private set; }
    public event Action<string>? Message;
    public event Action<int>? Failed;
    public event Action<string?>? Blocked;
    public event Action? ControllerReady;
    public event Action? Navigated;

    public void OnEvent(int @event, int hr, string? text)
    {
        switch (@event)
        {
            case NativeWebView.ViewCreated:
                Created = hr >= 0;
                if (!Created) Failed?.Invoke(hr);
                else ControllerReady?.Invoke();
                break;
            case NativeWebView.NavigationCompleted:
                Loaded = hr >= 0;
                if (Loaded) { foreach (var json in outbox) NativeWebView.susu_wv_post(handle, json); outbox.Clear(); Navigated?.Invoke(); }
                else Failed?.Invoke(hr);
                break;
            case NativeWebView.WebMessage: if (text is not null) Message?.Invoke(text); break;
            case NativeWebView.ProcessFailed: Failed?.Invoke(hr); break;
            case NativeWebView.NavigationBlocked: Blocked?.Invoke(text); break;
        }
    }

    public void Post(string json)
    {
        if (handle == 0) return;
        if (Loaded) NativeWebView.susu_wv_post(handle, json);
        else outbox.Add(json);
    }

    public void Bounds(int width, int height) { if (Created) NativeWebView.susu_wv_bounds(handle, 0, 0, width, height); }
    public void Visible(bool visible) { if (Created) NativeWebView.susu_wv_visible(handle, visible ? 1 : 0); }
    public void Suspend() { if (Created) NativeWebView.susu_wv_suspend(handle); }
    public void Focus() { if (Created) NativeWebView.susu_wv_focus(handle); }
    public void ParentMoved() { if (Created) NativeWebView.susu_wv_parent_moved(handle); }

    public void Close()
    {
        if (handle == 0) return;
        NativeWebView.susu_wv_close(handle);
        handle = 0;
        if (self.IsAllocated) self.Free();
    }
}
