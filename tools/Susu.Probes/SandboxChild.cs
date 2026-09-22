using Susu.Windows;
using System.Diagnostics;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;

internal static class SandboxChild
{
    internal static int Run(string secretPath, int port)
    {
        bool container = SandboxProbe.IsAppContainer();
        bool resourceRead = File.ReadAllBytes(Environment.ProcessPath!).Length > 0;
        bool secretDenied = false;
        try { File.ReadAllText(secretPath); }
        catch (UnauthorizedAccessException) { secretDenied = true; }
        bool writeDenied = false;
        string path = Path.Combine(AppContext.BaseDirectory, "unexpected-container-write.txt");
        try { File.WriteAllText(path, "F00 synthetic write probe"); }
        catch (UnauthorizedAccessException) { writeDenied = true; }
        bool tcpDenied = false;
        string tcpOutcome = "connected";
        using (var tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try { tcp.ConnectAsync(IPAddress.Loopback, port, deadline.Token).AsTask().GetAwaiter().GetResult(); }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.AccessDenied) { tcpDenied = true; tcpOutcome = "access-denied"; }
            catch (OperationCanceledException) { tcpDenied = true; tcpOutcome = "deadline-no-connection"; }
        }
        bool udpDenied = false;
        using (var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            try { udp.SendTo(new byte[] { 43 }, new IPEndPoint(IPAddress.Loopback, port)); udpDenied = true; } // Parent independently rejects any delivered datagram.
            catch (SocketException error) when (error.SocketErrorCode == SocketError.AccessDenied) { udpDenied = true; }
        }
        bool childDenied = false;
        try
        {
            using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { ArgumentList = { "--sandbox-grandchild" }, UseShellExecute = false, CreateNoWindow = true });
            if (child is not null && !child.WaitForExit(1000)) { child.Kill(); child.WaitForExit(); }
        }
        catch (Win32Exception error) when (error.NativeErrorCode is 5 or 1816) { childDenied = true; }
        Console.WriteLine($"container={container}; resourceRead={resourceRead}; secretDenied={secretDenied}; resourceWriteDenied={writeDenied}; tcpBlocked={tcpDenied}; tcpOutcome={tcpOutcome}; udpSubmittedOrDenied={udpDenied}; childDenied={childDenied}");
        return container && resourceRead && secretDenied && writeDenied && tcpDenied && udpDenied && childDenied ? 0 : 1;
    }
}
