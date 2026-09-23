#include <windows.h>
#include <wrl.h>
#include <WebView2.h>
#include <memory>
#include <string>
#include <vector>
#include <chrono>
#include <functional>
#include <cstdio>
#include <thread>
#include <algorithm>

using Microsoft::WRL::ComPtr;
using Microsoft::WRL::Callback;
using Clock = std::chrono::steady_clock;
struct WindowState {
    HWND hwnd = nullptr;
    ComPtr<ICoreWebView2Controller> controller;
    ComPtr<ICoreWebView2> view;
    EventRegistrationToken navigation{};
    bool registered = false;
    bool ready = false;
    bool stopped = false;
    HRESULT status = E_PENDING;
};
struct EnvironmentState {
    ComPtr<ICoreWebView2Environment> environment;
    bool ready = false;
    bool stopped = false;
    bool exited = false;
    HRESULT status = E_PENDING;
};
static void Pump(const std::function<bool()>& stop, int milliseconds) {
    const auto deadline = Clock::now() + std::chrono::milliseconds(milliseconds);
    while (!stop() && Clock::now() < deadline) {
        MSG message{};
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&message); DispatchMessageW(&message); }
        if(stop())break;
        const auto remaining=std::chrono::duration_cast<std::chrono::milliseconds>(deadline-Clock::now()).count();
        if(remaining>0)MsgWaitForMultipleObjectsEx(0,nullptr,static_cast<DWORD>(remaining),QS_ALLINPUT,MWMO_INPUTAVAILABLE);
    }
}
static void Event(const char* name) { printf("{\"event\":\"%s\",\"tick\":%llu}\n", name, GetTickCount64()); fflush(stdout); }

