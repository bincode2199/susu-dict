using System.Runtime.InteropServices;

namespace Susu.Windows;

internal static partial class NativeMethods
{
    [LibraryImport("ole32.dll")] internal static partial int CoInitializeEx(nint reserved, uint mode);
    [LibraryImport("ole32.dll")] internal static partial void CoUninitialize();
    [LibraryImport("ole32.dll")] internal static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);
    [LibraryImport("mfplat.dll")] internal static partial int MFStartup(uint version, uint flags);
    [LibraryImport("mfplat.dll")] internal static partial int MFShutdown();
    [LibraryImport("user32.dll", EntryPoint="CreateWindowExW", StringMarshalling=StringMarshalling.Utf16, SetLastError=true)]
    internal static partial nint CreateWindowEx(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [LibraryImport("user32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static partial bool DestroyWindow(nint hwnd);
    [LibraryImport("crypt32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CryptProtectData(in DataBlob input, nint description, nint entropy, nint reserved, nint prompt, uint flags, out DataBlob output);
    [LibraryImport("crypt32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CryptUnprotectData(in DataBlob input, nint description, nint entropy, nint reserved, nint prompt, uint flags, out DataBlob output);
    [LibraryImport("kernel32.dll")] internal static partial nint LocalFree(nint memory);
}

[StructLayout(LayoutKind.Sequential)]
internal struct DataBlob { internal int Length; internal nint Data; }
