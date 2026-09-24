using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Susu.Windows;

/// <summary>Current-user DPAPI; never machine-wide and never allows a UI prompt.</summary>
public sealed class DpapiProtector : Susu.Abstractions.ISecretProtector
{
    public byte[] Protect(ReadOnlySpan<byte> plaintext) => Transform(plaintext, true);
    public byte[] Unprotect(ReadOnlySpan<byte> ciphertext) => Transform(ciphertext, false);

    private static unsafe byte[] Transform(ReadOnlySpan<byte> data, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        fixed (byte* pointer = data)
        {
            var input = new DataBlob { Length = data.Length, Data = (nint)pointer };
            DataBlob output;
            bool success = protect
                ? NativeMethods.CryptProtectData(in input, 0, 0, 0, 0, 1, out output)
                : NativeMethods.CryptUnprotectData(in input, 0, 0, 0, 0, 1, out output);
            if (!success) throw new Win32Exception(Marshal.GetLastPInvokeError());
            try
            {
                var result = new byte[output.Length];
                Marshal.Copy(output.Data, result, 0, result.Length);
                return result;
            }
            finally
            {
                if (output.Data != 0)
                {
                    CryptographicOperations.ZeroMemory(new Span<byte>((void*)output.Data, output.Length));
                    NativeMethods.LocalFree(output.Data);
                }
            }
        }
    }
}
