#include "quickjs.h"
#include <stdlib.h>
#include <string.h>
#include <stdint.h>

#ifdef _WIN32
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __attribute__((visibility("default")))
#endif

typedef int (*interrupt_fn)(void *);
// kind 1: API call (id = promise id, returns promise to JS), 2: call completed, 3: log,
// 4: host JSON materialized, JS slice starts now (materialization is bounded by the 4 MiB cap
//    and is not interruptible, so it is excluded from the continuous-execution budget).
typedef int (*host_fn)(void *opaque, int kind, int id, const char *json, size_t length);
// Returns 0 and a caller-owned UTF-8 buffer valid until the next call, or nonzero if absent/denied.
typedef int (*read_module_fn)(void *opaque, const char *name, const char **data, size_t *length);

typedef struct {
    int id;
    JSValue resolve;
    JSValue reject;
} pending_call;

typedef struct {
    JSRuntime *runtime;
    JSContext *context;
    interrupt_fn interrupted;
    host_fn host;
    read_module_fn read_module;
    void *opaque;
    JSValue plugin;
    JSValue invoke;
    JSValue abort;
    pending_call *pending;
    int pending_count;
    int pending_capacity;
    int next_id;
} susu_engine;

static int interrupt(JSRuntime *runtime, void *opaque) {
    (void)runtime;
    susu_engine *engine = opaque;
    return engine->interrupted && engine->interrupted(engine->opaque);
}

EXPORT void susu_qjs_free(susu_engine *engine) {
    if (!engine) return;
    if (engine->context) {
        for (int i = 0; i < engine->pending_count; i++) {
            JS_FreeValue(engine->context, engine->pending[i].resolve);
            JS_FreeValue(engine->context, engine->pending[i].reject);
        }
        JS_FreeValue(engine->context, engine->plugin);
        JS_FreeValue(engine->context, engine->invoke);
        JS_FreeValue(engine->context, engine->abort);
        JS_FreeContext(engine->context);
    }
    free(engine->pending);
    if (engine->runtime) JS_FreeRuntime(engine->runtime);
    free(engine);
}

// Script-visible dynamic compilers are removed; the host keeps its C-level
// source compiler for plugin modules. No third-party bytecode is accepted.
static const char *lockdown = "for(const f of [function(){},async function(){},function*(){},async function*(){}])Object.defineProperty(Object.getPrototypeOf(f),'constructor',{value:undefined,writable:false,configurable:false});Object.defineProperty(globalThis,'eval',{value:undefined,writable:false,configurable:false});Object.defineProperty(globalThis,'Function',{value:undefined,writable:false,configurable:false});";

static susu_engine *create(size_t memory_limit, interrupt_fn interrupted, void *opaque) {
    susu_engine *engine = calloc(1, sizeof(*engine));
    if (!engine) return NULL;
    engine->plugin = JS_UNDEFINED;
    engine->invoke = JS_UNDEFINED;
    engine->abort = JS_UNDEFINED;
    engine->runtime = JS_NewRuntime();
    if (!engine->runtime) { free(engine); return NULL; }
    JS_SetMemoryLimit(engine->runtime, memory_limit);
    JS_SetMaxStackSize(engine->runtime, 512 * 1024);
    engine->interrupted = interrupted;
    engine->opaque = opaque;
    JS_SetInterruptHandler(engine->runtime, interrupt, engine);
    engine->context = JS_NewContext(engine->runtime);
    if (!engine->context) { susu_qjs_free(engine); return NULL; }
    JS_SetContextOpaque(engine->context, engine);
    return engine;
}

static int apply_lockdown(susu_engine *engine) {
    JSValue result = JS_Eval(engine->context, lockdown, strlen(lockdown), "<lockdown>", JS_EVAL_TYPE_GLOBAL);
    int failed = JS_IsException(result);
    JS_FreeValue(engine->context, result);
    return failed;
}

EXPORT susu_engine *susu_qjs_new(size_t memory_limit, interrupt_fn interrupted, void *opaque) {
    susu_engine *engine = create(memory_limit, interrupted, opaque);
    if (engine && apply_lockdown(engine)) { susu_qjs_free(engine); return NULL; }
    return engine;
}

