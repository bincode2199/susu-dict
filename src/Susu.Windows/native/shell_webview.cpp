// Production WebView2 host for Su-Su windows (ARCHITECTURE 6, 9; PLAN 4.5.5).
// All functions run on the host's STA message thread. One environment, one controller per window.
// Each view is locked to one trusted virtual-host origin: other navigations, frames, new windows,
// downloads, permissions and sub-resource requests outside the origin are refused; host objects,
// devtools (unless requested), context menus, autofill, zoom and browser accelerator keys are off.
// Web messages are forwarded only when their source is the trusted origin.
#include <windows.h>
#include <wrl.h>
#include <WebView2.h>
#include <WebView2EnvironmentOptions.h>
#include <string>

using Microsoft::WRL::Callback;
using Microsoft::WRL::ComPtr;
using Microsoft::WRL::Make;

extern "C" {
typedef void(__stdcall* susu_event_fn)(void* context, int event, int hr, const wchar_t* text);
}

namespace {
enum Event : int {
    ViewCreated = 1, NavigationCompleted = 2, WebMessage = 3, ProcessFailed = 4, NavigationBlocked = 5, Suspended = 6,
    EnvironmentCreated = 101, BrowserExited = 102,
};

struct Environment {
    ComPtr<ICoreWebView2Environment> env;
    EventRegistrationToken exited{};
    bool exitedRegistered = false;
    susu_event_fn callback = nullptr;
    void* context = nullptr;
    bool released = false;
};

struct View {
    Environment* environment = nullptr;
    ComPtr<ICoreWebView2Controller> controller;
    ComPtr<ICoreWebView2> view;
    std::wstring origin; // https://host/
    susu_event_fn callback = nullptr;
    void* context = nullptr;
    bool closed = false;
    EventRegistrationToken tokens[9]{};
};

bool Trusted(const View* v, const wchar_t* uri) {
    return uri && _wcsnicmp(uri, v->origin.c_str(), v->origin.size()) == 0;
}

void Emit(View* v, int event, HRESULT hr, const wchar_t* text = nullptr) {
    if (!v->closed && v->callback) v->callback(v->context, event, hr, text);
}

HRESULT Configure(View* v, BOOL devTools) {
    ComPtr<ICoreWebView2Settings> s;
    HRESULT hr = v->view->get_Settings(&s);
    if (FAILED(hr)) return hr;
    s->put_IsScriptEnabled(TRUE);
    s->put_IsWebMessageEnabled(TRUE);
    s->put_AreHostObjectsAllowed(FALSE);
    s->put_AreDevToolsEnabled(devTools);
    s->put_AreDefaultContextMenusEnabled(FALSE);
    s->put_AreDefaultScriptDialogsEnabled(FALSE);
    s->put_IsStatusBarEnabled(FALSE);
    s->put_IsZoomControlEnabled(FALSE);
    s->put_IsBuiltInErrorPageEnabled(FALSE);
    ComPtr<ICoreWebView2Settings3> s3;
    if (SUCCEEDED(s.As(&s3))) s3->put_AreBrowserAcceleratorKeysEnabled(FALSE);
    ComPtr<ICoreWebView2Settings4> s4;
    if (SUCCEEDED(s.As(&s4))) { s4->put_IsPasswordAutosaveEnabled(FALSE); s4->put_IsGeneralAutofillEnabled(FALSE); }
    ComPtr<ICoreWebView2Settings5> s5;
    if (SUCCEEDED(s.As(&s5))) s5->put_IsPinchZoomEnabled(FALSE);
    ComPtr<ICoreWebView2Settings6> s6;
    if (SUCCEEDED(s.As(&s6))) s6->put_IsSwipeNavigationEnabled(FALSE);
    ComPtr<ICoreWebView2Settings9> s9;
    if (SUCCEEDED(s.As(&s9))) s9->put_IsNonClientRegionSupportEnabled(TRUE); // CSS app-region: drag title bars
    return S_OK;
}

HRESULT Guard(View* v) {
    auto* view = v->view.Get();
    HRESULT hr = view->add_NavigationStarting(Callback<ICoreWebView2NavigationStartingEventHandler>(
        [v](ICoreWebView2*, ICoreWebView2NavigationStartingEventArgs* args) -> HRESULT {
            LPWSTR uri = nullptr;
            args->get_Uri(&uri);
            if (!Trusted(v, uri)) { args->put_Cancel(TRUE); Emit(v, NavigationBlocked, S_OK, uri); }
            CoTaskMemFree(uri);
            return S_OK;
        }).Get(), &v->tokens[0]);
    if (FAILED(hr)) return hr;
    hr = view->add_FrameNavigationStarting(Callback<ICoreWebView2NavigationStartingEventHandler>(
        [v](ICoreWebView2*, ICoreWebView2NavigationStartingEventArgs* args) -> HRESULT {
            LPWSTR uri = nullptr;
            args->get_Uri(&uri);
            args->put_Cancel(TRUE); // no frames at all
            Emit(v, NavigationBlocked, S_OK, uri);
            CoTaskMemFree(uri);
            return S_OK;
        }).Get(), &v->tokens[1]);
    if (FAILED(hr)) return hr;
    hr = view->add_NewWindowRequested(Callback<ICoreWebView2NewWindowRequestedEventHandler>(
        [v](ICoreWebView2*, ICoreWebView2NewWindowRequestedEventArgs* args) -> HRESULT {
            LPWSTR uri = nullptr;
            args->get_Uri(&uri);
            args->put_Handled(TRUE); // never open a window; external links go through a host command
            Emit(v, NavigationBlocked, S_OK, uri);
            CoTaskMemFree(uri);
            return S_OK;
        }).Get(), &v->tokens[2]);
    if (FAILED(hr)) return hr;
    hr = view->add_PermissionRequested(Callback<ICoreWebView2PermissionRequestedEventHandler>(
        [](ICoreWebView2*, ICoreWebView2PermissionRequestedEventArgs* args) -> HRESULT {
            args->put_State(COREWEBVIEW2_PERMISSION_STATE_DENY);
            return S_OK;
        }).Get(), &v->tokens[3]);
    if (FAILED(hr)) return hr;
    hr = view->add_WebMessageReceived(Callback<ICoreWebView2WebMessageReceivedEventHandler>(
        [v](ICoreWebView2*, ICoreWebView2WebMessageReceivedEventArgs* args) -> HRESULT {
            LPWSTR source = nullptr, message = nullptr;
            args->get_Source(&source);
            if (Trusted(v, source) && SUCCEEDED(args->TryGetWebMessageAsString(&message)) && message) Emit(v, WebMessage, S_OK, message);
            CoTaskMemFree(source);
            CoTaskMemFree(message);
            return S_OK;
        }).Get(), &v->tokens[4]);
    if (FAILED(hr)) return hr;
    hr = view->add_ProcessFailed(Callback<ICoreWebView2ProcessFailedEventHandler>(
        [v](ICoreWebView2*, ICoreWebView2ProcessFailedEventArgs* args) -> HRESULT {
            COREWEBVIEW2_PROCESS_FAILED_KIND kind{};
            args->get_ProcessFailedKind(&kind);
            Emit(v, ProcessFailed, static_cast<HRESULT>(kind));
            return S_OK;
        }).Get(), &v->tokens[5]);
    if (FAILED(hr)) return hr;
    hr = view->add_NavigationCompleted(Callback<ICoreWebView2NavigationCompletedEventHandler>(
        [v](ICoreWebView2*, ICoreWebView2NavigationCompletedEventArgs* args) -> HRESULT {
            BOOL ok = FALSE;
            args->get_IsSuccess(&ok);
            Emit(v, NavigationCompleted, ok ? S_OK : E_FAIL);
            return S_OK;
        }).Get(), &v->tokens[6]);
    if (FAILED(hr)) return hr;
    ComPtr<ICoreWebView2_4> view4;
    if (SUCCEEDED(v->view.As(&view4))) {
        view4->add_DownloadStarting(Callback<ICoreWebView2DownloadStartingEventHandler>(
            [](ICoreWebView2*, ICoreWebView2DownloadStartingEventArgs* args) -> HRESULT { args->put_Cancel(TRUE); args->put_Handled(TRUE); return S_OK; }).Get(), &v->tokens[7]);
    }
    // Defense in depth: sub-resources may only come from the trusted origin (CSP also forbids them).
    hr = view->AddWebResourceRequestedFilter(L"*", COREWEBVIEW2_WEB_RESOURCE_CONTEXT_ALL);
    if (FAILED(hr)) return hr;
    return view->add_WebResourceRequested(Callback<ICoreWebView2WebResourceRequestedEventHandler>(
        [v](ICoreWebView2*, ICoreWebView2WebResourceRequestedEventArgs* args) -> HRESULT {
            ComPtr<ICoreWebView2WebResourceRequest> request;
            LPWSTR uri = nullptr;
            if (SUCCEEDED(args->get_Request(&request))) request->get_Uri(&uri);
            if (!Trusted(v, uri) && v->environment && v->environment->env) {
                ComPtr<ICoreWebView2WebResourceResponse> response;
                if (SUCCEEDED(v->environment->env->CreateWebResourceResponse(nullptr, 403, L"Forbidden", L"", &response))) args->put_Response(response.Get());
                Emit(v, NavigationBlocked, S_OK, uri);
            }
            CoTaskMemFree(uri);
            return S_OK;
        }).Get(), &v->tokens[8]);
}
}  // namespace