static HRESULT MeasureWindows(const wchar_t* folder, const wchar_t* userData, int warmMilliseconds, int idleMilliseconds,int memoryMode) {
    if (!folder || !userData || warmMilliseconds < 0 || idleMilliseconds < 0 || warmMilliseconds > 600000 || idleMilliseconds > 300000) return E_INVALIDARG;
    HRESULT hr = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(hr)) return hr;
    auto environment = std::make_shared<EnvironmentState>();
    Event("start");
    hr = CreateCoreWebView2EnvironmentWithOptions(nullptr, userData, nullptr,
        Callback<ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler>([environment](HRESULT result, ICoreWebView2Environment* env) -> HRESULT {
            if (environment->stopped) return S_OK;
            environment->status = FAILED(result) ? result : (env ? S_OK : E_POINTER);
            environment->environment = env;
            environment->ready = true;
            return S_OK;
        }).Get());
    if (SUCCEEDED(hr)) { Pump([&]{return environment->ready;},15000); hr = environment->ready ? environment->status : HRESULT_FROM_WIN32(WAIT_TIMEOUT); }
    if(SUCCEEDED(hr))Event("environment-ready");
    ComPtr<ICoreWebView2Environment5> env5;
    EventRegistrationToken exitToken{};
    bool exitRegistered = false;
    if (SUCCEEDED(hr)) {
        hr = environment->environment.As(&env5);
        if (SUCCEEDED(hr)) {
            hr = env5->add_BrowserProcessExited(Callback<ICoreWebView2BrowserProcessExitedEventHandler>(
                [environment](ICoreWebView2Environment*, ICoreWebView2BrowserProcessExitedEventArgs*) -> HRESULT {environment->exited=true; Event("browser-exited"); return S_OK;}).Get(), &exitToken);
            exitRegistered = SUCCEEDED(hr);
        }
    }
    const wchar_t* kinds[] = {L"main",L"settings",L"selection",L"ocr",L"voice",L"transcribe",L"tray",L"error"};
    std::vector<std::shared_ptr<WindowState>> windows;
    for (const auto* kind : kinds) {
        if (FAILED(hr)) break;
        auto window = std::make_shared<WindowState>();
        windows.push_back(window);
        int width = wcscmp(kind,L"settings")==0 || wcscmp(kind,L"transcribe")==0 ? 900 : 520;
        window->hwnd = CreateWindowExW(WS_EX_NOACTIVATE,L"STATIC",L"Su-Su F00 representative window",WS_OVERLAPPEDWINDOW,
            80,80,width,700,nullptr,nullptr,GetModuleHandleW(nullptr),nullptr);
        if (!window->hwnd) { hr = HRESULT_FROM_WIN32(GetLastError()); break; }
        const std::wstring url = L"https://susu-probe.example/index.html?kind=" + std::wstring(kind);
        hr = environment->environment->CreateCoreWebView2Controller(window->hwnd,
            Callback<ICoreWebView2CreateCoreWebView2ControllerCompletedHandler>([window,folder,url,width](HRESULT result, ICoreWebView2Controller* controller) -> HRESULT {
                if (window->stopped) {if(controller)controller->Close();return S_OK;}
                if (FAILED(result)||!controller) {window->status=FAILED(result)?result:E_POINTER;window->ready=true;return S_OK;}
                window->controller=controller;
                window->status=controller->get_CoreWebView2(&window->view);
                ComPtr<ICoreWebView2_3> view3;
                if (SUCCEEDED(window->status)) window->status=window->view.As(&view3);
                if (SUCCEEDED(window->status)) window->status=view3->SetVirtualHostNameToFolderMapping(L"susu-probe.example",folder,COREWEBVIEW2_HOST_RESOURCE_ACCESS_KIND_DENY_CORS);
                if (FAILED(window->status)) {window->ready=true;return S_OK;}
                ComPtr<ICoreWebView2Settings> settings;
                if (SUCCEEDED(window->view->get_Settings(&settings))) {settings->put_AreHostObjectsAllowed(FALSE);settings->put_AreDevToolsEnabled(FALSE);}
                RECT bounds{0,0,width,650};controller->put_Bounds(bounds);
                window->status=window->view->add_NavigationCompleted(Callback<ICoreWebView2NavigationCompletedEventHandler>([window](ICoreWebView2*,ICoreWebView2NavigationCompletedEventArgs* args)->HRESULT{
                    BOOL ok=FALSE;HRESULT result=args->get_IsSuccess(&ok);window->status=FAILED(result)?result:(ok?S_OK:E_FAIL);window->ready=true;return S_OK;
                }).Get(),&window->navigation);
                window->registered=SUCCEEDED(window->status);
                if (SUCCEEDED(window->status)) window->status=window->view->Navigate(url.c_str());
                if (FAILED(window->status)) window->ready=true;
                return S_OK;
            }).Get());
        if (SUCCEEDED(hr)) {Pump([&]{return window->ready;},15000);hr=window->ready?window->status:HRESULT_FROM_WIN32(WAIT_TIMEOUT);}
        if (SUCCEEDED(hr)) {Event("window-ready");ShowWindow(window->hwnd,SW_SHOWNOACTIVATE);}
    }
    if (SUCCEEDED(hr)) {
        Pump([]{return false;},1000);
        Event("all-visible");
        for (auto& window:windows) {window->controller->put_IsVisible(FALSE);ShowWindow(window->hwnd,SW_HIDE);}
        Event("all-hidden");
        const auto hiddenAt=Clock::now();
        for(auto& window:windows){
            if(memoryMode==1){
                ComPtr<ICoreWebView2_3> view3;hr=window->view.As(&view3);if(FAILED(hr))break;
                window->ready=false;
                hr=view3->TrySuspend(Callback<ICoreWebView2TrySuspendCompletedHandler>([window](HRESULT result,BOOL suspended)->HRESULT{
                    window->status=FAILED(result)?result:(suspended?S_OK:E_FAIL);window->ready=true;return S_OK;
                }).Get());
                if(SUCCEEDED(hr)){Pump([&]{return window->ready;},5000);hr=window->ready?window->status:HRESULT_FROM_WIN32(WAIT_TIMEOUT);}
                if(FAILED(hr))break;
            }else if(memoryMode==2){
                ComPtr<ICoreWebView2_19> view19;hr=window->view.As(&view19);if(FAILED(hr))break;
                hr=view19->put_MemoryUsageTargetLevel(COREWEBVIEW2_MEMORY_USAGE_TARGET_LEVEL_LOW);if(FAILED(hr))break;
            }
        }
        if(SUCCEEDED(hr)){
            Event(memoryMode==1?"all-suspended":memoryMode==2?"low-memory-requested":"baseline-hidden");
            const auto elapsed=std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now()-hiddenAt).count();
            Pump([]{return false;},static_cast<int>(std::max<int64_t>(0,warmMilliseconds-elapsed)));
        }
        if(SUCCEEDED(hr)&&warmMilliseconds<10000&&memoryMode!=0){
            for(auto& window:windows){
                if(memoryMode==1){ComPtr<ICoreWebView2_3> view3;hr=window->view.As(&view3);if(SUCCEEDED(hr))hr=view3->Resume();}
                else{ComPtr<ICoreWebView2_19> view19;hr=window->view.As(&view19);if(SUCCEEDED(hr))hr=view19->put_MemoryUsageTargetLevel(COREWEBVIEW2_MEMORY_USAGE_TARGET_LEVEL_NORMAL);}
                if(FAILED(hr))break;
                window->controller->put_IsVisible(TRUE);
                window->ready=false;
                hr=window->view->ExecuteScript(L"document.querySelector('#app').children.length > 0",Callback<ICoreWebView2ExecuteScriptCompletedHandler>([window](HRESULT result,LPCWSTR json)->HRESULT{
                    window->status=FAILED(result)?result:(json&&wcscmp(json,L"true")==0?S_OK:E_FAIL);window->ready=true;return S_OK;
                }).Get());
                if(SUCCEEDED(hr)){Pump([&]{return window->ready;},2000);hr=window->ready?window->status:HRESULT_FROM_WIN32(WAIT_TIMEOUT);}
                if(FAILED(hr))break;
            }
            if(SUCCEEDED(hr))Event("all-resumed-dom-verified");
        }
    }
    for (auto& window:windows) {
        window->stopped=true;
        if(window->view&&window->registered)window->view->remove_NavigationCompleted(window->navigation);
        window->view.Reset();
        if(window->controller)window->controller->Close();
        window->controller.Reset();
        if(window->hwnd)DestroyWindow(window->hwnd);
    }
    Event("controllers-closed");
    Pump([&]{return environment->exited;},5000);
    if(SUCCEEDED(hr)&&!environment->exited)hr=HRESULT_FROM_WIN32(WAIT_TIMEOUT);
    if(exitRegistered)env5->remove_BrowserProcessExited(exitToken);
    env5.Reset();environment->stopped=true;environment->environment.Reset();
    if(SUCCEEDED(hr))Pump([]{return false;},idleMilliseconds);
    CoUninitialize();
    Event("finished");
    return hr;
}

extern "C" __declspec(dllexport) HRESULT susu_measure_windows(const wchar_t* folder, const wchar_t* userData, int warmMilliseconds, int idleMilliseconds,int memoryMode) {
    HRESULT result=E_FAIL;
    std::thread sta([&]{result=MeasureWindows(folder,userData,warmMilliseconds,idleMilliseconds,memoryMode);});
    sta.join();
    return result;
}

