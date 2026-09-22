using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

internal static unsafe partial class QuickJsProbe
{
    [LibraryImport("susu_quickjs", EntryPoint="susu_qjs_new")]
    private static partial nint New(nuint memory, delegate* unmanaged[Cdecl]<nint, int> interrupt, nint state);
    [LibraryImport("susu_quickjs", EntryPoint="susu_qjs_free")]
    private static partial void Free(nint engine);
    [LibraryImport("susu_quickjs", EntryPoint="susu_qjs_eval", StringMarshalling=StringMarshalling.Utf8)]
    private static partial int Eval(nint engine, string source, nuint length, int module, byte* output, nuint capacity);
    [LibraryImport("susu_quickjs", EntryPoint="susu_qjs_drain")]
    private static partial int Drain(nint engine);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Interrupt(nint deadline) => Stopwatch.GetTimestamp() >= *(long*)deadline ? 1 : 0;

    internal static void Run()
    {
        long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
        nint engine = New(16 * 1024 * 1024, &Interrupt, (nint)(&deadline));
        if (engine == 0) throw new InvalidOperationException("QuickJS initialization failed.");
        try
        {
            string Evaluate(string code, int module = 0)
            {
                byte* buffer = stackalloc byte[4096];
                int status = Eval(engine, code, (nuint)Encoding.UTF8.GetByteCount(code), module, buffer, 4096);
                if (status != 0) throw new InvalidOperationException($"QuickJS status {status}: {Marshal.PtrToStringUTF8((nint)buffer)}");
                return Marshal.PtrToStringUTF8((nint)buffer)!;
            }
            if (Evaluate("JSON.stringify({text:'中文',value:21*2})") != "{\"text\":\"中文\",\"value\":42}") throw new InvalidOperationException("QuickJS result mismatch.");
            if (Evaluate("[typeof eval,typeof Function,typeof (()=>{}).constructor,typeof (async()=>{}).constructor].join(',')") != "undefined,undefined,undefined,undefined") throw new InvalidOperationException("Dynamic compiler still exposed.");
            Evaluate("export const value=42; globalThis.moduleResult=value;", 1);
            if (Drain(engine) < 0 || Evaluate("moduleResult") != "42") throw new InvalidOperationException("ES module failed.");
            Evaluate("Promise.resolve(42).then(v=>globalThis.promiseResult=v)");
            if (Drain(engine) < 0 || Evaluate("promiseResult") != "42") throw new InvalidOperationException("Promise failed.");
            byte* limited = stackalloc byte[4096];
            const string allocation = "new Uint8Array(32*1024*1024)";
            if (Eval(engine, allocation, (nuint)allocation.Length, 0, limited, 4096) != 1) throw new InvalidOperationException("Memory limit was not enforced.");
            deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10;
            byte* output = stackalloc byte[4096];
            const string loop = "for(;;){}";
            if (Eval(engine, loop, (nuint)loop.Length, 0, output, 4096) != 1) throw new InvalidOperationException("Infinite loop was not interrupted.");
        }
        finally { Free(engine); }
        deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 5;
        engine = New(16 * 1024 * 1024, &Interrupt, (nint)(&deadline));
        if (engine == 0) throw new InvalidOperationException("QuickJS microtask runtime initialization failed.");
        try
        {
            const string chain = "function next(){Promise.resolve().then(next)};next()";
            byte* output = stackalloc byte[4096];
            if (Eval(engine, chain, (nuint)chain.Length, 0, output, 4096) != 0) throw new InvalidOperationException("Microtask setup failed.");
            if (Drain(engine) >= 0) throw new InvalidOperationException("Infinite microtask chain was not interrupted.");
        }
        finally { Free(engine); }
    }
}
