// F00 PER01 fallback route: WebView2-hosted plugins (hidden host page -> sandboxed iframe ->
// one module Worker per plugin). Benchmark harness only; not a product component.
#include <windows.h>
#include <wrl.h>
#include <WebView2.h>
#include <WebView2EnvironmentOptions.h>
#include <tlhelp32.h>
#include <psapi.h>
#include <string>
#include <vector>
#include <thread>
#include <atomic>
#include <functional>
#include <chrono>

using Microsoft::WRL::ComPtr;
using Microsoft::WRL::Callback;
using Microsoft::WRL::Make;

extern "C" HRESULT susu_process_memory(HANDLE process, unsigned long long* privateWorkingSet, unsigned long long* privateBytes, unsigned long long* peakPrivateBytes);

namespace {
typedef void(__cdecl* message_fn)(void* opaque, const wchar_t* json);
constexpr UINT WM_POST_JSON = WM_APP + 1;
constexpr UINT WM_STOP = WM_APP + 2;

struct Session {
    std::wstring userData, hostFolder, pluginFolder;
    message_fn onMessage = nullptr;
    void* opaque = nullptr;
    HWND hwnd = nullptr;
    std::thread thread;
    HANDLE ready = nullptr;       // signalled when sandbox is ready or start failed
    HANDLE finished = nullptr;    // signalled when the STA thread has torn down
    HRESULT startResult = E_PENDING;
    double timings[4]{};          // environment, controller, navigation, sandbox-ready (ms)
    ComPtr<ICoreWebView2Environment> environment;
    ComPtr<ICoreWebView2Controller> controller;
    ComPtr<ICoreWebView2> view;
    std::atomic<DWORD> browserPid{0};
    bool exited = false;
    int exitedResult = 0;
    LARGE_INTEGER started{};
};

double Elapsed(const Session* s) {
    LARGE_INTEGER now, frequency;
    QueryPerformanceCounter(&now);
    QueryPerformanceFrequency(&frequency);
    return (now.QuadPart - s->started.QuadPart) * 1000.0 / frequency.QuadPart;
}

LRESULT CALLBACK WindowProc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) {
    auto s = reinterpret_cast<Session*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
    if (message == WM_POST_JSON) {
        auto text = reinterpret_cast<std::wstring*>(lparam);
        if (s && s->view) s->view->PostWebMessageAsJson(text->c_str());
        delete text;
        return 0;
    }
    if (message == WM_STOP) { PostQuitMessage(0); return 0; }
    return DefWindowProcW(hwnd, message, wparam, lparam);
}

void Fail(Session* s, HRESULT hr) { if (s->startResult == E_PENDING) { s->startResult = hr; SetEvent(s->ready); } }

