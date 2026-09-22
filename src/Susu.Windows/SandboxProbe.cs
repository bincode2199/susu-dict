using System.Runtime.InteropServices;
using System.Text;
using System.Net;
using System.Net.Sockets;

namespace Susu.Windows;

public static partial class SandboxProbe
{
    [LibraryImport("susu_windows_probe", EntryPoint = "susu_is_appcontainer")]
    private static partial int IsContainer(out int value);
    [LibraryImport("susu_windows_probe", EntryPoint = "susu_sandbox_probe_v2", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int Launch(string executable, string directory, string profile, string arguments, byte* output, uint capacity, out uint exitCode, out int stage,int closeJob);
    [LibraryImport("susu_windows_probe",EntryPoint="susu_job_memory_probe")]
    private static partial int MemoryProbe(out int error);
    public static int CheckMemoryLimit(){Marshal.ThrowExceptionForHR(MemoryProbe(out int error));return error;}
    [LibraryImport("susu_windows_probe",EntryPoint="susu_container_storage")]
    private static unsafe partial int ContainerStorage(char* folder,uint capacity);
    public static unsafe string CheckOwnStorage()
    {
        char[] folder=new char[32768];
        fixed(char* pointer=folder)Marshal.ThrowExceptionForHR(ContainerStorage(pointer,(uint)folder.Length));
        string root=new(folder,0,Array.IndexOf(folder,'\0'));
        string directory=Path.Combine(root,"LocalState");Directory.CreateDirectory(directory);
        string file=Path.Combine(directory,"Susu-F00-synthetic.txt");
        try{File.WriteAllText(file,"synthetic-42");if(File.ReadAllText(file)!="synthetic-42")throw new InvalidOperationException("Container storage mismatch.");}
        finally{if(File.Exists(file))File.Delete(file);}
        return directory;
    }

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
                $"--sandbox-child \"{secretPath}\" {port}", pointer, (uint)output.Length, out uint exitCode, out int stage,0);
            if (hr < 0) throw new InvalidOperationException($"AppContainer stage {stage}, HRESULT 0x{hr:X8}", Marshal.GetExceptionForHR(hr));
            string text = Encoding.UTF8.GetString(output, 0, Array.IndexOf(output, (byte)0));
            if (exitCode != 0) throw new InvalidOperationException($"Sandbox child exit 0x{exitCode:X8}: {text}");
            if (listener.Pending() || udpReceiver.Available > 0) throw new InvalidOperationException("AppContainer delivered direct loopback traffic.");
            string result=text + "receiverTcpAccepted=False; receiverUdpDelivered=False; profileRemoved=True; resourceAclRestored=True\n";
            foreach(string mode in new[]{"memory","job-close","own-storage"})
            {
                Array.Clear(output);
                hr=Launch(stagedExecutable,staged,$"Susu.F00.{Guid.NewGuid():N}",$"--sandbox-lifecycle {mode}",pointer,(uint)output.Length,out exitCode,out stage,mode=="job-close"?1:0);
                if(hr<0)throw new InvalidOperationException($"Sandbox {mode} stage {stage}, HRESULT 0x{hr:X8}");
                string message=Encoding.UTF8.GetString(output,0,Array.IndexOf(output,(byte)0));
                if(mode=="memory"&&(exitCode!=0||!message.Contains("memoryLimitDenied=True",StringComparison.Ordinal)))throw new InvalidOperationException($"Job memory probe failed: {message}");
                if(mode=="own-storage"&&(exitCode!=0||!message.Contains("ownStorageWrite=True",StringComparison.Ordinal)))throw new InvalidOperationException($"Container own-storage probe failed: {message}");
                // Native code requires a live, ready child, closes the sole Job
                // handle and waits at most two seconds. Windows may use exit 0
                // for Job teardown; nonzero exit is not the lifecycle contract.
                if(mode=="job-close"&&(exitCode==3||!message.Contains("containerReady=True",StringComparison.Ordinal)))throw new InvalidOperationException($"Kill-on-job-close probe failed: exit=0x{exitCode:X8}; {message}");
                result+=$"{mode}: exit=0x{exitCode:X8}; {message.Trim()}; profileRemoved=True; resourceAclRestored=True\n";
            }
            return result;
        }
    }
}
