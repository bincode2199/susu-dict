using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Susu.Windows;

namespace Susu.Probes.PluginHost;

/// <summary>
/// F00 prototype access matrix X01–X07 (TEST-PLAN X) against the disposable plugin host.
/// Every case records expected vs observed; a case passes only on the expected outcome.
/// </summary>
internal static partial class XMatrix
{
    internal sealed record Case(string Id, string Name, string Expected, string Observed, bool Passed);

    private static readonly List<Case> cases = [];

    private static void Record(string id, string name, string expected, string observed, bool passed)
    {
        cases.Add(new Case(id, name, expected, observed, passed));
        Console.Error.WriteLine($"{(passed ? "PASS" : "FAIL")} {id} {name}: {observed}");
    }

    private static void Try(string id, string name, string expected, Func<(string Observed, bool Passed)> body)
    {
        try { var (observed, passed) = body(); Record(id, name, expected, observed, passed); }
        catch (Exception error) { Record(id, name, expected, $"exception {error.GetType().Name}: {error.Message}", false); }
    }

    /// <summary>Copies the published probe (exe, DLLs, plugins) to a disposable directory owned by the current user.</summary>
    public static string Stage(string label)
    {
        string source = AppContext.BaseDirectory;
        string staged = Path.GetFullPath(Path.Combine("artifacts", "probe-data", label, Guid.NewGuid().ToString("N"), "binaries"));
        Directory.CreateDirectory(staged);
        foreach (string file in Directory.EnumerateFiles(source))
            if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                File.Copy(file, Path.Combine(staged, Path.GetFileName(file)));
        foreach (string file in Directory.EnumerateFiles(Path.Combine(source, "plugins"), "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(staged, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        return staged;
    }

    public static string RunProbes(ContainerHost container, string exe, IEnumerable<string> probes, SafeHandle[]? extraInherit = null, ulong memoryLimit = 128UL << 20)
    {
        using var output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var inherit = new List<SafeHandle> { output.ClientSafePipeHandle };
        if (extraInherit is not null) inherit.AddRange(extraInherit);
        using var process = container.Start(exe, "--x-probe " + string.Join(' ', probes.Select(Quote)), [.. inherit], null, output.ClientSafePipeHandle, memoryLimit);
        output.DisposeLocalCopyOfClientHandle();
        var text = new StreamReader(output).ReadToEndAsync();
        if (!process.WaitForExit(30000)) throw new TimeoutException("Probe child did not exit.");
        NativeInherit.GetExitCodeProcess(process.Process.DangerousGetHandle(), out uint code);
        return (text.Wait(5000) ? text.Result : "") + $"\nexit={code}\n";
    }

    private static string Quote(string value) => value.Contains(' ') ? "\"" + value + "\"" : value;

    private static Dictionary<string, string> Parse(string output)
        => output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('=', 2)).Where(p => p.Length == 2).GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.Last()[1]);