// Caller owns the fixed UTF-8 output buffer. No JSValue or allocator crosses ABI.
// Returns 0 on success, 1 JS exception, 2 buffer overflow, 3 invalid input.
EXPORT int susu_qjs_eval(susu_engine *engine, const char *source, size_t length, int module, char *output, size_t capacity) {
    if (!engine || !source || !output || capacity == 0 || length > 1024 * 1024) return 3;
    JSContext *context = engine->context;
    JSValue result = JS_Eval(context, source, length, "probe.js", module ? JS_EVAL_TYPE_MODULE : JS_EVAL_TYPE_GLOBAL);
    int status = JS_IsException(result) ? 1 : 0;
    if (status) { JS_FreeValue(context, result); result = JS_GetException(context); }
    size_t bytes = 0;
    const char *text = JS_ToCStringLen(context, &bytes, result);
    if (!text) status = 1;
    else if (bytes >= capacity) status = 2;
    else { memcpy(output, text, bytes); output[bytes] = 0; }
    if (status == 2 || !text) output[0] = 0;
    if (text) JS_FreeCString(context, text);
    JS_FreeValue(context, result);
    return status;
}

// The same interrupt budget spans the complete microtask drain. A job that throws
// (including the uncatchable interrupt error) returns < 0; its exception is
// discarded and draining continues unless the budget has expired (-2).
EXPORT int susu_qjs_drain(susu_engine *engine) {
    for (;;) {
        JSContext *context = NULL;
        int result = JS_ExecutePendingJob(engine->runtime, &context);
        if (result == 0) return 0;
        if (result < 0) JS_FreeValue(context ? context : engine->context, JS_GetException(context ? context : engine->context));
        if (interrupt(engine->runtime, engine)) return -2;
    }
}

/* ---- Plugin host extension (F00 prototype) ---- */

static JSValue js_host(JSContext *ctx, JSValueConst this_val, int argc, JSValueConst *argv) {
    (void)this_val;
    susu_engine *engine = JS_GetContextOpaque(ctx);
    int kind = 0;
    if (argc < 2 || JS_ToInt32(ctx, &kind, argv[0])) return JS_ThrowTypeError(ctx, "host: bad arguments");
    size_t length = 0;
    const char *text = JS_ToCStringLen(ctx, &length, argv[1]);
    if (!text) return JS_EXCEPTION;
    if (length > 1024 * 1024) { JS_FreeCString(ctx, text); return JS_ThrowRangeError(ctx, "host message exceeds 1 MiB"); }
    if (kind != 1) {
        if (kind == 2 || kind == 3) engine->host(engine->opaque, kind, 0, text, length); // 4 is host-only
        JS_FreeCString(ctx, text);
        return JS_UNDEFINED;
    }
    if (engine->pending_count == engine->pending_capacity) {
        int capacity = engine->pending_capacity ? engine->pending_capacity * 2 : 8;
        pending_call *grown = realloc(engine->pending, sizeof(*grown) * (size_t)capacity);
        if (!grown) { JS_FreeCString(ctx, text); return JS_ThrowOutOfMemory(ctx); }
        engine->pending = grown;
        engine->pending_capacity = capacity;
    }
    JSValue funcs[2];
    JSValue promise = JS_NewPromiseCapability(ctx, funcs);
    if (JS_IsException(promise)) { JS_FreeCString(ctx, text); return promise; }
    int id = ++engine->next_id;
    engine->pending[engine->pending_count++] = (pending_call){ id, funcs[0], funcs[1] };
    if (engine->host(engine->opaque, 1, id, text, length) != 0) {
        engine->pending_count--;
        JS_FreeValue(ctx, funcs[0]);
        JSValue reject = funcs[1];
        JSValue error = JS_NewError(ctx);
        JS_SetPropertyStr(ctx, error, "kind", JS_NewString(ctx, "bad_response"));
        JS_FreeValue(ctx, JS_Call(ctx, reject, JS_UNDEFINED, 1, (JSValueConst *)&error));
        JS_FreeValue(ctx, error);
        JS_FreeValue(ctx, reject);
    }
    JS_FreeCString(ctx, text);
    return promise;
}

