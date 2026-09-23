using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Susu.Windows;

namespace Susu.Probes.PluginHost;

/// <summary>
/// Access probes executed inside the container (or as an adversarial plain process).
/// Each argument is one probe; one "name=outcome" line is printed per probe.
/// Outcomes: allowed / denied(reason) / error(reason). No real user data is touched.
/// </summary>
internal static partial class XProbeChild
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetHandleInformation(nint handle, out uint flags);
    [LibraryImport("kernel32.dll")]
    private static partial uint GetFileType(nint handle);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandle(nint handle, out FileInfoNative info);

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileInfoNative
    {
        public uint Attributes; public long Created, Accessed, Written; public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    public static int Run(string[] probes)
    {
        foreach (string probe in probes)
        {
            string outcome;
            try { outcome = Execute(probe); }
            catch (UnauthorizedAccessException) { outcome = "denied(access)"; }
            catch (Win32Exception error) when (error.NativeErrorCode is 5 or 1816) { outcome = $"denied(win32 {error.NativeErrorCode})"; }
            catch (SocketException error) when (error.SocketErrorCode is SocketError.AccessDenied) { outcome = "denied(socket access)"; }
            catch (SocketException error) { outcome = $"denied(socket {error.SocketErrorCode})"; }
            catch (OperationCanceledException) { outcome = "denied(deadline without connection)"; }
            catch (AggregateException error) when (error.InnerException is SocketException socket) { outcome = $"denied(socket {socket.SocketErrorCode})"; }
            catch (Exception error) { outcome = $"error({error.GetType().Name}: {error.Message.Replace('\n', ' ')})"; }
            Console.WriteLine($"{probe}={outcome}");
            Console.Out.Flush();
        }
        return 0;
    }

    private static string Execute(string probe)
    {
        int colon = probe.IndexOf(':');
        string kind = colon < 0 ? probe : probe[..colon], arg = colon < 0 ? "" : probe[(colon + 1)..];
        switch (kind)
        {
            case "container": return SandboxProbe.IsAppContainer() ? "allowed(appcontainer)" : "denied(not appcontainer)";
            case "read":
                using (var stream = File.OpenRead(arg)) { stream.ReadByte(); }
                return "allowed";
            case "write":
                File.WriteAllText(arg, "F00 synthetic write probe");
                return "allowed";
            case "tcp":
            {
                var (host, port) = Endpoint(arg);
                using var socket = new Socket(host.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                using var deadline = new CancellationTokenSource(2000);
                socket.ConnectAsync(host, port, deadline.Token).AsTask().GetAwaiter().GetResult();
                return "allowed";
            }
            case "udpdns":
            {
                var (host, port) = Endpoint(arg);
                using var socket = new Socket(host.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                socket.SendTo(DnsQuery("example.com"), new IPEndPoint(host, port));
                using var deadline = new CancellationTokenSource(2000);
                byte[] buffer = new byte[512];
                socket.ReceiveAsync(buffer, SocketFlags.None, deadline.Token).AsTask().GetAwaiter().GetResult();
                return "allowed(response)";
            }
            case "udp":
            {
                var (host, port) = Endpoint(arg);
                using var socket = new Socket(host.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                socket.SendTo([43], new IPEndPoint(host, port));
                return "submitted"; // the receiver independently checks for delivery
            }
            case "dns":
            {
                var task = Dns.GetHostAddressesAsync(arg);
                if (!task.Wait(3000)) return "denied(deadline)";
                return task.Result.Length > 0 ? "allowed" : "denied(empty)";
            }
            case "spawn":
            {
                using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { ArgumentList = { "--sandbox-grandchild" }, UseShellExecute = false, CreateNoWindow = true });
                if (child is not null && !child.WaitForExit(1000)) { child.Kill(); child.WaitForExit(); }
                return "allowed";
            }
            case "alloc":
            {
                int error = SandboxProbe.CheckMemoryLimit();
                return error == 0 ? "allowed" : $"denied(win32 {error})";
            }
            case "ownstorage":
                return $"allowed({SandboxProbe.CheckOwnStorage()})";
            case "handle":
            {
                // arg = <handle value>|<volume serial>|<file index>; compares object identity, not paths.
                string[] parts = arg.Split('|');
                nint handle = (nint)long.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                // AppContainer processes run with strict handle checks: touching an invalid value
                // raises STATUS_INVALID_HANDLE, so consult the own handle table first.
                if (!OwnHandles().Contains(handle)) return "denied(not present)";
                if (!GetHandleInformation(handle, out _)) return "denied(not present)";
                if (GetFileType(handle) != 1) return "denied(value refers to a non-file object)";
                if (!GetFileInformationByHandle(handle, out var info)) return "denied(no file information)";
                ulong index = ((ulong)info.IndexHigh << 32) | info.IndexLow;
                return info.VolumeSerial.ToString(System.Globalization.CultureInfo.InvariantCulture) == parts[1] && index.ToString(System.Globalization.CultureInfo.InvariantCulture) == parts[2]
                    ? "allowed(inherited)" : "denied(value refers to another file)";
            }
            case "pipe":
            {
                // Adversarial client: try to open the host pipe (local form or \\127.0.0.1 remote form).
                string name = arg.StartsWith("remote|", StringComparison.Ordinal) ? arg[7..] : arg;
                string server = arg.StartsWith("remote|", StringComparison.Ordinal) ? "127.0.0.1" : ".";
                using var client = new NamedPipeClientStream(server, name, PipeDirection.InOut);
                client.Connect(2000);
                client.Write(Encoding.ASCII.GetBytes("rogue"));
                Thread.Sleep(500);
                return "allowed(connected)";
            }
            case "squat":
            {
                using var squatter = new NamedPipeServerStream(arg, PipeDirection.InOut, 1);
                Console.WriteLine("squat=ready");
                Console.Out.Flush();
                Thread.Sleep(15000);
                return "allowed";
            }
            default: return "error(unknown probe)";
        }
    }

    private static (IPAddress Host, int Port) Endpoint(string value)
    {
        int split = value.LastIndexOf(':');
        return (IPAddress.Parse(value[..split]), int.Parse(value[(split + 1)..], System.Globalization.CultureInfo.InvariantCulture));
    }

    [LibraryImport("ntdll.dll")]
    private static unsafe partial int NtQueryInformationProcess(nint process, int infoClass, void* buffer, uint length, out uint returned);

    /// <summary>Handle values in this process (ProcessHandleInformation, Windows 8+).</summary>
    private static unsafe HashSet<nint> OwnHandles()
    {
        uint size = 64 * 1024;
        while (true)
        {
            byte[] buffer = new byte[size];
            fixed (byte* p = buffer)
            {
                int status = NtQueryInformationProcess(-1, 51, p, size, out uint needed);
                if (status == unchecked((int)0xC0000004) && size < (16u << 20)) { size = Math.Max(size * 2, needed + 4096); continue; }
                if (status < 0) throw new System.ComponentModel.Win32Exception($"NtQueryInformationProcess 0x{status:X8}");
                long count = *(long*)p;
                var result = new HashSet<nint>();
                for (long i = 0; i < count; i++) result.Add(*(nint*)(p + 16 + i * 40)); // PROCESS_HANDLE_TABLE_ENTRY_INFO, 40 bytes on x64
                return result;
            }
        }
    }

    public static byte[] DnsQuery(string name)
    {
        var query = new List<byte> { 0x12, 0x34, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        foreach (string label in name.Split('.')) { query.Add((byte)label.Length); query.AddRange(Encoding.ASCII.GetBytes(label)); }
        query.AddRange([0x00, 0x00, 0x01, 0x00, 0x01]);
        return [.. query];
    }
}