void Run(Session* s) {
    HRESULT hr = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(hr)) { Fail(s, hr); SetEvent(s->finished); return; }
    WNDCLASSW cls{};
    cls.lpfnWndProc = WindowProc;
    cls.hInstance = GetModuleHandleW(nullptr);
    cls.lpszClassName = L"SusuF00WebViewPluginHost";
    RegisterClassW(&cls);
    s->hwnd = CreateWindowExW(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, cls.lpszClassName, L"Su-Su F00 WebView plugin host", WS_POPUP, 0, 0, 64, 64, nullptr, nullptr, cls.hInstance, nullptr);
    if (!s->hwnd) { Fail(s, HRESULT_FROM_WIN32(GetLastError())); CoUninitialize(); SetEvent(s->finished); return; }
    SetWindowLongPtrW(s->hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(s));
    auto options = Make<CoreWebView2EnvironmentOptions>();
    // Hidden pages throttle timers; the plugin watchdog must not be delayed.
    options->put_AdditionalBrowserArguments(L"--disable-background-timer-throttling --disable-renderer-backgrounding");
    hr = CreateCoreWebView2EnvironmentWithOptions(nullptr, s->userData.c_str(), options.Get(),
        Callback<ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler>([s](HRESULT result, ICoreWebView2Environment* env) -> HRESULT {
            if (FAILED(result) || !env) { Fail(s, FAILED(result) ? result : E_POINTER); return S_OK; }
            s->timings[0] = Elapsed(s);
            s->environment = env;
            ComPtr<ICoreWebView2Environment5> env5;
            if (SUCCEEDED(s->environment.As(&env5))) {
                EventRegistrationToken token{};
                env5->add_BrowserProcessExited(Callback<ICoreWebView2BrowserProcessExitedEventHandler>(
                    [s](ICoreWebView2Environment*, ICoreWebView2BrowserProcessExitedEventArgs*) -> HRESULT { s->exited = true; return S_OK; }).Get(), &token);
            }
            return env->CreateCoreWebView2Controller(s->hwnd, Callback<ICoreWebView2CreateCoreWebView2ControllerCompletedHandler>(
                [s](HRESULT code, ICoreWebView2Controller* controller) -> HRESULT {
                    if (FAILED(code) || !controller) { Fail(s, FAILED(code) ? code : E_POINTER); return S_OK; }
                    s->timings[1] = Elapsed(s);
                    s->controller = controller;
                    HRESULT r = controller->get_CoreWebView2(&s->view);
                    ComPtr<ICoreWebView2_3> view3;
                    if (SUCCEEDED(r)) r = s->view.As(&view3);
                    if (SUCCEEDED(r)) r = view3->SetVirtualHostNameToFolderMapping(L"susu-plugin-host.example", s->hostFolder.c_str(), COREWEBVIEW2_HOST_RESOURCE_ACCESS_KIND_DENY_CORS);
                    // Plugin modules are imported cross-origin (dynamic import) by the blob workers in the opaque-origin iframe.
                    if (SUCCEEDED(r)) r = view3->SetVirtualHostNameToFolderMapping(L"susu-plugin.example", s->pluginFolder.c_str(), COREWEBVIEW2_HOST_RESOURCE_ACCESS_KIND_ALLOW);
                    ComPtr<ICoreWebView2Settings> settings;
                    if (SUCCEEDED(r) && SUCCEEDED(s->view->get_Settings(&settings))) { settings->put_AreDevToolsEnabled(FALSE); settings->put_AreHostObjectsAllowed(FALSE); settings->put_IsStatusBarEnabled(FALSE); }
                    UINT32 pid = 0;
                    if (SUCCEEDED(r) && SUCCEEDED(s->view->get_BrowserProcessId(&pid))) s->browserPid = pid;
                    EventRegistrationToken token{};
                    if (SUCCEEDED(r)) r = s->view->add_NavigationCompleted(Callback<ICoreWebView2NavigationCompletedEventHandler>(
                        [s](ICoreWebView2*, ICoreWebView2NavigationCompletedEventArgs* args) -> HRESULT {
                            BOOL ok = FALSE; args->get_IsSuccess(&ok);
                            if (!ok) Fail(s, E_FAIL); else s->timings[2] = Elapsed(s);
                            return S_OK;
                        }).Get(), &token);
                    if (SUCCEEDED(r)) r = s->view->add_WebMessageReceived(Callback<ICoreWebView2WebMessageReceivedEventHandler>(
                        [s](ICoreWebView2*, ICoreWebView2WebMessageReceivedEventArgs* args) -> HRESULT {
                            LPWSTR json = nullptr;
                            if (FAILED(args->get_WebMessageAsJson(&json)) || !json) return S_OK;
                            if (s->startResult == E_PENDING && wcsstr(json, L"\"SandboxReady\"")) { s->timings[3] = Elapsed(s); s->startResult = S_OK; SetEvent(s->ready); }
                            else if (s->onMessage) s->onMessage(s->opaque, json);
                            CoTaskMemFree(json);
                            return S_OK;
                        }).Get(), &token);
                    RECT bounds{0, 0, 64, 64};
                    if (SUCCEEDED(r)) controller->put_Bounds(bounds);
                    if (SUCCEEDED(r)) r = s->view->Navigate(L"https://susu-plugin-host.example/host.html");
                    if (FAILED(r)) Fail(s, r);
                    return S_OK;
                }).Get());
        }).Get());
    if (FAILED(hr)) Fail(s, hr);
    MSG msg{};
    while (GetMessageW(&msg, nullptr, 0, 0) > 0) { TranslateMessage(&msg); DispatchMessageW(&msg); }
    // Teardown on this STA thread: close controller, release environment, then wait for the
    // browser process itself (the exit event is not reliably delivered once references drop).
    HANDLE browser = s->browserPid ? OpenProcess(SYNCHRONIZE, FALSE, s->browserPid) : nullptr;
    if (s->controller) s->controller->Close();
    s->view.Reset(); s->controller.Reset();
    s->environment.Reset();
    const ULONGLONG deadline = GetTickCount64() + 5000;
    bool gone = browser == nullptr;
    while (!gone && GetTickCount64() < deadline) {
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&msg); DispatchMessageW(&msg); }
        gone = MsgWaitForMultipleObjectsEx(1, &browser, 20, QS_ALLINPUT, MWMO_INPUTAVAILABLE) == WAIT_OBJECT_0;
    }
    if (browser) CloseHandle(browser);
    s->exitedResult = (gone && s->browserPid) || s->exited ? 1 : 0;
    DestroyWindow(s->hwnd);
    CoUninitialize();
    SetEvent(s->finished);
}

