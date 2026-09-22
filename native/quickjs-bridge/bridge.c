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
typedef struct {
    JSRuntime *runtime;
    JSContext *context;
    interrupt_fn interrupted;
    void *opaque;
} susu_engine;

static int interrupt(JSRuntime *runtime, void *opaque) {
    (void)runtime;
    susu_engine *engine = opaque;
    return engine->interrupted && engine->interrupted(engine->opaque);
}

EXPORT void susu_qjs_free(susu_engine *engine) {
    if (!engine) return;
    if (engine->context) JS_FreeContext(engine->context);
    if (engine->runtime) JS_FreeRuntime(engine->runtime);
    free(engine);
}

EXPORT susu_engine *susu_qjs_new(size_t memory_limit, interrupt_fn interrupted, void *opaque) {
    susu_engine *engine = calloc(1, sizeof(*engine));
    if (!engine) return NULL;
    engine->runtime = JS_NewRuntime();
    if (!engine->runtime) { free(engine); return NULL; }
    JS_SetMemoryLimit(engine->runtime, memory_limit);
    JS_SetMaxStackSize(engine->runtime, 512 * 1024);
    engine->interrupted = interrupted;
    engine->opaque = opaque;
    JS_SetInterruptHandler(engine->runtime, interrupt, engine);
    engine->context = JS_NewContext(engine->runtime);
    if (!engine->context) { susu_qjs_free(engine); return NULL; }
    // Keep the host source compiler but remove script-visible dynamic compilers,
    // including constructor chains from ordinary, async and generator functions.
    const char *bootstrap = "for(const f of [function(){},async function(){},function*(){},async function*(){}])Object.defineProperty(Object.getPrototypeOf(f),'constructor',{value:undefined,writable:false,configurable:false});Object.defineProperty(globalThis,'eval',{value:undefined,writable:false,configurable:false});Object.defineProperty(globalThis,'Function',{value:undefined,writable:false,configurable:false});";
    JSValue result = JS_Eval(engine->context, bootstrap, strlen(bootstrap), "<bootstrap>", JS_EVAL_TYPE_GLOBAL);
    int failed = JS_IsException(result);
    JS_FreeValue(engine->context, result);
    if (failed) { susu_qjs_free(engine); return NULL; }
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

// The same interrupt budget spans the complete microtask drain.
EXPORT int susu_qjs_drain(susu_engine *engine) {
    JSContext *context = NULL;
    int result;
    while ((result = JS_ExecutePendingJob(engine->runtime, &context)) > 0) {
        if (interrupt(engine->runtime, engine)) return -2;
    }
    return result;
}
