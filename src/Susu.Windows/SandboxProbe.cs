using System.Runtime.InteropServices;
using System.Text;
using System.Net;
using System.Net.Sockets;

namespace Susu.Windows;

public static partial class SandboxProbe
{
    [LibraryImport("susu_windows_probe", EntryPoint = "susu_is_appcontainer")]
    private static partial int IsContainer(out int value);
    [LibraryImport("susu_windows_probe", EntryPoint = "susu_sandbox_probe", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int Launch(string executable, string directory, string profile, string arguments, byte* output, uint capacity, out uint exitCode, out int stage);

    public static bool IsAppContainer()
    {
        Marshal.ThrowExceptionForHR(IsContainer(out int value));
        return value == 1;
    }

    public static unsafe string Run(string executable, string secretPath)
    {
        if (executable.Contains('"') || secretPath.Contains('"')) throw new ArgumentException("Paths must not contain quotes.");
        // Stage a disposable copy owned by the real test user. Build output may
        // be owned by an IDE/sandbox account; never change its ACL or take ownership.
        string staged = Path.GetFullPath(Path.Combine("artifacts", "probe-data", "sandbox-runs", Guid.NewGuid().ToString("N"), "binaries"));
        Directory.CreateDirectory(staged);
        foreach (string dll in Directory.EnumerateFiles(Path.GetDirectoryName(executable)!, "*.dll"))
            File.Copy(dll, Path.Combine(staged, Path.GetFileName(dll)));
        string stagedExecutable = Path.Combine(staged, Path.GetFileName(executable));
        File.Copy(executable, stagedExecutable);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var udpReceiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        using (var control = new TcpClient())
        {
            control.Connect(IPAddress.Loopback, port);
            using var accepted = listener.AcceptTcpClient();
        }
        using (var udpControl = new UdpClient())
        {
            udpControl.Send(new byte[] { 42 }, new IPEndPoint(IPAddress.Loopback, port));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var received = udpReceiver.ReceiveAsync(timeout.Token).AsTask().GetAwaiter().GetResult();
            if (received.Buffer.Length != 1 || received.Buffer[0] != 42) throw new InvalidOperationException("UDP control mismatch.");
        }
        byte[] output = new byte[4096];
        fixed (byte* pointer = output)
        {
            int hr = Launch(stagedExecutable, staged, $"Susu.F00.{Guid.NewGuid():N}",
                $"--sandbox-child \"{secretPath}\" {port}", pointer, (uint)output.Length, out uint exitCode, out int stage);
            if (hr < 0) throw new InvalidOperationException($"AppContainer stage {stage}, HRESULT 0x{hr:X8}", Marshal.GetExceptionForHR(hr));
            string text = Encoding.UTF8.GetString(output, 0, Array.IndexOf(output, (byte)0));
            if (exitCode != 0) throw new InvalidOperationException($"Sandbox child exit 0x{exitCode:X8}: {text}");
            if (listener.Pending() || udpReceiver.Available > 0) throw new InvalidOperationException("AppContainer delivered direct loopback traffic.");
            return text + "receiverTcpAccepted=False; receiverUdpDelivered=False\n";
        }
    }
}
