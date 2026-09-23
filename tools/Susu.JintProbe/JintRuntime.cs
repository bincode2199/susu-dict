using System.Text;
using Jint;
using Jint.Native;
using Jint.Native.Json;
using Jint.Runtime;
using Jint.Runtime.Interop;
using Jint.Runtime.Modules;

namespace Susu.Probes.PluginHost;

/// <summary>
/// Jint comparison runtime for PER01: same plugin source, bootstrap, IPC and budget
/// semantics as the QuickJS route. Built only in the separate comparison executable.
/// </summary>
internal sealed class JintRuntime : IPluginRuntime
{
    private sealed class BudgetExceededException() : Exception("execution budget exceeded");

    private sealed class BudgetConstraint(ExecutionBudget budget) : Constraint
    {
        public override void Check() { if (budget.Expired) throw new BudgetExceededException(); }
        public override void Reset() { }
    }

    /// <summary>"./" and "../" specifiers inside the plugin root only.</summary>
    private sealed class PluginModuleLoader(string root) : ModuleLoader
    {
        public override ResolvedSpecifier Resolve(string? referencingModuleLocation, ModuleRequest moduleRequest)
        {
            string specifier = moduleRequest.Specifier;
            if (!(specifier.StartsWith("./", StringComparison.Ordinal) || specifier.StartsWith("../", StringComparison.Ordinal)) || specifier.Contains('\\') || specifier.Contains(':'))
                throw new JavaScriptException("module specifier not allowed");
            string baseDirectory = referencingModuleLocation is null ? "" : Path.GetDirectoryName(referencingModuleLocation.Replace('/', Path.DirectorySeparatorChar)) ?? "";
            string relative = Path.GetRelativePath(root, Path.GetFullPath(Path.Combine(root, baseDirectory, specifier)));
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) throw new JavaScriptException("module escapes plugin root");
            string key = relative.Replace(Path.DirectorySeparatorChar, '/');
            return new ResolvedSpecifier(moduleRequest, key, null, SpecifierType.Bare);
        }