    public static int Run(string evidencePath)
    {
        string staged = Stage("xmatrix");
        string exe = Path.Combine(staged, Path.GetFileName(Environment.ProcessPath!));
        string name = $"Susu.F00.X.{Guid.NewGuid():N}";
        string sensitiveRoot = Guid.NewGuid().ToString("N");
        var sensitive = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), $"Su-Su-F00-probe-{sensitiveRoot}", "settings.yaml"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), $"Su-Su-F00-probe-{sensitiveRoot}", "secrets.dat"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), $"Su-Su-F00-probe-{sensitiveRoot}", "susu.db"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), $"Su-Su-F00-probe-{sensitiveRoot}", "cache", "capture.wav"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), $"Su-Su-F00-probe-{sensitiveRoot}.txt"),
        };
        foreach (string file in sensitive) { Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, "synthetic F00 probe data; no user content"); }
        try
        {
            RunCases(staged, exe, name, sensitive);
        }
        finally
        {
            foreach (string file in sensitive) { try { File.Delete(file); } catch (IOException) { } }
            foreach (string dir in sensitive.Select(f => Path.GetDirectoryName(f)!).Distinct()) { try { if (dir.Contains(sensitiveRoot)) Directory.Delete(dir, true); } catch (IOException) { } }
            foreach (string dir in sensitive.Select(f => Path.GetDirectoryName(Path.GetDirectoryName(f))!).Where(d => d.Contains(sensitiveRoot))) { try { Directory.Delete(dir, true); } catch (IOException) { } }
        }
        var report = new XReport(DateTimeOffset.UtcNow, Environment.OSVersion.VersionString, "F00 disposable plugin-host prototype; not F04 production acceptance", [.. cases]);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(evidencePath))!);
        File.WriteAllText(evidencePath, JsonSerializer.Serialize(report, XJson.Default.XReport));
        Console.Error.WriteLine($"{cases.Count(c => c.Passed)}/{cases.Count} cases passed");
        return cases.All(c => c.Passed) ? 0 : 1;
    }

    private static void RunCases(string staged, string exe, string name, string[] sensitive)
    {
        // ---------- X01 profile lifecycle ----------
        string? firstSid = null;
        Try("X01", "first start creates profile, host authenticates and serves a call", "created profile; authenticated pipe; call completes", () =>
        {
            using var session = HostSession.Start(new HostSession.Options(exe, staged, "quickjs", ProfileName: name, KeepProfile: true));
            firstSid = session.Container.SidText;
            var loaded = session.Load("bench", "plugins/bench");
            var call = session.Invoke("bench", "translate", "{\"text\":\"hello\"}", ["https://api.bench.example"]);
            bool ok = call.Result.Wait(5000) && call.Result.Result.Type == FrameTypes.Completed;
            bool created = session.Container.Created;
            bool shut = session.Shutdown();
            return ($"created={created}; loaded={loaded.Ok}; completed={ok}; cleanShutdown={shut}", created && loaded.Ok && ok && shut);
        });
        Try("X01", "restart reuses the existing profile and SID", "created=false; same SID; call completes", () =>
        {
            using var session = HostSession.Start(new HostSession.Options(exe, staged, "quickjs", ProfileName: name, KeepProfile: true));
            session.Load("bench", "plugins/bench");
            var call = session.Invoke("bench", "translate", "{\"text\":\"again\"}", ["https://api.bench.example"]);
            bool ok = call.Result.Wait(5000) && call.Result.Result.Type == FrameTypes.Completed;
            bool reused = !session.Container.Created && session.Container.SidText == firstSid;
            return ($"reused={reused}; completed={ok}", reused && ok);
        });
        Try("X01", "uninstall deletes profile and its container folder; reinstall creates anew", "profile folder removed; next open creates", () =>
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", name.ToLowerInvariant());
            bool existedBefore = Directory.Exists(folder);
            var existing = ContainerHost.Open(name, staged, allowExisting: true);
            existing.Close(deleteProfile: true);
            bool removed = !Directory.Exists(folder);
            var fresh = ContainerHost.Open(name, staged, allowExisting: true);
            bool recreated = fresh.Created;
            fresh.Close(deleteProfile: true);
            return ($"folderBefore={existedBefore}; folderRemoved={removed}; recreated={recreated}", removed && recreated);
        });
        Try("X01", "broken install (missing native library) fails startup; no unsandboxed retry; no process left", "start throws; failed child terminated; no fallback launch", () =>
        {
            // HostSession has no unsandboxed launch path at all; this checks the failure is
            // surfaced and the half-started container child does not linger.
            string broken = Path.Combine(Path.GetDirectoryName(staged)!, "broken-install");
            Directory.CreateDirectory(broken);
            string copy = Path.Combine(broken, Path.GetFileName(exe));
            File.Copy(exe, copy, true); // susu_windows_probe.dll / susu_quickjs.dll deliberately absent
            string observed;
            bool failed;
            try
            {
                using var session = HostSession.Start(new HostSession.Options(copy, broken, "quickjs"));
                observed = "host started unexpectedly"; failed = false;
            }
            catch (Exception error) { observed = $"{error.GetType().Name}: {error.Message}"; failed = true; }
            var deadline = Stopwatch.StartNew();
            Process[] left;
            do { left = [.. Process.GetProcesses().Where(p => { try { return string.Equals(p.MainModule?.FileName, copy, StringComparison.OrdinalIgnoreCase); } catch { return false; } })]; if (left.Length == 0) break; Thread.Sleep(50); }
            while (deadline.ElapsedMilliseconds < 2000);
            return ($"{observed}; processesFromBrokenInstall={left.Length}", failed && left.Length == 0);
        });

        // ---------- X02 resources ----------
        using (var container = ContainerHost.Open($"{name}.x2", staged, allowExisting: false))
        {
            var probes = new List<string>
            {
                "container", $"read:{exe}", $"read:{Path.Combine(staged, "susu_quickjs.dll")}", $"read:{Path.Combine(staged, "plugins", "bench", "main.js")}",
            };
            probes.AddRange(sensitive.Select(f => $"read:{f}"));
            probes.Add($"write:{Path.Combine(staged, "plugins", "bench", "main.js")}");
            probes.Add($"write:{Path.Combine(staged, "plugins", "bench", "injected.js")}");
            probes.Add($"write:{Path.Combine(staged, "susu_quickjs.dll")}");
            var results = Parse(RunProbes(container, exe, probes));
            bool Allowed(string key) => results.TryGetValue(key, out var v) && v.StartsWith("allowed", StringComparison.Ordinal);
            bool Denied(string key) => results.TryGetValue(key, out var v) && v.StartsWith("denied", StringComparison.Ordinal);
            Record("X02", "runs as AppContainer", "allowed(appcontainer)", results.GetValueOrDefault("container", "missing"), Allowed("container"));
            foreach (string probe in probes.Skip(1).Take(3)) Record("X02", $"required read-only resource {Path.GetFileName(probe)}", "allowed", results.GetValueOrDefault(probe, "missing"), Allowed(probe));
            foreach (string probe in probes.Skip(4).Take(sensitive.Length)) Record("X02", $"sensitive read {Path.GetFileName(probe)} ({Path.GetFileName(Path.GetDirectoryName(probe))})", "denied", results.GetValueOrDefault(probe, "missing"), Denied(probe));
            foreach (string probe in probes.Skip(4 + sensitive.Length)) Record("X02", $"program/plugin write {Path.GetFileName(probe[6..])}", "denied", results.GetValueOrDefault(probe, "missing"), Denied(probe));
            Try("X02", "resource ACL is not opened to all packages or everyone", "only the container SID was added", () =>
            {
                string sddl = new DirectoryInfo(staged).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
                bool broad = sddl.Contains(";;;S-1-15-2-1)", StringComparison.Ordinal) || sddl.Contains(";;;AC)", StringComparison.Ordinal) || sddl.Contains(";;;WD)", StringComparison.Ordinal);
                bool scoped = sddl.Contains(container.SidText, StringComparison.Ordinal);
                return ($"containerAce={scoped}; broadAce={broad}", scoped && !broad);
            });

            // ---------- X03 own storage ----------
            var own = Parse(RunProbes(container, exe, ["ownstorage"]));
            Record("X03", "container-owned LocalState file + private registry round trip", "allowed (expected AppContainer behaviour; synthetic data only)", own.GetValueOrDefault("ownstorage", "missing"), own.GetValueOrDefault("ownstorage", "").StartsWith("allowed", StringComparison.Ordinal));

            // ---------- X04 network ----------
            X04(container, exe);

            // ---------- X06 child/memory ----------
            var x6 = Parse(RunProbes(container, exe, ["spawn", "alloc"], memoryLimit: 128UL << 20));
            Record("X06", "child process creation", "denied (Job active-process limit 1)", x6.GetValueOrDefault("spawn", "missing"), x6.GetValueOrDefault("spawn", "").StartsWith("denied", StringComparison.Ordinal));
            Record("X06", "256 MiB commit under 128 MiB Job limit", "denied", x6.GetValueOrDefault("alloc", "missing"), x6.GetValueOrDefault("alloc", "").StartsWith("denied", StringComparison.Ordinal));

            // ---------- X05 handle inheritance ----------
            Try("X05", "unlisted inheritable handle is not inherited (control: listed handle is)", "unlisted denied; listed allowed", () =>
            {
                string secret = sensitive[1];
                using var handle = File.OpenHandle(secret, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.None);
                // Make it inheritable so only the explicit handle list protects the child.
                SetInheritable(handle);
                if (!XProbeChild.GetFileInformationByHandle(handle.DangerousGetHandle(), out var id)) throw new System.ComponentModel.Win32Exception();
                string probe = $"handle:{handle.DangerousGetHandle()}|{id.VolumeSerial}|{((ulong)id.IndexHigh << 32) | id.IndexLow}";
                var unlisted = Parse(RunProbes(container, exe, [probe]));
                var listed = Parse(RunProbes(container, exe, [probe], [handle]));
                string a = unlisted.GetValueOrDefault(probe, "missing"), b = listed.GetValueOrDefault(probe, "missing");
                return ($"unlisted={a}; listedControl={b}", a.StartsWith("denied", StringComparison.Ordinal) && b.StartsWith("allowed", StringComparison.Ordinal));
            });
            container.Close(deleteProfile: true);
        }

        X05Pipes(staged, exe, name);
        X06ParentCrash(staged, exe, name);
        X07(staged, exe);
    }

    private static void SetInheritable(SafeFileHandle handle)
    {
        if (!NativeInherit.SetHandleInformation(handle.DangerousGetHandle(), 1, 1)) throw new System.ComponentModel.Win32Exception();
    }

    private static void X04(ContainerHost container, string exe)
    {
        // Parent controls prove the path works outside the container.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var udpReceiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        bool loopControl;
        using (var control = new TcpClient()) { control.Connect(IPAddress.Loopback, port); using var accepted = listener.AcceptTcpClient(); loopControl = true; }
        string internetControl = Control(() => { using var c = new TcpClient(); if (!c.ConnectAsync("1.1.1.1", 443).Wait(3000)) throw new TimeoutException(); });
        string dnsControl = Control(() => { if (!Dns.GetHostAddressesAsync("example.com").Wait(3000)) throw new TimeoutException(); });
        string gateway = FindDnsServer();
        string lanControl = Control(() =>
        {
            using var udp = new UdpClient();
            udp.Send(XProbeChild.DnsQuery("example.com"), new IPEndPoint(IPAddress.Parse(gateway), 53));
            var receive = udp.ReceiveAsync();
            if (!receive.Wait(3000)) throw new TimeoutException();
        });
        var probes = new[] { "tcp:1.1.1.1:443", "dns:example.com", $"udpdns:{gateway}:53", $"tcp:127.0.0.1:{port}", $"udp:127.0.0.1:{port}" };
        var results = Parse(RunProbes(container, exe, probes));
        Thread.Sleep(300);
        bool tcpDelivered = listener.Pending(), udpDelivered = udpReceiver.Available > 0;
        Record("X04", "direct Internet TCP 1.1.1.1:443", $"denied (parent control {internetControl})", results.GetValueOrDefault(probes[0], "missing"), results.GetValueOrDefault(probes[0], "").StartsWith("denied", StringComparison.Ordinal) && internetControl == "ok");
        Record("X04", "direct DNS resolution", $"denied (parent control {dnsControl})", results.GetValueOrDefault(probes[1], "missing"), results.GetValueOrDefault(probes[1], "").StartsWith("denied", StringComparison.Ordinal) && dnsControl == "ok");
        Record("X04", $"direct LAN UDP to {gateway}:53", $"denied (parent control {lanControl})", results.GetValueOrDefault(probes[2], "missing"), results.GetValueOrDefault(probes[2], "").StartsWith("denied", StringComparison.Ordinal) && lanControl == "ok");
        Record("X04", "direct loopback TCP", $"no connection (parent control {loopControl})", $"{results.GetValueOrDefault(probes[3], "missing")}; receiverAccepted={tcpDelivered}", !tcpDelivered && results.GetValueOrDefault(probes[3], "").StartsWith("denied", StringComparison.Ordinal));
        Record("X04", "direct loopback UDP", "no datagram delivered", $"{results.GetValueOrDefault(probes[4], "missing")}; receiverGotDatagram={udpDelivered}", !udpDelivered);
        Try("X04", "no loopback exemption configured for the container", "container absent from LoopbackExempt list", () =>
        {
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "CheckNetIsolation.exe"), "LoopbackExempt -s") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var process = Process.Start(info)!;
            string text = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10000);
            bool listed = text.Contains(container.SidText, StringComparison.OrdinalIgnoreCase) || text.Contains(container.Name, StringComparison.OrdinalIgnoreCase);
            return ($"listed={listed}", !listed);
        });
    }

    private static string Control(Action action) { try { action(); return "ok"; } catch (Exception error) { return $"failed {error.GetType().Name}"; } }

    private static string FindDnsServer()
    {
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            foreach (var dns in nic.GetIPProperties().DnsAddresses)
                if (dns.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(dns)) return dns.ToString();
        return "10.0.2.3";
    }

    private static void X05Pipes(string staged, string exe, string name)
    {
        using var container = ContainerHost.Open($"{name}.x5", staged, allowExisting: false);
        string me = WindowsIdentity.GetCurrent().User!.Value;

        Try("X05", "pipe DACL: other users and non-matching containers cannot open the pipe", "different container SID denied; DACL lists only user + this container", () =>
        {
            // Lowbox access checks intersect the user check and the package check, so the
            // DACL must name the user; the only broader principals it could leak to are checked here.
            string pipeName = $"Susu.F00.X5.{Guid.NewGuid():N}";
            using var server = new NamedPipeServerStream(PipeDirection.InOut, true, false, container.CreatePipe(@"\\.\pipe\" + pipeName, me));
            using var other = ContainerHost.Open($"{name}.x5other", staged, allowExisting: false);
            string outcome = Parse(RunProbes(other, exe, [$"pipe:{pipeName}"])).GetValueOrDefault($"pipe:{pipeName}", "missing");
            other.Close(deleteProfile: true);
            return ($"otherContainer={outcome}; connected={server.IsConnected}", outcome.StartsWith("denied", StringComparison.Ordinal) && !server.IsConnected);
        });
        Try("X05", "same-user plain process and same-SID container process not launched by the host are rejected", "both verdict 2 (PID mismatch) and disconnected", () =>
        {
            string pipeName = $"Susu.F00.X5.{Guid.NewGuid():N}";
            using var server = new NamedPipeServerStream(PipeDirection.InOut, true, false, container.CreatePipe(@"\\.\pipe\" + pipeName, me));
            // The legitimately launched child (idle, never connects) is the only accepted peer.
            using var legit = container.Start(exe, "--x-probe-idle", [], null, null, 128UL << 20);
            var verdicts = new List<string>();
            using (var plainOut = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable))
            {
                var (plain, plainPid) = ContainerHost.StartPlain(exe, $"--x-probe pipe:{pipeName}", plainOut.ClientSafePipeHandle);
                using (plain)
                {
                    plainOut.DisposeLocalCopyOfClientHandle();
                    if (!server.WaitForConnectionAsync().Wait(5000)) return ("plain rogue never connected", false);
                    int v = container.Verify(server.SafePipeHandle, legit.Process, out int pid);
                    server.Disconnect();
                    verdicts.Add($"plain verdict={v} pid={pid}/{plainPid}");
                    if (v != 2 || pid != plainPid) return (string.Join("; ", verdicts), false);
                }
            }
            using var output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            using var rogue = container.Start(exe, $"--x-probe pipe:{pipeName}", [output.ClientSafePipeHandle], null, output.ClientSafePipeHandle, 128UL << 20);
            output.DisposeLocalCopyOfClientHandle();
            if (!server.WaitForConnectionAsync().Wait(5000)) return (string.Join("; ", verdicts) + "; container rogue never connected", false);
            int verdict = container.Verify(server.SafePipeHandle, legit.Process, out int clientPid);
            server.Disconnect();
            verdicts.Add($"container verdict={verdict} pid={clientPid}/{rogue.Pid}");
            return (string.Join("; ", verdicts), verdict == 2 && clientPid == rogue.Pid);
        });
        Try("X05", "host session start survives a rogue client that connects first", "rogue rejected, legitimate child authenticated", () =>
        {
            // A same-user rogue connects to the fresh instance before the child is launched.
            SafeProcessHandle? rogue = null;
            using var session = HostSession.Start(new HostSession.Options(exe, staged, "quickjs", BeforeLaunch: pipeName =>
            {
                rogue = ContainerHost.StartPlain(exe, $"--x-probe pipe:{pipeName}", null).Process;
                Thread.Sleep(400);
            }));
            rogue?.Dispose();
            var loaded = session.Load("bench", "plugins/bench");
            return ($"started; rejectedClients={session.RejectedClients}; loaded={loaded.Ok}", loaded.Ok && session.RejectedClients >= 1);
        });
        Try("X05", "child refuses a server whose PID is not the launcher's", "child exits 20 before sending Hello", () =>
        {
            string pipeName = $"Susu.F00.X5.{Guid.NewGuid():N}";
            using var server = new NamedPipeServerStream(PipeDirection.InOut, true, false, container.CreatePipe(@"\\.\pipe\" + pipeName, me));
            using var nonce = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
            using var output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            int wrongPid = Environment.ProcessId + 4; // not the pipe server
            using var child = container.Start(exe, $"--plugin-host {pipeName} {wrongPid} quickjs", [nonce.ClientSafePipeHandle, output.ClientSafePipeHandle], nonce.ClientSafePipeHandle, output.ClientSafePipeHandle, 256UL << 20);
            nonce.DisposeLocalCopyOfClientHandle(); output.DisposeLocalCopyOfClientHandle();
            using (var writer = new StreamWriter(nonce)) writer.WriteLine("0123456789ABCDEF");
            var text = new StreamReader(output).ReadToEndAsync();
            bool connected = server.WaitForConnectionAsync().Wait(5000);
            byte[] probe = new byte[4];
            var read = connected ? server.ReadAsync(probe).AsTask() : Task.FromResult(0);
            bool exited = child.WaitForExit(5000);
            int received = read.Wait(1000) ? read.Result : -1;
            NativeInherit.GetExitCodeProcess(child.Process.DangerousGetHandle(), out uint code);
            return ($"exit={code}; helloBytes={received}; diagnostics={(text.Wait(1000) ? text.Result.Trim() : "")}", exited && code == 20 && received <= 0);
        });
        Try("X05", "pre-created (squatted) pipe name blocks host startup", "CreateNamedPipe first-instance failure; no child launched", () =>
        {
            string pipeName = $"Susu.F00.X5.{Guid.NewGuid():N}";
            using var output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            var (squatter, _) = ContainerHost.StartPlain(exe, $"--x-probe squat:{pipeName}", output.ClientSafePipeHandle);
            using (squatter)
            {
                output.DisposeLocalCopyOfClientHandle();
                using var reader = new StreamReader(output);
                string? ready = reader.ReadLine();
                string observed;
                bool failed;
                try { using var p = container.CreatePipe(@"\\.\pipe\" + pipeName, me); observed = "created"; failed = false; }
                catch (Exception error) { observed = $"{error.GetType().Name} 0x{error.HResult:X8}"; failed = true; }
                NativeInherit.TerminateProcess(squatter.DangerousGetHandle(), 1);
                return ($"squatter={ready}; hostCreate={observed}", ready == "squat=ready" && failed);
            }
        });
        Try("X05", "remote (SMB) pipe clients rejected; control without flag connects", "flagged: denied; control: connected", () =>
        {
            string flagged = $"Susu.F00.X5R.{Guid.NewGuid():N}", control = $"Susu.F00.X5C.{Guid.NewGuid():N}";
            using var a = new NamedPipeServerStream(PipeDirection.InOut, true, false, ContainerHost.CreateTestPipe(@"\\.\pipe\" + flagged, me, rejectRemote: true));
            using var b = new NamedPipeServerStream(PipeDirection.InOut, true, false, ContainerHost.CreateTestPipe(@"\\.\pipe\" + control, me, rejectRemote: false));
            var waitB = b.WaitForConnectionAsync();
            string rejected = Control(() => { using var c = new NamedPipeClientStream("127.0.0.1", flagged, PipeDirection.InOut); c.Connect(3000); });
            string accepted = Control(() => { using var c = new NamedPipeClientStream("127.0.0.1", control, PipeDirection.InOut); c.Connect(3000); });
            bool controlOk = accepted == "ok";
            return ($"flagged={rejected}; control={accepted}", rejected != "ok" && controlOk);
        });
        container.Close(deleteProfile: true);
    }

    private static string PlainProbe(string exe, string probe)
    {
        using var output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var (process, _) = ContainerHost.StartPlain(exe, $"--x-probe {probe}", output.ClientSafePipeHandle);
        using (process)
        {
            output.DisposeLocalCopyOfClientHandle();
            string text = new StreamReader(output).ReadToEnd();
            return Parse(text).GetValueOrDefault(probe, $"missing: {text.Trim()}");
        }
    }

    private static void X06ParentCrash(string staged, string exe, string name)
    {
        Try("X06", "abnormal parent termination kills the container child (Job kill-on-close)", "child exits within 2 s of parent TerminateProcess", () =>
        {
            string profile = $"{name}.x6";
            using var output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            var (parent, _) = ContainerHost.StartPlain(exe, $"--x06-parent {Quote(staged)} {profile}", output.ClientSafePipeHandle);
            using (parent)
            {
                output.DisposeLocalCopyOfClientHandle();
                using var reader = new StreamReader(output);
                string? line = reader.ReadLine();
                if (line is null || !line.StartsWith("child=", StringComparison.Ordinal)) return ($"parent output: {line}", false);
                int pid = int.Parse(line[6..], System.Globalization.CultureInfo.InvariantCulture);
                using var child = Process.GetProcessById(pid);
                var timer = Stopwatch.StartNew();
                NativeInherit.TerminateProcess(parent.DangerousGetHandle(), 99);
                bool exited = child.WaitForExit(2000);
                double ms = timer.Elapsed.TotalMilliseconds;
                // The crashed parent could not clean up: startup recovery must remove stale profiles.
                var stale = ContainerHost.Open(profile, staged, allowExisting: true);
                bool wasStale = !stale.Created;
                stale.Close(deleteProfile: true);
                return ($"childExited={exited} after {ms:F0} ms; staleProfileLeft={wasStale} (removed by recovery)", exited);
            }
        });
    }

    /// <summary>Child of the crash test: hosts one container child, reports its PID, then waits to be killed.</summary>
    public static int CrashParent(string staged, string profile)
    {
        string exe = Path.Combine(staged, Path.GetFileName(Environment.ProcessPath!));
        var container = ContainerHost.Open(profile, staged, allowExisting: false);
        var child = container.Start(exe, "--x-probe-idle", [], null, null, 128UL << 20);
        Console.WriteLine($"child={child.Pid}");
        Console.Out.Flush();
        Thread.Sleep(60000);
        GC.KeepAlive(child);
        return 0;
    }

    private static void X07(string staged, string exe)
    {
        using var local = new TcpListener(IPAddress.Loopback, 0);
        local.Start();
        int port = ((IPEndPoint)local.LocalEndpoint).Port;
        string localOrigin = $"http://127.0.0.1:{port}";
        using var cts = new CancellationTokenSource();
        var server = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                using var client = await local.AcceptTcpClientAsync(cts.Token);
                using var stream = client.GetStream();
                byte[] buffer = new byte[4096];
                _ = await stream.ReadAsync(buffer, cts.Token);
                byte[] body = Encoding.UTF8.GetBytes("{\"fixture\":\"ollama-like local service\"}");
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), cts.Token);
                await stream.WriteAsync(body, cts.Token);
            }
        });
        try
        {
            using var session = HostSession.Start(new HostSession.Options(exe, staged, "quickjs"));
            session.Broker.ApproveLocalOrigin(Broker.Origin(new Uri(localOrigin))!);
            var loaded = session.Load("malicious", "plugins/malicious");
            Record("X07", "adversarial plugin loads", "loaded", loaded.Ok ? "loaded" : loaded.Error ?? "failed", loaded.Ok);
            var call = session.Invoke("malicious", "probe", JsonSerializer.Serialize(new LocalRequest($"{localOrigin}/api/tags"), XJson.Default.LocalRequest), ["https://api.bench.example", localOrigin]);
            if (!call.Result.Wait(10000)) { Record("X07", "adversarial probe completes", "completed", "timeout", false); return; }
            var payload = call.Result.Result.Payload!.Value.Deserialize(PluginJson.Default.CompletedPayload)!;
            if (!payload.Ok || payload.Result is null) { Record("X07", "adversarial probe completes", "completed", payload.Error?.Detail ?? "failed", false); return; }
            foreach (var property in payload.Result.Value.EnumerateObject())
            {
                string value = property.Value.GetString() ?? "";
                bool shouldAllow = property.Name == "approvedLocalOrigin";
                Record("X07", $"plugin attempt {property.Name}", shouldAllow ? "allowed through host proxy" : "denied", value, shouldAllow ? value == "allowed" : value.StartsWith("denied", StringComparison.Ordinal));
            }
            Record("X07", "grant revoked when the call terminates", "0 active grants", $"{session.Broker.ActiveGrants} active", session.Broker.ActiveGrants == 0);
        }
        catch (Exception error) { Record("X07", "adversarial plugin session", "runs", $"exception {error.GetType().Name}: {error.Message}", false); }
        finally { cts.Cancel(); local.Stop(); try { server.Wait(1000); } catch (AggregateException) { } }

        Try("X07", "compromised host process: forged grant/plugin/call, revoked grant, late and unknown frames", "all forged API calls denied; late frame dropped; unknown type disconnects", () =>
        {
            using var session = HostSession.Start(new HostSession.Options(exe, staged, "quickjs", ChildMode: "--plugin-host-adversarial"));
            var call = session.Invoke("bench", "translate", "{\"text\":\"x\"}", ["https://api.bench.example"]);
            bool completed = call.Result.Wait(5000);
            var deadline = Stopwatch.StartNew();
            while (session.ReaderError is null && deadline.ElapsedMilliseconds < 5000) Thread.Sleep(50);
            string[] denies;
            lock (session.Broker.Decisions) denies = [.. session.Broker.Decisions.Where(d => d.StartsWith("deny", StringComparison.Ordinal))];
            bool ok = completed && denies.Length == 4 && session.Broker.Decisions.Count == 4 && session.DroppedFrames >= 1 && session.ReaderError is not null;
            return ($"denies=[{string.Join("; ", denies)}]; dropped={session.DroppedFrames}; reader={session.ReaderError}", ok);
        });
    }

    internal sealed record LocalRequest(string LocalUrl);
    internal sealed record XReport(DateTimeOffset Timestamp, string OS, string Scope, Case[] Cases);

    [JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(XReport))]
    [JsonSerializable(typeof(LocalRequest))]
    internal partial class XJson : JsonSerializerContext;
}

internal static partial class NativeInherit
{
    [System.Runtime.InteropServices.LibraryImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    internal static partial bool SetHandleInformation(nint handle, uint mask, uint flags);
    [System.Runtime.InteropServices.LibraryImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    internal static partial bool TerminateProcess(nint process, uint code);
    [System.Runtime.InteropServices.LibraryImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    internal static partial bool GetExitCodeProcess(nint process, out uint code);
}
