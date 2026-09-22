using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Susu.Windows;

public static class PlatformProbes
{
    public static void Window()
    {
        nint window = NativeMethods.CreateWindowEx(0x08000000, "STATIC", "Su-Su F00 probe", 0x80000000, 0, 0, 320, 120, 0, 0, 0, 0);
        if (window == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (!NativeMethods.DestroyWindow(window)) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    // Explicit IUnknown boundary: activation only, not full feature acceptance.
    public static unsafe void ActivateCom(Guid clsid, Guid iid)
    {
        int init = NativeMethods.CoInitializeEx(0, 0); // MTA
        Marshal.ThrowExceptionForHR(init);
        try
        {
            Marshal.ThrowExceptionForHR(NativeMethods.CoCreateInstance(in clsid, 0, 1, in iid, out nint instance));
            if (instance == 0) throw new InvalidOperationException("COM returned a null interface.");
            var table = *(nint**)instance;
            ((delegate* unmanaged[Stdcall]<nint, uint>)table[2])(instance);
        }
        finally { NativeMethods.CoUninitialize(); }
    }

    public static void MediaFoundation()
    {
        Marshal.ThrowExceptionForHR(NativeMethods.MFStartup(0x00020070, 0));
        Marshal.ThrowExceptionForHR(NativeMethods.MFShutdown());
    }

    public static void Dpapi()
    {
        var protector = new DpapiProtector();
        byte[] sample = Encoding.UTF8.GetBytes("F00 synthetic secret 中文");
        byte[] ciphertext = protector.Protect(sample);
        if (ciphertext.AsSpan().SequenceEqual(sample)) throw new InvalidOperationException("DPAPI did not encrypt.");
        if (!protector.Unprotect(ciphertext).AsSpan().SequenceEqual(sample)) throw new InvalidOperationException("DPAPI round-trip mismatch.");
        ciphertext[^1] ^= 1;
        try { protector.Unprotect(ciphertext); }
        catch (Win32Exception) { return; }
        throw new InvalidOperationException("DPAPI accepted corrupted ciphertext.");
    }
}
