using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Susu.Runtime;

/// <summary>
/// QuickJS-NG runtime for one plugin package through the thin C ABI (native/quickjs-bridge), the
/// route validated in F00 (PER01/X01-X07). This is a straight migration of the F00 prototype
/// binding (tools/Susu.Probes/PluginHost/QuickJsRuntime.cs); the wire protocol moved to
/// Susu.Contracts.IpcEnvelope but the native contract is unchanged.
/// </summary>
public sealed unsafe partial class QuickJsRuntime : IPluginRuntime
{
    [LibraryImport("susu_quickjs", EntryPoint = "susu_qjs_new_plugin")]
    private static partial nint NewPlugin(nuint memory, delegate* unmanaged[Cdecl]<long*, int> interrupt,
        delegate* unmanaged[Cdecl]<long*, int, int, byte*, nuint, int> host,
        delegate* unmanaged[Cdecl]<long*, byte*, byte**, nuint*, int> readModule, long* opaque);
    [LibraryImport("susu_quickjs", EntryPoint = "susu_qjs_free")] private static partial void Free(nint engine);
    [LibraryImport("susu_quickjs", EntryPoint = "susu_qjs_load", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LoadNative(nint engine, string entry, byte* error, nuint capacity);
    [LibraryImport("susu_quickjs", EntryPoint = "susu_qjs_invoke", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int InvokeNative(nint engine, int callId, string capability, byte* request, nuint requestLength, byte* config, nuint configLength);
    [LibraryImport("susu_quickjs", EntryPoint = "susu_qjs_settle")]
    private static partial int SettleNative(nint engine, int id, int ok, byte* json, nuint length);
    [LibraryImport("susu_quickjs", EntryPoint = "susu_qjs_abort")] private static partial int AbortNative(nint engine, int callId);
    [LibraryImport("susu_quickjs", EntryPoint = "susu_qjs_memory")] private static partial long MemoryNative(nint engine);

    private readonly string pluginId;
    private readonly string root;
    private readonly ExecutionBudget budget;
    private readonly IRuntimeCallbacks callbacks;
    private readonly GCHandle self;
    private nint engine;
    private byte* moduleBuffer;

    public static IPluginRuntime Create(string pluginId, string root, int memoryMiB, ExecutionBudget budget, IRuntimeCallbacks callbacks)
        => new QuickJsRuntime(pluginId, root, memoryMiB, budget, callbacks);

    private QuickJsRuntime(string pluginId, string root, int memoryMiB, ExecutionBudget budget, IRuntimeCallbacks callbacks)
    {
        this.pluginId = pluginId; this.root = root; this.budget = budget; this.callbacks = callbacks;
        self = GCHandle.Alloc(this);
        budget.Cell[1] = GCHandle.ToIntPtr(self);
        budget.Stop();
        engine = NewPlugin((nuint)memoryMiB * 1024 * 1024, &Interrupt, &Host, &ReadModule, budget.Cell);
        if (engine == 0) { self.Free(); throw new InvalidOperationException("QuickJS plugin runtime creation failed."); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)]), MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int Interrupt(long* cell) => System.Diagnostics.Stopwatch.GetTimestamp() >= Volatile.Read(ref cell[0]) ? 1 : 0;

    private static QuickJsRuntime From(long* cell) => (QuickJsRuntime)GCHandle.FromIntPtr((nint)cell[1]).Target!;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)]), MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int Host(long* cell, int kind, int id, byte* json, nuint length)
    {
        try
        {
            var runtime = From(cell);
            string text = json == null ? "" : Encoding.UTF8.GetString(json, (int)length);
            switch (kind)
            {
                case 1: return runtime.callbacks.ApiCall(runtime.pluginId, id, text);
                case 2: runtime.callbacks.Completed(runtime.pluginId, text); return 0;
                case 3: runtime.callbacks.Log(runtime.pluginId, text); return 0;
                case 4: runtime.budget.Start(); return 0;
                default: return 1;
            }
        }
        catch { return 1; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)]), MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int ReadModule(long* cell, byte* name, byte** data, nuint* length)
    {
        try
        {
            var runtime = From(cell);
            string? path = PluginFiles.Resolve(runtime.root, Marshal.PtrToStringUTF8((nint)name) ?? "");
            if (path is null) return 1;
            byte[] bytes = File.ReadAllBytes(path);
            if (runtime.moduleBuffer != null) NativeMemory.Free(runtime.moduleBuffer);
            // QuickJS requires input[length] == 0.
            runtime.moduleBuffer = (byte*)NativeMemory.Alloc((nuint)bytes.Length + 1);
            runtime.moduleBuffer[bytes.Length] = 0;
            bytes.CopyTo(new Span<byte>(runtime.moduleBuffer, bytes.Length));
            *data = runtime.moduleBuffer;
            *length = (nuint)bytes.Length;
            return 0;
        }
        catch { return 1; }
    }

    public string? Load(string entry, out int status)
    {
        byte* error = stackalloc byte[1024];
        budget.Start(budget.SliceTicks * 10); // compile + top-level evaluation; recorded as 10x slice
        try { status = LoadNative(engine, entry, error, 1024); }
        finally { budget.Stop(); }
        return status == 0 ? null : status == 3 ? "main.js has no default export object" : Marshal.PtrToStringUTF8((nint)error);
    }

    public int Invoke(int callId, string capability, string requestJson, string configJson)
    {
        byte[] request = Z(requestJson), config = Z(configJson);
        budget.Start();
        try { fixed (byte* r = request) fixed (byte* c = config) return InvokeNative(engine, callId, capability, r, (nuint)request.Length - 1, c, (nuint)config.Length - 1); }
        finally { budget.Stop(); }
    }

    public int Settle(int apiId, bool ok, string json)
    {
        byte[] bytes = Z(json);
        budget.Start();
        try { fixed (byte* b = bytes) return SettleNative(engine, apiId, ok ? 1 : 0, b, (nuint)bytes.Length - 1); }
        finally { budget.Stop(); }
    }

    public int Abort(int callId)
    {
        budget.Start();
        try { return AbortNative(engine, callId); }
        finally { budget.Stop(); }
    }

    public long EngineBytes => MemoryNative(engine);

    // QuickJS parsers require a terminating NUL after the given length.
    private static byte[] Z(string text) => Encoding.UTF8.GetBytes(text + "\0");

    public void Dispose()
    {
        if (engine != 0) { Free(engine); engine = 0; }
        if (moduleBuffer != null) { NativeMemory.Free(moduleBuffer); moduleBuffer = null; }
        if (self.IsAllocated) self.Free();
    }
}