void CollectTree(DWORD root, std::vector<DWORD>& pids) {
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE) return;
    std::vector<PROCESSENTRY32W> all;
    PROCESSENTRY32W entry{sizeof(entry)};
    if (Process32FirstW(snapshot, &entry)) do all.push_back(entry); while (Process32NextW(snapshot, &entry));
    CloseHandle(snapshot);
    pids.push_back(root);
    for (size_t i = 0; i < pids.size(); ++i)
        for (const auto& e : all)
            if (e.th32ParentProcessID == pids[i] && e.th32ProcessID != pids[i] && _wcsicmp(e.szExeFile, L"msedgewebview2.exe") == 0) pids.push_back(e.th32ProcessID);
}
}

extern "C" __declspec(dllexport) HRESULT susu_wvp_start(const wchar_t* userData, const wchar_t* hostFolder, const wchar_t* pluginFolder,
    message_fn onMessage, void* opaque, void** handle, double* timings) {
    if (!userData || !hostFolder || !pluginFolder || !onMessage || !handle || !timings) return E_INVALIDARG;
    auto s = new Session{};
    s->userData = userData; s->hostFolder = hostFolder; s->pluginFolder = pluginFolder;
    s->onMessage = onMessage; s->opaque = opaque;
    s->ready = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    s->finished = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    QueryPerformanceCounter(&s->started);
    s->thread = std::thread(Run, s);
    if (WaitForSingleObject(s->ready, 20000) != WAIT_OBJECT_0) s->startResult = HRESULT_FROM_WIN32(WAIT_TIMEOUT);
    for (int i = 0; i < 4; ++i) timings[i] = s->timings[i];
    *handle = s;
    return s->startResult;
}

extern "C" __declspec(dllexport) HRESULT susu_wvp_post(void* handle, const wchar_t* json) {
    auto s = static_cast<Session*>(handle);
    if (!s || !json || !s->hwnd) return E_INVALIDARG;
    auto text = new std::wstring(json);
    if (!PostMessageW(s->hwnd, WM_POST_JSON, 0, reinterpret_cast<LPARAM>(text))) { delete text; return HRESULT_FROM_WIN32(GetLastError()); }
    return S_OK;
}

// Private working set / private bytes over the browser process and all its descendants.
extern "C" __declspec(dllexport) HRESULT susu_wvp_memory(void* handle, unsigned long long* pws, unsigned long long* privateBytes, int* processes, int* unreadable) {
    auto s = static_cast<Session*>(handle);
    if (!s || !pws || !privateBytes || !processes || !unreadable || !s->browserPid) return E_INVALIDARG;
    std::vector<DWORD> pids;
    CollectTree(s->browserPid, pids);
    *pws = 0; *privateBytes = 0; *processes = 0; *unreadable = 0;
    for (DWORD pid : pids) {
        HANDLE process = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, FALSE, pid);
        unsigned long long a = 0, b = 0, c = 0;
        if (process && SUCCEEDED(susu_process_memory(process, &a, &b, &c))) { *pws += a; *privateBytes += b; ++*processes; }
        else ++*unreadable;
        if (process) CloseHandle(process);
    }
    return S_OK;
}

extern "C" __declspec(dllexport) HRESULT susu_wvp_stop(void* handle, int* exited) {
    auto s = static_cast<Session*>(handle);
    if (!s || !exited) return E_INVALIDARG;
    if (s->hwnd) PostMessageW(s->hwnd, WM_STOP, 0, 0);
    WaitForSingleObject(s->finished, 15000);
    if (s->thread.joinable()) s->thread.join();
    *exited = s->exitedResult;
    CloseHandle(s->ready); CloseHandle(s->finished);
    delete s;
    return S_OK;
}

extern "C" __declspec(dllexport) DWORD susu_wvp_browser_pid(void* handle) {
    auto s = static_cast<Session*>(handle);
    return s ? s->browserPid.load() : 0;
}
