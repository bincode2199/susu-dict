#include <windows.h>
#include <elscore.h>
#include <elssrvc.h>
#include <wrl.h>
#include <WebView2.h>
#include <memory>
#include <string>
#include <thread>
#include <vector>
#include <chrono>
#include <algorithm>
#include <functional>

using Microsoft::WRL::ComPtr;
using Microsoft::WRL::Callback;
using Clock = std::chrono::steady_clock;

extern "C" __declspec(dllexport) HRESULT susu_els_probe() {
    GUID language = ELS_GUID_LANGUAGE_DETECTION;
    MAPPING_ENUM_OPTIONS options{};
    options.Size = sizeof(options);
    options.pGuid = &language;
    MAPPING_SERVICE_INFO* services = nullptr;
    DWORD count = 0;
    HRESULT hr = MappingGetServices(&options, &services, &count);
    if (FAILED(hr)) return hr;
    if (count == 0) { MappingFreeServices(services); return HRESULT_FROM_WIN32(ERROR_NOT_FOUND); }
    MAPPING_PROPERTY_BAG bag{};
    bag.Size = sizeof(bag);
    const wchar_t* text = L"This is a complete English sentence for local language detection.";
    hr = MappingRecognizeText(services, text, static_cast<DWORD>(wcslen(text)), 0, nullptr, &bag);
    if (SUCCEEDED(hr)) {
        bool english = false;
        for (DWORD i = 0; i < bag.dwRangesCount; ++i) {
            const auto& range = bag.prgResultRanges[i];
            const auto* data = static_cast<const wchar_t*>(range.pData);
            const size_t chars = range.dwDataSize / sizeof(wchar_t);
            for (size_t j = 0; data && j + 2 < chars; ++j)
                if ((j == 0 || data[j - 1] == 0) && data[j] == L'e' && data[j + 1] == L'n' && (data[j + 2] == 0 || data[j + 2] == L'-')) english = true;
        }
        HRESULT cleanup = MappingFreePropertyBag(&bag);
        if (FAILED(cleanup)) hr = cleanup;
        else if (!english) hr = E_UNEXPECTED;
    }
    MappingFreeServices(services);
    return hr;
}

struct WebState {
    ComPtr<ICoreWebView2Environment> environment;
    ComPtr<ICoreWebView2Controller> controller;
    ComPtr<ICoreWebView2> view;
    EventRegistrationToken navigationToken{};
    EventRegistrationToken exitToken{};
    bool navigationRegistered = false;
    bool exitRegistered = false;
    bool done = false;
    bool exited = false;
    bool stopped = false;
    HRESULT result = E_PENDING;
    std::vector<double> samples;
};