// Only "./" or "../" specifiers inside the plugin root; no URLs, absolute
// paths, drive letters, backslashes or escapes above the root.
static char *normalize(JSContext *ctx, const char *base, const char *name, void *opaque) {
    (void)opaque;
    if (strncmp(name, "./", 2) != 0 && strncmp(name, "../", 3) != 0) { JS_ThrowReferenceError(ctx, "module specifier not allowed"); return NULL; }
    if (strchr(name, '\\') || strchr(name, ':') || strchr(name, '?') || strchr(name, '#')) { JS_ThrowReferenceError(ctx, "module specifier not allowed"); return NULL; }
    char path[512];
    size_t used = 0;
    const char *slash = strrchr(base, '/');
    if (slash) {
        used = (size_t)(slash - base);
        if (used >= sizeof(path)) { JS_ThrowReferenceError(ctx, "module path too long"); return NULL; }
        memcpy(path, base, used);
    }
    path[used] = 0;
    const char *p = name;
    while (*p) {
        const char *end = strchr(p, '/');
        size_t segment = end ? (size_t)(end - p) : strlen(p);
        if (segment == 0 || (segment == 1 && p[0] == '.')) {
        } else if (segment == 2 && p[0] == '.' && p[1] == '.') {
            if (used == 0) { JS_ThrowReferenceError(ctx, "module escapes plugin root"); return NULL; }
            char *last = strrchr(path, '/');
            used = last ? (size_t)(last - path) : 0;
            path[used] = 0;
        } else {
            if (used + segment + 2 >= sizeof(path)) { JS_ThrowReferenceError(ctx, "module path too long"); return NULL; }
            if (used) path[used++] = '/';
            memcpy(path + used, p, segment);
            used += segment;
            path[used] = 0;
        }
        p = end ? end + 1 : p + segment;
    }
    if (used == 0) { JS_ThrowReferenceError(ctx, "empty module path"); return NULL; }
    return js_strdup(ctx, path);
}

static JSModuleDef *load(JSContext *ctx, const char *name, void *opaque) {
    (void)opaque;
    susu_engine *engine = JS_GetContextOpaque(ctx);
    const char *data = NULL;
    size_t length = 0;
    if (!engine->read_module || engine->read_module(engine->opaque, name, &data, &length) != 0 || !data || length > 4 * 1024 * 1024) {
        JS_ThrowReferenceError(ctx, "module not found in plugin package");
        return NULL;
    }
    JSValue compiled = JS_Eval(ctx, data, length, name, JS_EVAL_TYPE_MODULE | JS_EVAL_FLAG_COMPILE_ONLY);
    if (JS_IsException(compiled)) return NULL;
    JSModuleDef *module = JS_VALUE_GET_PTR(compiled);
    JS_FreeValue(ctx, compiled);
    return module;
}

static const char *bootstrap =
"(function(host){'use strict';"
"const KINDS=new Set(['auth','quota','rate_limited','network','timeout','unsupported_language','bad_response']);"
"class PluginError extends Error{constructor(kind,detail){super(String(kind));this.kind=kind;this.detail=detail;}}"
"Object.defineProperty(globalThis,'PluginError',{value:PluginError,writable:false,configurable:false});"
"const calls=new Map();"
"function makeSignal(){const listeners=[];const s={aborted:false,reason:undefined,onabort:null,"
"addEventListener(t,f){if(t==='abort'&&typeof f==='function')listeners.push(f);},"
"removeEventListener(t,f){const i=listeners.indexOf(f);if(i>=0)listeners.splice(i,1);},"
"throwIfAborted(){if(s.aborted)throw s.reason;}};"
"return {signal:s,abort(reason){if(s.aborted)return;s.aborted=true;s.reason=reason;for(const f of listeners.slice()){try{f({type:'abort'});}catch(_){}}if(typeof s.onabort==='function'){try{s.onabort({type:'abort'});}catch(_){}}}};}"
"function toError(e){if(e&&typeof e==='object'&&typeof e.kind==='string'&&KINDS.has(e.kind))return {kind:e.kind,detail:e.detail===undefined?undefined:String(e.detail).slice(0,1024)};"
"if(e&&e.kind==='cancelled')return {kind:'cancelled'};return {kind:'bad_response',detail:String(e&&e.message||e).slice(0,1024)};}"
"function finish(id,message){calls.delete(id);let text;try{text=JSON.stringify(message);}catch(e){text=JSON.stringify({callId:id,ok:false,error:{kind:'bad_response',detail:'result is not JSON'}});}host(2,text);}"
"function invoke(plugin,id,cap,req,config){const c=makeSignal();calls.set(id,c);"
"const api=(op,args)=>c.signal.aborted?Promise.reject(c.signal.reason):host(1,JSON.stringify({callId:id,op,args}));"
"const ctx=Object.freeze({config:Object.freeze(config||{}),signal:c.signal,lang:Object.freeze({from:'en',to:'zh-Hans'}),"
"$http:(r)=>api('http',r),$store:Object.freeze({get:(k)=>api('store.get',{key:k}),set:(k,v)=>api('store.set',{key:k,value:v})}),"
"$log:(level,msg)=>{host(3,JSON.stringify({callId:id,level:String(level),msg:String(msg).slice(0,4096)}));}});"
"Promise.resolve().then(()=>{const f=plugin&&plugin[cap];if(typeof f!=='function')throw new PluginError('bad_response','capability not exported');return f.call(plugin,req,ctx);})"
".then(r=>finish(id,{callId:id,ok:true,result:r}),e=>finish(id,{callId:id,ok:false,error:toError(e)}));}"
"function abort(id){const c=calls.get(id);if(c)c.abort({name:'AbortError',kind:'cancelled'});}"
"return [invoke,abort];})";

