using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Susu.Windows;

/// <summary>F00 disposable AppContainer launcher (see native/container.cpp). Prototype for F04.</summary>
public sealed partial class ContainerHost : IDisposable
{
    [LibraryImport("susu_windows_probe", EntryPoint = "susu_container_open", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int Open(string name, string resource, int allowExisting, out nint handle, out int created);
    [LibraryImport("susu_windows_probe", EntryPoint = "susu_container_sid")]
    private static unsafe partial int Sid(nint handle, char* text, uint capacity);
    [LibraryImport("susu_windows_probe", EntryPoint = "susu_container_close")]
    private static partial int Close(nint handle, int deleteProfile);
    [LibraryImport("susu_windows_probe", EntryPoint = "susu_container_pipe", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int Pipe(nint handle, string name, string? extraSid, int rejectRemote, out nint pipe);
    [LibraryImport("susu_windows_probe", EntryPoint = "susu_container_launch", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int Launch(nint handle, string executable, string arguments, nint* inherit, int inheritCount, nint stdIn, nint stdOut, ulong memoryLimit, out nint process, out nint job, out uint pid);
    [LibraryImport("susu_windows_probe", EntryPoint = "susu_container_verify_client")]
    private static partial int VerifyClient(nint handle, nint pipe, nint process, out int result, out uint clientPid);
    [LibraryImport("susu_windows_probe", EntryPoint = "susu_pipe_server_pid")]
    private static partial int ServerPid(nint pipe, out uint pid);
    [LibraryImport("susu_windows_probe", EntryPoint = "susu_launch_plain", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int LaunchPlain(string executable, string arguments, nint stdOut, out nint process, out uint pid);
    [LibraryImport("susu_windows_probe", EntryPoint = "susu_process_memory")]
    private static partial int Memory(nint process, out ulong privateWorkingSet, out ulong privateBytes, out ulong peakPrivateBytes);

    private nint handle;
    public string Name { get; }
    public bool Created { get; }
    public string SidText { get; }

    private ContainerHost(string name, nint handle, bool created, string sid) { Name = name; this.handle = handle; Created = created; SidText = sid; }

    public static unsafe ContainerHost Open(string name, string resourceDirectory, bool allowExisting)
    {
        Marshal.ThrowExceptionForHR(Open(name, resourceDirectory, allowExisting ? 1 : 0, out nint handle, out int created));
        char* text = stackalloc char[256];
        int hr = Sid(handle, text, 256);
        if (hr < 0) { Close(handle, created); Marshal.ThrowExceptionForHR(hr); }
        return new ContainerHost(name, handle, created == 1, new string(text));
    }

    public SafePipeHandle CreatePipe(string pipeName, string? extraSid = null, bool rejectRemote = true)
    {
        Marshal.ThrowExceptionForHR(Pipe(handle, pipeName, extraSid, rejectRemote ? 1 : 0, out nint pipe));
        return new SafePipeHandle(pipe, true);
    }

    /// <summary>Creates a pipe with the same flags but a caller-chosen DACL; test controls only.</summary>
    public static SafePipeHandle CreateTestPipe(string pipeName, string sid, bool rejectRemote)
    {
        Marshal.ThrowExceptionForHR(Pipe(0, pipeName, sid, rejectRemote ? 1 : 0, out nint pipe));
        return new SafePipeHandle(pipe, true);
    }

    public unsafe ContainerProcess Start(string executable, string arguments, SafeHandle[] inherit, SafeHandle? stdIn, SafeHandle? stdOut, ulong memoryLimit)
    {
        nint* list = stackalloc nint[Math.Max(1, inherit.Length)];
        for (int i = 0; i < inherit.Length; i++) list[i] = inherit[i].DangerousGetHandle();
        int hr = Launch(handle, executable, arguments, list, inherit.Length, stdIn?.DangerousGetHandle() ?? 0, stdOut?.DangerousGetHandle() ?? 0, memoryLimit, out nint process, out nint job, out uint pid);
        Marshal.ThrowExceptionForHR(hr);
        return new ContainerProcess(new SafeProcessHandle(process, true), new SafeWaitHandle(job, true), (int)pid);
    }

    /// <summary>1 ok, 2 PID mismatch, 3 not AppContainer, 4 SID mismatch.</summary>
    public int Verify(SafePipeHandle pipe, SafeProcessHandle expected, out int clientPid)
    {
        Marshal.ThrowExceptionForHR(VerifyClient(handle, pipe.DangerousGetHandle(), expected.DangerousGetHandle(), out int result, out uint pid));
        clientPid = (int)pid;
        return result;
    }

    public static int GetServerPid(SafePipeHandle pipe)
    {
        Marshal.ThrowExceptionForHR(ServerPid(pipe.DangerousGetHandle(), out uint pid));
        return (int)pid;
    }

    public static (SafeProcessHandle Process, int Pid) StartPlain(string executable, string arguments, SafeHandle? stdOut)
    {
        Marshal.ThrowExceptionForHR(LaunchPlain(executable, arguments, stdOut?.DangerousGetHandle() ?? 0, out nint process, out uint pid));
        return (new SafeProcessHandle(process, true), (int)pid);
    }

    public static (ulong PrivateWorkingSet, ulong PrivateBytes, ulong PeakPrivateBytes) ProcessMemory(SafeProcessHandle process)
    {
        Marshal.ThrowExceptionForHR(Memory(process.DangerousGetHandle(), out ulong pws, out ulong privateBytes, out ulong peak));
        return (pws, privateBytes, peak);
    }

    /// <summary>Restores the resource ACL; deleteProfile=true is the uninstall path.</summary>
    public void Close(bool deleteProfile)
    {
        if (handle == 0) return;
        int hr = Close(handle, deleteProfile ? 1 : 0);
        handle = 0;
        Marshal.ThrowExceptionForHR(hr);
    }

    public void Dispose() { if (handle != 0) { Close(handle, Created ? 1 : 0); handle = 0; } }
}

public sealed class ContainerProcess(SafeProcessHandle process, SafeWaitHandle job, int pid) : IDisposable
{
    public SafeProcessHandle Process { get; } = process;
    public SafeWaitHandle Job { get; } = job;
    public int Pid { get; } = pid;

    public bool WaitForExit(int milliseconds)
    {
        using var wait = new ProcessWaitHandle(Process);
        return wait.WaitOne(milliseconds);
    }

    public void Dispose() { Job.Dispose(); Process.Dispose(); }
}

public sealed class ProcessWaitHandle : WaitHandle
{
    public ProcessWaitHandle(SafeProcessHandle process)
    {
        bool added = false;
        process.DangerousAddRef(ref added);
        try { SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), false); }
        finally { if (added) process.DangerousRelease(); }
    }
}