        protected override string LoadModuleContents(Engine engine, ResolvedSpecifier resolved)
            => PluginFiles.Resolve(root, resolved.Key) is { } path ? File.ReadAllText(path, Encoding.UTF8) : throw new JavaScriptException("module not found in plugin package");
    }

    private const string Bootstrap = "(function(host){'use strict';" +
        "const KINDS=new Set(['auth','quota','rate_limited','network','timeout','unsupported_language','bad_response']);" +
        "class PluginError extends Error{constructor(kind,detail){super(String(kind));this.kind=kind;this.detail=detail;}}" +
        "Object.defineProperty(globalThis,'PluginError',{value:PluginError,writable:false,configurable:false});" +
        "const calls=new Map();" +
        "function makeSignal(){const listeners=[];const s={aborted:false,reason:undefined,onabort:null," +
        "addEventListener(t,f){if(t==='abort'&&typeof f==='function')listeners.push(f);}," +
        "removeEventListener(t,f){const i=listeners.indexOf(f);if(i>=0)listeners.splice(i,1);}," +
        "throwIfAborted(){if(s.aborted)throw s.reason;}};" +
        "return {signal:s,abort(reason){if(s.aborted)return;s.aborted=true;s.reason=reason;for(const f of listeners.slice()){try{f({type:'abort'});}catch(_){}}if(typeof s.onabort==='function'){try{s.onabort({type:'abort'});}catch(_){}}}};}" +
        "function toError(e){if(e&&typeof e==='object'&&typeof e.kind==='string'&&KINDS.has(e.kind))return {kind:e.kind,detail:e.detail===undefined?undefined:String(e.detail).slice(0,1024)};" +
        "if(e&&e.kind==='cancelled')return {kind:'cancelled'};return {kind:'bad_response',detail:String(e&&e.message||e).slice(0,1024)};}" +
        "function finish(id,message){calls.delete(id);let text;try{text=JSON.stringify(message);}catch(e){text=JSON.stringify({callId:id,ok:false,error:{kind:'bad_response',detail:'result is not JSON'}});}host(2,text);}" +
        "function invoke(plugin,id,cap,req,config){const c=makeSignal();calls.set(id,c);" +
        "const api=(op,args)=>c.signal.aborted?Promise.reject(c.signal.reason):host(1,JSON.stringify({callId:id,op,args}));" +
        "const ctx=Object.freeze({config:Object.freeze(config||{}),signal:c.signal,lang:Object.freeze({from:'en',to:'zh-Hans'})," +
        "$http:(r)=>api('http',r),$store:Object.freeze({get:(k)=>api('store.get',{key:k}),set:(k,v)=>api('store.set',{key:k,value:v})})," +
        "$log:(level,msg)=>{host(3,JSON.stringify({callId:id,level:String(level),msg:String(msg).slice(0,4096)}));}});" +
        "Promise.resolve().then(()=>{const f=plugin&&plugin[cap];if(typeof f!=='function')throw new PluginError('bad_response','capability not exported');return f.call(plugin,req,ctx);})" +
        ".then(r=>finish(id,{callId:id,ok:true,result:r}),e=>finish(id,{callId:id,ok:false,error:toError(e)}));}" +
        "function abort(id){const c=calls.get(id);if(c)c.abort({name:'AbortError',kind:'cancelled'});}" +
        "return [invoke,abort];})";

    private readonly string pluginId;
    private readonly ExecutionBudget budget;
    private readonly IRuntimeCallbacks callbacks;
    private readonly Engine engine;
    private readonly JsValue invoke;
    private readonly JsValue abort;
    private readonly Dictionary<int, (Action<JsValue> Resolve, Action<JsValue> Reject)> pending = [];
    private JsValue plugin = JsValue.Undefined;
    private int nextId;

    public static IPluginRuntime Create(string pluginId, string root, int memoryMiB, ExecutionBudget budget, IRuntimeCallbacks callbacks)
        => new JintRuntime(pluginId, root, memoryMiB, budget, callbacks);

    private JintRuntime(string pluginId, string root, int memoryMiB, ExecutionBudget budget, IRuntimeCallbacks callbacks)
    {
        this.pluginId = pluginId; this.budget = budget; this.callbacks = callbacks;
        budget.Stop();
        engine = new Engine(options =>
        {
            options.Strict = true;
            options.DisableStringCompilation();
            options.LimitMemory((long)memoryMiB * 1024 * 1024);
            options.Constraint(new BudgetConstraint(budget));
            options.EnableModules(new PluginModuleLoader(root));
        });
        var host = new ClrFunction(engine, "host", Host);
        var pair = engine.Invoke(engine.Evaluate(Bootstrap), host);
        invoke = pair.Get(0);
        abort = pair.Get(1);
    }

    private JsValue Host(JsValue self, JsValue[] arguments)
    {
        int kind = (int)TypeConverter.ToNumber(arguments.Length > 0 ? arguments[0] : JsValue.Undefined);
        string text = TypeConverter.ToString(arguments.Length > 1 ? arguments[1] : JsValue.Undefined);
        if (Encoding.UTF8.GetByteCount(text) > 1024 * 1024) throw new JavaScriptException("host message exceeds 1 MiB");
        switch (kind)
        {
            case 1:
                var (promise, resolve, reject) = engine.Advanced.RegisterPromise();
                int id = ++nextId;
                pending[id] = (resolve, reject);
                if (callbacks.ApiCall(pluginId, id, text) != 0)
                {
                    pending.Remove(id);
                    reject(new JsonParser(engine).Parse("{\"kind\":\"bad_response\"}"));
                }
                return promise;
            case 2: callbacks.Completed(pluginId, text); return JsValue.Undefined;
            case 3: callbacks.Log(pluginId, text); return JsValue.Undefined;
            default: return JsValue.Undefined;
        }
    }

    /// <summary>Runs host work with the same status contract as the QuickJS bridge.</summary>
    private int Slice(Action action, bool drain = true)
    {
        try
        {
            action();
            if (drain) engine.Advanced.ProcessTasks();
            return 0;
        }
        catch (BudgetExceededException) { return 2; }
        catch (Exception) when (budget.Expired) { return 2; }
        catch (JavaScriptException) { return 1; }
        finally { budget.Stop(); }
    }

    public string? Load(string entry, out int status)
    {
        string? error = null;
        budget.Start(budget.SliceTicks * 10);
        try
        {
            var ns = engine.Modules.Import("./" + entry);
            plugin = ns.Get("default");
            engine.Advanced.ProcessTasks();
            status = plugin.IsObject() ? 0 : 3;
            if (status == 3) error = "main.js has no default export object";
        }
        catch (BudgetExceededException) { status = 2; }
        catch (Exception e) { status = 1; error = e.Message; }
        finally { budget.Stop(); }
        return error;
    }

    public int Invoke(int callId, string capability, string requestJson, string configJson)
    {
        JsValue request, config;
        try { request = new JsonParser(engine).Parse(requestJson); config = new JsonParser(engine).Parse(configJson); }
        catch (Exception) { return 1; }
        budget.Start(); // slice starts after host JSON materialization, as in the QuickJS route
        return Slice(() => engine.Invoke(invoke, plugin, callId, capability, request, config));
    }

    public int Settle(int apiId, bool ok, string json)
    {
        if (!pending.Remove(apiId, out var functions)) return 1;
        JsValue value;
        try { value = new JsonParser(engine).Parse(json); }
        catch (Exception) { value = new JsonParser(engine).Parse("{\"kind\":\"bad_response\"}"); ok = false; }
        budget.Start();
        return Slice(() => (ok ? functions.Resolve : functions.Reject)(value));
    }

    public int Abort(int callId)
    {
        budget.Start();
        return Slice(() => engine.Invoke(abort, callId));
    }

    public long EngineBytes => 0; // not attributable per runtime in a managed engine; PER01 uses process PWS

    public void Dispose() => engine.Dispose();
}
