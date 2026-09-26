using System.Runtime.InteropServices;

namespace Susu.Windows.Audio;

/// <summary>
/// Explicit IUnknown/vtable calls for SAPI, Media Foundation and WASAPI (ARCHITECTURE 1: no dynamic COM, no RCW;
/// IUnknown interfaces through a thin vtable layer). Every interface pointer is released by its owner in a finally.
/// </summary>
internal static unsafe partial class Com
{
    public const uint ClsctxInproc = 1, ClsctxAll = 0x17;

    [LibraryImport("ole32.dll")] internal static partial void CoTaskMemFree(nint memory);

    public static nint Slot(nint obj, int index) => (*(nint**)obj)[index];

    public static void Release(nint obj)
    {
        if (obj != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(obj, 2))(obj);
    }

    public static void Check(int hr, string what)
    {
        if (hr < 0) throw new COMException(what, hr);
    }

    public static nint Create(Guid clsid, Guid iid, uint context = ClsctxInproc)
    {
        Check(NativeMethods.CoCreateInstance(in clsid, 0, context, in iid, out nint instance), "CoCreateInstance");
        return instance;
    }

    /// <summary>Reads and frees a CoTaskMem string an interface returned.</summary>
    public static string? TakeString(nint value)
    {
        if (value == 0) return null;
        try { return Marshal.PtrToStringUni(value); }
        finally { CoTaskMemFree(value); }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on a fresh MTA thread (COM objects never cross threads here, and callers on the UI
    /// thread or a thread-pool thread of unknown apartment are never blocked or re-initialized).
    /// </summary>
    public static Task<T> RunMta<T>(Func<T> work, string name)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            int init = NativeMethods.CoInitializeEx(0, 0); // COINIT_MULTITHREADED
            try { done.TrySetResult(work()); }
            catch (Exception error) { done.TrySetException(error); }
            finally { if (init >= 0) NativeMethods.CoUninitialize(); }
        }) { IsBackground = true, Name = name };
        thread.Start();
        return done.Task;
    }
}