static void PumpUntil(const std::function<bool()>& done, Clock::time_point deadline) {
    while (!done() && Clock::now() < deadline) {
        MSG message{};
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        MsgWaitForMultipleObjectsEx(0, nullptr, 10, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
    }
}

static HRESULT WebProbe(const wchar_t* userData, double* samples, int capacity, int* browserExited) {
    HRESULT hr = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(hr)) return hr;
    auto state = std::make_shared<WebState>();
    HWND window = CreateWindowExW(WS_EX_NOACTIVATE, L"STATIC", L"Su-Su WebView feasibility probe", WS_OVERLAPPEDWINDOW,
        100, 100, 640, 480, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    if (!window) { CoUninitialize(); return HRESULT_FROM_WIN32(GetLastError()); }
    hr = CreateCoreWebView2EnvironmentWithOptions(nullptr, userData, nullptr,
        Callback<ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler>([state, window](HRESULT result, ICoreWebView2Environment* env) -> HRESULT {
            if (state->stopped) return S_OK;
            if (FAILED(result) || !env) { state->result = FAILED(result) ? result : E_POINTER; state->done = true; return S_OK; }
            state->environment = env;
            ComPtr<ICoreWebView2Environment5> env5;
            if (SUCCEEDED(env->QueryInterface(IID_PPV_ARGS(&env5)))) {
                state->exitRegistered = SUCCEEDED(env5->add_BrowserProcessExited(Callback<ICoreWebView2BrowserProcessExitedEventHandler>(
                    [state](ICoreWebView2Environment*, ICoreWebView2BrowserProcessExitedEventArgs*) -> HRESULT { state->exited = true; return S_OK; }).Get(), &state->exitToken));
            }
            HRESULT created = env->CreateCoreWebView2Controller(window,
                Callback<ICoreWebView2CreateCoreWebView2ControllerCompletedHandler>([state](HRESULT code, ICoreWebView2Controller* controller) -> HRESULT {
                    if (state->stopped) { if (controller) controller->Close(); return S_OK; }
                    if (FAILED(code) || !controller) { state->result = FAILED(code) ? code : E_POINTER; state->done = true; return S_OK; }
                    state->controller = controller;
                    state->result = controller->get_CoreWebView2(&state->view);
                    if (FAILED(state->result)) { state->done = true; return S_OK; }
                    RECT bounds{0, 0, 640, 480};
                    controller->put_Bounds(bounds);
                    controller->put_IsVisible(FALSE);
                    state->result = state->view->add_NavigationCompleted(Callback<ICoreWebView2NavigationCompletedEventHandler>(
                        [state](ICoreWebView2*, ICoreWebView2NavigationCompletedEventArgs* args) -> HRESULT {
                            BOOL success = FALSE;
                            HRESULT result = args->get_IsSuccess(&success);
                            state->result = FAILED(result) ? result : (success ? S_OK : E_FAIL);
                            state->done = true;
                            return S_OK;
                        }).Get(), &state->navigationToken);
                    state->navigationRegistered = SUCCEEDED(state->result);
                    if (FAILED(state->result)) { state->done = true; return S_OK; }
                    state->result = state->view->NavigateToString(L"<!doctype html><html><head><meta http-equiv='Content-Security-Policy' content=\"default-src 'none'; script-src 'none'\"></head><body><h1>Su-Su F00</h1><p>Synthetic WebView probe</p></body></html>");
                    if (FAILED(state->result)) state->done = true;
                    return S_OK;
                }).Get());
            if (FAILED(created)) { state->result = created; state->done = true; }
            return S_OK;
        }).Get());
    if (SUCCEEDED(hr)) {
        PumpUntil([&] { return state->done; }, Clock::now() + std::chrono::seconds(15));
        hr = state->done ? state->result : HRESULT_FROM_WIN32(WAIT_TIMEOUT);
    }
    for (int i = 0; SUCCEEDED(hr) && i < capacity; ++i) {
        state->done = false;
        auto start = Clock::now();
        hr = state->view->ExecuteScript(L"({text:'中文',value:21*2})", Callback<ICoreWebView2ExecuteScriptCompletedHandler>(
            [state](HRESULT result, LPCWSTR json) -> HRESULT {
                state->result = FAILED(result) ? result : (json && wcscmp(json, L"{\"text\":\"中文\",\"value\":42}") == 0 ? S_OK : E_UNEXPECTED);
                state->done = true;
                return S_OK;
            }).Get());
        if (FAILED(hr)) break;
        PumpUntil([&] { return state->done; }, Clock::now() + std::chrono::seconds(2));
        hr = state->done ? state->result : HRESULT_FROM_WIN32(WAIT_TIMEOUT);
        samples[i] = std::chrono::duration<double, std::milli>(Clock::now() - start).count();
    }
    state->stopped = true;
    if (state->view && state->navigationRegistered) state->view->remove_NavigationCompleted(state->navigationToken);
    state->view.Reset();
    if (state->controller) state->controller->Close();
    state->controller.Reset();
    PumpUntil([&] { return state->exited; }, Clock::now() + std::chrono::seconds(5));
    *browserExited = state->exited ? 1 : 0;
    if (state->environment && state->exitRegistered) {
        ComPtr<ICoreWebView2Environment5> env5;
        if (SUCCEEDED(state->environment.As(&env5))) env5->remove_BrowserProcessExited(state->exitToken);
    }
    state->environment.Reset();
    DestroyWindow(window);
    CoUninitialize();
    return hr;
}

extern "C" __declspec(dllexport) HRESULT susu_webview_probe(const wchar_t* userData, double* samples, int capacity, int* exited) {
    if (!userData || !samples || !exited || capacity < 1 || capacity > 100) return E_INVALIDARG;
    HRESULT result = E_FAIL;
    std::thread worker([&] { result = WebProbe(userData, samples, capacity, exited); });
    worker.join();
    return result;
}

// Ranked ELS language candidates for text (NUL-separated, double-NUL terminated in `output`).
// The service list is resolved once per process; MappingRecognizeText is the per-call cost.
extern "C" __declspec(dllexport) HRESULT susu_els_detect(const wchar_t* text, wchar_t* output, unsigned int capacity) {
    static MAPPING_SERVICE_INFO* services = nullptr;
    static DWORD count = 0;
    if (!text || !output || capacity < 2) return E_INVALIDARG;
    output[0] = output[1] = 0;
    if (!services) {
        GUID language = ELS_GUID_LANGUAGE_DETECTION;
        MAPPING_ENUM_OPTIONS options{};
        options.Size = sizeof(options);
        options.pGuid = &language;
        HRESULT hr = MappingGetServices(&options, &services, &count);
        if (FAILED(hr)) { services = nullptr; return hr; }
        if (!count) return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
    }
    MAPPING_PROPERTY_BAG bag{};
    bag.Size = sizeof(bag);
    HRESULT hr = MappingRecognizeText(services, text, static_cast<DWORD>(wcslen(text)), 0, nullptr, &bag);
    if (FAILED(hr)) return hr;
    size_t used = 0;
    for (DWORD i = 0; i < bag.dwRangesCount && bag.prgResultRanges; ++i) {
        const auto& range = bag.prgResultRanges[i];
        const size_t chars = range.dwDataSize / sizeof(wchar_t);
        if (used + chars + 1 >= capacity) break;
        memcpy(output + used, range.pData, chars * sizeof(wchar_t));
        used += chars;
    }
    output[used] = 0;
    if (used + 1 < capacity) output[used + 1] = 0;
    return MappingFreePropertyBag(&bag);
}