// Creates a runtime for one plugin package: host bridge, restricted loader, lockdown.
EXPORT susu_engine *susu_qjs_new_plugin(size_t memory_limit, interrupt_fn interrupted, host_fn host, read_module_fn read_module, void *opaque) {
    if (!host || !read_module) return NULL;
    susu_engine *engine = create(memory_limit, interrupted, opaque);
    if (!engine) return NULL;
    engine->host = host;
    engine->read_module = read_module;
    JS_SetModuleLoaderFunc(engine->runtime, normalize, load, NULL);
    JSContext *ctx = engine->context;
    JSValue factory = JS_Eval(ctx, bootstrap, strlen(bootstrap), "<host-bootstrap>", JS_EVAL_TYPE_GLOBAL);
    JSValue host_function = JS_NewCFunction(ctx, js_host, "host", 2);
    JSValue pair = JS_IsException(factory) ? JS_EXCEPTION : JS_Call(ctx, factory, JS_UNDEFINED, 1, (JSValueConst *)&host_function);
    JS_FreeValue(ctx, host_function);
    JS_FreeValue(ctx, factory);
    if (JS_IsException(pair)) { susu_qjs_free(engine); return NULL; }
    engine->invoke = JS_GetPropertyUint32(ctx, pair, 0);
    engine->abort = JS_GetPropertyUint32(ctx, pair, 1);
    JS_FreeValue(ctx, pair);
    if (apply_lockdown(engine)) { susu_qjs_free(engine); return NULL; }
    return engine;
}

static void describe_exception(JSContext *ctx, char *error, size_t capacity) {
    JSValue exception = JS_GetException(ctx);
    size_t length = 0;
    const char *text = JS_ToCStringLen(ctx, &length, exception);
    if (error && capacity) {
        size_t n = text ? (length < capacity - 1 ? length : capacity - 1) : 0;
        if (n) memcpy(error, text, n);
        error[n] = 0;
    }
    if (text) JS_FreeCString(ctx, text);
    JS_FreeValue(ctx, exception);
}

// Loads entry (normally "main.js") through the restricted loader, evaluates it
// and keeps its default export. Returns 0 ok, 1 exception, 2 interrupted, 3 no default export.
EXPORT int susu_qjs_load(susu_engine *engine, const char *entry, char *error, size_t capacity) {
    if (!engine || !entry) return 3;
    if (error && capacity) error[0] = 0;
    JSContext *ctx = engine->context;
    const char *data = NULL;
    size_t length = 0;
    if (engine->read_module(engine->opaque, entry, &data, &length) != 0 || !data || length > 4 * 1024 * 1024) {
        JS_ThrowReferenceError(ctx, "entry module not found in plugin package");
        describe_exception(ctx, error, capacity);
        return 1;
    }
    JSValue compiled = JS_Eval(ctx, data, length, entry, JS_EVAL_TYPE_MODULE | JS_EVAL_FLAG_COMPILE_ONLY);
    if (JS_IsException(compiled)) { describe_exception(ctx, error, capacity); return 1; }
    JSModuleDef *module = JS_VALUE_GET_PTR(compiled);
    JSValue evaluated = JS_EvalFunction(ctx, compiled);
    if (JS_IsException(evaluated)) { describe_exception(ctx, error, capacity); return interrupt(engine->runtime, engine) ? 2 : 1; }
    if (susu_qjs_drain(engine) == -2) { JS_FreeValue(ctx, evaluated); return 2; }
    int state = JS_IsPromise(evaluated) ? JS_PromiseState(ctx, evaluated) : JS_PROMISE_FULFILLED;
    if (state != JS_PROMISE_FULFILLED) {
        if (state == JS_PROMISE_REJECTED) {
            JSValue reason = JS_PromiseResult(ctx, evaluated);
            JS_Throw(ctx, reason);
            describe_exception(ctx, error, capacity);
        }
        JS_FreeValue(ctx, evaluated);
        return 1;
    }
    JS_FreeValue(ctx, evaluated);
    JSValue ns = JS_GetModuleNamespace(ctx, module);
    JSValue plugin = JS_GetPropertyStr(ctx, ns, "default");
    JS_FreeValue(ctx, ns);
    if (!JS_IsObject(plugin)) { JS_FreeValue(ctx, plugin); return 3; }
    JS_FreeValue(ctx, engine->plugin);
    engine->plugin = plugin;
    return 0;
}