extern "C" {

// Returns S_OK and the installed runtime version, or an error when the WebView2 Runtime is missing.
__declspec(dllexport) HRESULT __stdcall susu_wv_runtime_version(wchar_t* buffer, int length) {
    LPWSTR version = nullptr;
    HRESULT hr = GetAvailableCoreWebView2BrowserVersionString(nullptr, &version);
    if (SUCCEEDED(hr) && !version) hr = HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
    if (SUCCEEDED(hr) && buffer && length > 0) wcsncpy_s(buffer, length, version, _TRUNCATE);
    CoTaskMemFree(version);
    return hr;
}

__declspec(dllexport) HRESULT __stdcall susu_wv_env_create(const wchar_t* userDataFolder, const wchar_t* language, susu_event_fn callback, void* context, void** handle) {
    if (!userDataFolder || !callback || !handle) return E_INVALIDARG;
    auto* e = new Environment();
    e->callback = callback;
    e->context = context;
    auto options = Make<CoreWebView2EnvironmentOptions>();
    if (language && *language) options->put_Language(language);
    ComPtr<ICoreWebView2EnvironmentOptions2> options2;
    if (SUCCEEDED(options.As(&options2))) options2->put_ExclusiveUserDataFolderAccess(TRUE);
    HRESULT hr = CreateCoreWebView2EnvironmentWithOptions(nullptr, userDataFolder, options.Get(),
        Callback<ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler>([e](HRESULT result, ICoreWebView2Environment* env) -> HRESULT {
            if (e->released) { delete e; return S_OK; }
            if (SUCCEEDED(result) && env) {
                e->env = env;
                ComPtr<ICoreWebView2Environment5> env5;
                if (SUCCEEDED(e->env.As(&env5))) {
                    e->exitedRegistered = SUCCEEDED(env5->add_BrowserProcessExited(Callback<ICoreWebView2BrowserProcessExitedEventHandler>(
                        [e](ICoreWebView2Environment* sender, ICoreWebView2BrowserProcessExitedEventArgs*) -> HRESULT {
                            e->callback(e->context, BrowserExited, S_OK, nullptr);
                            if (e->released) {
                                // The host already let go: drop the subscription and the last reference now.
                                ComPtr<ICoreWebView2Environment5> env5;
                                if (sender && SUCCEEDED(sender->QueryInterface(IID_PPV_ARGS(&env5)))) env5->remove_BrowserProcessExited(e->exited);
                                e->env.Reset();
                                delete e;
                            }
                            return S_OK;
                        }).Get(), &e->exited));
                }
            }
            e->callback(e->context, EnvironmentCreated, FAILED(result) ? result : (env ? S_OK : E_POINTER), nullptr);
            return S_OK;
        }).Get());
    if (FAILED(hr)) { delete e; return hr; }
    *handle = e;
    return S_OK;
}

// Marks the environment released once every controller is closed. The reference is kept until
// BrowserProcessExited (WebView2 only raises it while the environment is referenced); the exit handler
// then reports the exit, unsubscribes and frees everything. Without an exit subscription it is freed now.
__declspec(dllexport) void __stdcall susu_wv_env_release(void* handle) {
    auto* e = static_cast<Environment*>(handle);
    if (!e) return;
    e->released = true;
    if (!e->env) return; // creation still pending: its callback deletes it
    if (!e->exitedRegistered) { e->env.Reset(); delete e; }
}

__declspec(dllexport) HRESULT __stdcall susu_wv_create(void* environment, HWND parent, const wchar_t* host, const wchar_t* folder, const wchar_t* path,
                                                       BOOL devTools, susu_event_fn callback, void* context, void** handle) {
    auto* e = static_cast<Environment*>(environment);
    if (!e || !e->env || !parent || !host || !folder || !path || !callback || !handle) return E_INVALIDARG;
    auto* v = new View();
    v->environment = e;
    v->callback = callback;
    v->context = context;
    v->origin = std::wstring(L"https://") + host + L"/";
    std::wstring hostName = host, folderName = folder, url = v->origin + (path[0] == L'/' ? path + 1 : path);
    HRESULT hr = e->env->CreateCoreWebView2Controller(parent, Callback<ICoreWebView2CreateCoreWebView2ControllerCompletedHandler>(
        [v, hostName, folderName, url, devTools](HRESULT result, ICoreWebView2Controller* controller) -> HRESULT {
            if (v->closed) { if (controller) controller->Close(); delete v; return S_OK; }
            if (FAILED(result) || !controller) { Emit(v, ViewCreated, FAILED(result) ? result : E_POINTER); return S_OK; }
            v->controller = controller;
            HRESULT hr = controller->get_CoreWebView2(&v->view);
            ComPtr<ICoreWebView2Controller2> controller2;
            if (SUCCEEDED(hr) && SUCCEEDED(v->controller.As(&controller2))) controller2->put_DefaultBackgroundColor(COREWEBVIEW2_COLOR{255, 255, 255, 255});
            if (SUCCEEDED(hr)) hr = Configure(v, devTools);
            if (SUCCEEDED(hr)) hr = Guard(v);
            ComPtr<ICoreWebView2_3> view3;
            if (SUCCEEDED(hr)) hr = v->view.As(&view3);
            if (SUCCEEDED(hr)) hr = view3->SetVirtualHostNameToFolderMapping(hostName.c_str(), folderName.c_str(), COREWEBVIEW2_HOST_RESOURCE_ACCESS_KIND_DENY);
            Emit(v, ViewCreated, hr);
            if (SUCCEEDED(hr)) hr = v->view->Navigate(url.c_str());
            if (FAILED(hr)) Emit(v, NavigationCompleted, hr);
            return S_OK;
        }).Get());
    if (FAILED(hr)) { delete v; return hr; }
    *handle = v;
    return S_OK;
}

__declspec(dllexport) HRESULT __stdcall susu_wv_post(void* handle, const wchar_t* json) {
    auto* v = static_cast<View*>(handle);
    if (!v || !v->view || !json) return E_INVALIDARG;
    return v->view->PostWebMessageAsJson(json);
}

__declspec(dllexport) HRESULT __stdcall susu_wv_bounds(void* handle, int x, int y, int width, int height) {
    auto* v = static_cast<View*>(handle);
    if (!v || !v->controller) return E_INVALIDARG;
    RECT r{x, y, x + width, y + height};
    return v->controller->put_Bounds(r);
}

__declspec(dllexport) HRESULT __stdcall susu_wv_visible(void* handle, BOOL visible) {
    auto* v = static_cast<View*>(handle);
    if (!v || !v->controller) return E_INVALIDARG;
    if (visible) {
        ComPtr<ICoreWebView2_3> view3;
        if (v->view && SUCCEEDED(v->view.As(&view3))) view3->Resume();
    }
    return v->controller->put_IsVisible(visible);
}

// Keep-warm: after hiding, suspend script timers and release memory (F00: TrySuspend passes PER02).
__declspec(dllexport) HRESULT __stdcall susu_wv_suspend(void* handle) {
    auto* v = static_cast<View*>(handle);
    if (!v || !v->view) return E_INVALIDARG;
    ComPtr<ICoreWebView2_3> view3;
    HRESULT hr = v->view.As(&view3);
    if (FAILED(hr)) return hr;
    return view3->TrySuspend(Callback<ICoreWebView2TrySuspendCompletedHandler>([v](HRESULT result, BOOL suspended) -> HRESULT {
        Emit(v, Suspended, FAILED(result) ? result : (suspended ? S_OK : S_FALSE));
        return S_OK;
    }).Get());
}

__declspec(dllexport) HRESULT __stdcall susu_wv_focus(void* handle) {
    auto* v = static_cast<View*>(handle);
    if (!v || !v->controller) return E_INVALIDARG;
    return v->controller->MoveFocus(COREWEBVIEW2_MOVE_FOCUS_REASON_PROGRAMMATIC);
}

__declspec(dllexport) HRESULT __stdcall susu_wv_parent_moved(void* handle) {
    auto* v = static_cast<View*>(handle);
    if (!v || !v->controller) return E_INVALIDARG;
    return v->controller->NotifyParentWindowPositionChanged();
}

__declspec(dllexport) void __stdcall susu_wv_close(void* handle) {
    auto* v = static_cast<View*>(handle);
    if (!v) return;
    if (!v->controller) { v->closed = true; return; } // creation pending: its callback deletes it
    v->closed = true;
    if (v->view) {
        v->view->remove_NavigationStarting(v->tokens[0]);
        v->view->remove_FrameNavigationStarting(v->tokens[1]);
        v->view->remove_NewWindowRequested(v->tokens[2]);
        v->view->remove_PermissionRequested(v->tokens[3]);
        v->view->remove_WebMessageReceived(v->tokens[4]);
        v->view->remove_ProcessFailed(v->tokens[5]);
        v->view->remove_NavigationCompleted(v->tokens[6]);
        ComPtr<ICoreWebView2_4> view4;
        if (SUCCEEDED(v->view.As(&view4))) view4->remove_DownloadStarting(v->tokens[7]);
        v->view->remove_WebResourceRequested(v->tokens[8]);
    }
    v->controller->Close();
    v->view.Reset();
    v->controller.Reset();
    delete v;
}

}  // extern "C"