// Starts one capability call. Request/config are parsed as JSON data, never as code.
// Returns 0 ok, 1 exception (bad JSON), 2 interrupted during the synchronous slice.
EXPORT int susu_qjs_invoke(susu_engine *engine, int call_id, const char *capability, const char *request, size_t request_length, const char *config, size_t config_length) {
    if (!engine || !capability || !request || !config || request_length > 4 * 1024 * 1024 || config_length > 1024 * 1024) return 1;
    JSContext *ctx = engine->context;
    JSValue args[5];
    args[0] = JS_DupValue(ctx, engine->plugin);
    args[1] = JS_NewInt32(ctx, call_id);
    args[2] = JS_NewString(ctx, capability);
    args[3] = JS_ParseJSON(ctx, request, request_length, "<request>");
    args[4] = JS_ParseJSON(ctx, config, config_length, "<config>");
    int status = 0;
    if (JS_IsException(args[3]) || JS_IsException(args[4])) status = 1;
    else {
        engine->host(engine->opaque, 4, 0, NULL, 0);
        JSValue result = JS_Call(ctx, engine->invoke, JS_UNDEFINED, 5, (JSValueConst *)args);
        if (JS_IsException(result)) status = 1;
        JS_FreeValue(ctx, result);
    }
    for (int i = 0; i < 5; i++) JS_FreeValue(ctx, args[i]);
    if (status) { JS_FreeValue(ctx, JS_GetException(ctx)); return interrupt(engine->runtime, engine) ? 2 : 1; }
    return susu_qjs_drain(engine) == -2 ? 2 : 0;
}

// Resolves or rejects a pending host API promise with JSON data.
// Returns 0 ok, 1 unknown id / bad JSON, 2 interrupted.
EXPORT int susu_qjs_settle(susu_engine *engine, int id, int ok, const char *json, size_t length) {
    if (!engine || !json || length > 4 * 1024 * 1024) return 1;
    JSContext *ctx = engine->context;
    int index = -1;
    for (int i = 0; i < engine->pending_count; i++) if (engine->pending[i].id == id) { index = i; break; }
    if (index < 0) return 1;
    pending_call call = engine->pending[index];
    engine->pending[index] = engine->pending[--engine->pending_count];
    JSValue value = JS_ParseJSON(ctx, json, length, "<api-result>");
    int status = 0;
    if (JS_IsException(value)) { JS_FreeValue(ctx, JS_GetException(ctx)); value = JS_NewError(ctx); JS_SetPropertyStr(ctx, value, "kind", JS_NewString(ctx, "bad_response")); ok = 0; }
    engine->host(engine->opaque, 4, 0, NULL, 0);
    JSValue result = JS_Call(ctx, ok ? call.resolve : call.reject, JS_UNDEFINED, 1, (JSValueConst *)&value);
    if (JS_IsException(result)) { JS_FreeValue(ctx, JS_GetException(ctx)); status = interrupt(engine->runtime, engine) ? 2 : 1; }
    JS_FreeValue(ctx, result);
    JS_FreeValue(ctx, value);
    JS_FreeValue(ctx, call.resolve);
    JS_FreeValue(ctx, call.reject);
    if (status) return status;
    return susu_qjs_drain(engine) == -2 ? 2 : 0;
}

// Aborts the call's signal (plugin-visible) and drains. 0 ok, 2 interrupted.
EXPORT int susu_qjs_abort(susu_engine *engine, int call_id) {
    if (!engine) return 1;
    JSContext *ctx = engine->context;
    JSValue id = JS_NewInt32(ctx, call_id);
    JSValue result = JS_Call(ctx, engine->abort, JS_UNDEFINED, 1, (JSValueConst *)&id);
    if (JS_IsException(result)) JS_FreeValue(ctx, JS_GetException(ctx));
    JS_FreeValue(ctx, result);
    return susu_qjs_drain(engine) == -2 ? 2 : 0;
}

EXPORT int64_t susu_qjs_memory(susu_engine *engine) {
    if (!engine) return -1;
    JSMemoryUsage usage;
    JS_ComputeMemoryUsage(engine->runtime, &usage);
    return usage.malloc_size;
}
