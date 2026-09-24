// F00 PER03/PER04 driver: native shell paint, cold WebView first frame, hot resume frame, and
// keep-warm/release cycles, measured with QPC on one STA thread. Writes JSON lines. Probe only.
#include <windows.h>
#include <wrl.h>
#include <WebView2.h>
#include <dwmapi.h>
#include <tlhelp32.h>
#include <string>
#include <vector>
#include <functional>
#include <thread>
#include <cstdio>

using Microsoft::WRL::ComPtr;
using Microsoft::WRL::Callback;

extern "C" HRESULT susu_process_memory(HANDLE process, unsigned long long* privateWorkingSet, unsigned long long* privateBytes, unsigned long long* peakPrivateBytes);

namespace {
double Now() { LARGE_INTEGER c, f; QueryPerformanceCounter(&c); QueryPerformanceFrequency(&f); return c.QuadPart * 1000.0 / f.QuadPart; }

bool Pump(const std::function<bool()>& done, int timeoutMs) {
    const double deadline = Now() + timeoutMs;
    MSG msg{};
    while (!done()) {
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&msg); DispatchMessageW(&msg); }
        if (done()) break;
        double left = deadline - Now();
        if (left <= 0) return false;
        MsgWaitForMultipleObjectsEx(0, nullptr, static_cast<DWORD>(left < 1 ? 1 : left), QS_ALLINPUT, MWMO_INPUTAVAILABLE);
    }
    return true;
}

LRESULT CALLBACK ShellProc(HWND hwnd, UINT message, WPARAM w, LPARAM l) {
    if (message == WM_PAINT) {
        PAINTSTRUCT ps; HDC dc = BeginPaint(hwnd, &ps);
        RECT r; GetClientRect(hwnd, &r);
        HBRUSH brush = CreateSolidBrush(RGB(250, 250, 249)); FillRect(dc, &r, brush); DeleteObject(brush);
        SetBkMode(dc, TRANSPARENT); TextOutW(dc, 16, 16, L"Su-Su", 5);
        EndPaint(hwnd, &ps);
        return 0;
    }
    return DefWindowProcW(hwnd, message, w, l);
}

struct View {
    HWND hwnd = nullptr;
    ComPtr<ICoreWebView2Environment> env;
    ComPtr<ICoreWebView2Controller> controller;
    ComPtr<ICoreWebView2> view;
    DWORD browserPid = 0;
    bool firstFrame = false;
    int lastShownSeq = -1;
    int shownFrames = 0;
    int dom = 0;
    HRESULT status = S_OK;
};

HWND CreateShell() {
    WNDCLASSW cls{};
    cls.lpfnWndProc = ShellProc; cls.hInstance = GetModuleHandleW(nullptr); cls.lpszClassName = L"SusuF00LatencyShell";
    cls.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    RegisterClassW(&cls);
    return CreateWindowExW(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, cls.lpszClassName, L"Su-Su F00 latency shell", WS_POPUP | WS_BORDER, 120, 120, 520, 700, nullptr, nullptr, cls.hInstance, nullptr);
}

// Creates environment + controller in view.hwnd and navigates; returns after the first-frame message.
bool Open(View& v, const std::wstring& folder, const std::wstring& userData, double* envMs, double* controllerMs, double t0) {
    bool envDone = false, controllerDone = false;
    HRESULT hr = CreateCoreWebView2EnvironmentWithOptions(nullptr, userData.c_str(), nullptr,
        Callback<ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler>([&](HRESULT r, ICoreWebView2Environment* e) -> HRESULT {
            envDone = true; v.status = r; v.env = e; if (envMs) *envMs = Now() - t0; return S_OK; }).Get());
    if (FAILED(hr) || !Pump([&] { return envDone; }, 10000) || FAILED(v.status) || !v.env) return false;
    hr = v.env->CreateCoreWebView2Controller(v.hwnd, Callback<ICoreWebView2CreateCoreWebView2ControllerCompletedHandler>([&](HRESULT r, ICoreWebView2Controller* c) -> HRESULT {
        controllerDone = true; v.status = r; v.controller = c; if (controllerMs) *controllerMs = Now() - t0; return S_OK; }).Get());
    if (FAILED(hr) || !Pump([&] { return controllerDone; }, 10000) || FAILED(v.status) || !v.controller) return false;
    v.controller->get_CoreWebView2(&v.view);
    ComPtr<ICoreWebView2_3> view3; v.view.As(&view3);
    view3->SetVirtualHostNameToFolderMapping(L"susu-probe.example", folder.c_str(), COREWEBVIEW2_HOST_RESOURCE_ACCESS_KIND_DENY_CORS);
    UINT32 pid = 0; v.view->get_BrowserProcessId(&pid); v.browserPid = pid;
    RECT bounds; GetClientRect(v.hwnd, &bounds); v.controller->put_Bounds(bounds);
    EventRegistrationToken token{};
    v.view->add_WebMessageReceived(Callback<ICoreWebView2WebMessageReceivedEventHandler>([&v](ICoreWebView2*, ICoreWebView2WebMessageReceivedEventArgs* args) -> HRESULT {
        LPWSTR json = nullptr;
        if (SUCCEEDED(args->get_WebMessageAsJson(&json)) && json) {
            if (wcsstr(json, L"\"first-frame\"")) v.firstFrame = true;
            else if (const wchar_t* p = wcsstr(json, L"\"seq\":")) {
                v.lastShownSeq = _wtoi(p + 6); v.shownFrames++;
                if (const wchar_t* d = wcsstr(json, L"\"dom\":")) v.dom = _wtoi(d + 6);
            }
            CoTaskMemFree(json);
        }
        return S_OK; }).Get(), &token);
    v.firstFrame = false;
    if (FAILED(v.view->Navigate(L"https://susu-probe.example/index.html?kind=main"))) return false;
    return Pump([&] { return v.firstFrame; }, 15000);
}

// Closes the controller, releases the environment and waits for the browser process itself.
double Release(View& v) {
    HANDLE browser = v.browserPid ? OpenProcess(SYNCHRONIZE, FALSE, v.browserPid) : nullptr;
    const double start = Now();
    if (v.controller) v.controller->Close();
    v.view.Reset(); v.controller.Reset(); v.env.Reset();
    bool gone = !browser;
    const double deadline = Now() + 20000;
    MSG msg{};
    while (!gone && Now() < deadline) {
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&msg); DispatchMessageW(&msg); }
        gone = MsgWaitForMultipleObjectsEx(1, &browser, 20, QS_ALLINPUT, MWMO_INPUTAVAILABLE) == WAIT_OBJECT_0;
    }
    if (browser) CloseHandle(browser);
    return gone ? Now() - start : -1;
}

bool Suspend(View& v) {
    ComPtr<ICoreWebView2_3> view3; if (FAILED(v.view.As(&view3))) return false;
    bool done = false; BOOL ok = FALSE;
    view3->TrySuspend(Callback<ICoreWebView2TrySuspendCompletedHandler>([&](HRESULT, BOOL s) -> HRESULT { done = true; ok = s; return S_OK; }).Get());
    return Pump([&] { return done; }, 5000) && ok;
}

unsigned long long TreePws(DWORD root) {
    std::vector<DWORD> pids{GetCurrentProcessId()};
    if (root) {
        HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        std::vector<PROCESSENTRY32W> all; PROCESSENTRY32W e{sizeof(e)};
        if (Process32FirstW(snapshot, &e)) do all.push_back(e); while (Process32NextW(snapshot, &e));
        CloseHandle(snapshot);
        pids.push_back(root);
        for (size_t i = 1; i < pids.size(); ++i) for (auto& x : all) if (x.th32ParentProcessID == pids[i] && _wcsicmp(x.szExeFile, L"msedgewebview2.exe") == 0) pids.push_back(x.th32ProcessID);
    }
    unsigned long long total = 0;
    for (DWORD pid : pids) {
        HANDLE p = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, FALSE, pid);
        unsigned long long a = 0, b = 0, c = 0;
        if (p && SUCCEEDED(susu_process_memory(p, &a, &b, &c))) total += a;
        if (p) CloseHandle(p);
    }
    return total;
}

void Run(const std::wstring& folder, const std::wstring& userData, int coldSamples, int hotSamples, int cycles, FILE* out, HRESULT* result) {
    *result = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(*result)) return;
    // Cold: hotkey (t0) -> native shell painted and presented -> WebView first painted frame.
    for (int i = 0; i < coldSamples; ++i) {
        View v;
        const double t0 = Now();
        v.hwnd = CreateShell();
        ShowWindow(v.hwnd, SW_SHOWNOACTIVATE);
        UpdateWindow(v.hwnd);
        DwmFlush();
        const double shell = Now() - t0;
        double envMs = 0, controllerMs = 0;
        bool ok = Open(v, folder, userData, &envMs, &controllerMs, t0);
        const double ui = Now() - t0;
        const double exit = Release(v);
        DestroyWindow(v.hwnd);
        fprintf(out, "{\"kind\":\"cold\",\"i\":%d,\"ok\":%s,\"nativeShellMs\":%.3f,\"environmentMs\":%.3f,\"controllerMs\":%.3f,\"firstFrameMs\":%.3f,\"browserExitMs\":%.3f}\n", i, ok ? "true" : "false", shell, envMs, controllerMs, ui, exit);
        fflush(out);
    }
    // Hot: window hidden + WebView suspended (keep-warm) -> resume + show -> painted frame.
    View v;
    v.hwnd = CreateShell();
    ShowWindow(v.hwnd, SW_SHOWNOACTIVATE);
    if (!Open(v, folder, userData, nullptr, nullptr, Now())) { fprintf(out, "{\"kind\":\"hot-setup-failed\"}\n"); *result = E_FAIL; CoUninitialize(); return; }
    auto cycle = [&](int seq, bool suspend, double* shownMs, bool* suspended) {
        v.controller->put_IsVisible(FALSE);
        ShowWindow(v.hwnd, SW_HIDE);
        *suspended = suspend ? Suspend(v) : false;
        Pump([] { return false; }, 150);
        const double t0 = Now();
        if (*suspended) { ComPtr<ICoreWebView2_3> view3; v.view.As(&view3); view3->Resume(); }
        v.controller->put_IsVisible(TRUE);
        ShowWindow(v.hwnd, SW_SHOWNOACTIVATE);
        const int before = v.shownFrames;
        std::wstring message = L"{\"type\":\"shown\",\"seq\":" + std::to_wstring(seq) + L"}";
        v.view->PostWebMessageAsJson(message.c_str());
        bool ok = Pump([&] { return v.lastShownSeq == seq && v.shownFrames > before; }, 5000);
        *shownMs = ok ? Now() - t0 : -1;
        Pump([] { return false; }, 30); // collect any duplicate frame message
        return v.shownFrames - before;
    };
    for (int i = 0; i < hotSamples; ++i) {
        double ms; bool suspended;
        int frames = cycle(i, true, &ms, &suspended);
        fprintf(out, "{\"kind\":\"hot\",\"i\":%d,\"suspended\":%s,\"contentVisibleMs\":%.3f,\"frames\":%d,\"dom\":%d}\n", i, suspended ? "true" : "false", ms, frames, v.dom);
        fflush(out);
    }
    // PER04: repeated hide/show (keep-warm) cycles, duplicate-event and memory check.
    const unsigned long long before = TreePws(v.browserPid);
    int duplicates = 0, failures = 0, emptyDom = 0;
    for (int i = 0; i < cycles; ++i) {
        double ms; bool suspended;
        int frames = cycle(10000 + i, true, &ms, &suspended);
        if (frames != 1) duplicates += frames > 1 ? 1 : 0;
        if (ms < 0) failures++;
        if (v.dom <= 0) emptyDom++;
    }
    Pump([] { return false; }, 2000);
    const unsigned long long after = TreePws(v.browserPid);
    fprintf(out, "{\"kind\":\"cycles\",\"count\":%d,\"duplicates\":%d,\"failures\":%d,\"emptyDom\":%d,\"treePwsBeforeMiB\":%.3f,\"treePwsAfterMiB\":%.3f}\n",
        cycles, duplicates, failures, emptyDom, before / 1048576.0, after / 1048576.0);
    // Release (end of keep-warm) and reopen cold: state and page must come back intact.
    const double exit = Release(v);
    DestroyWindow(v.hwnd);
    View again;
    again.hwnd = CreateShell();
    ShowWindow(again.hwnd, SW_SHOWNOACTIVATE);
    const double t0 = Now();
    bool reopened = Open(again, folder, userData, nullptr, nullptr, t0);
    double reopenMs = Now() - t0;
    int frames = 0;
    if (reopened) { double ms; bool s; frames = cycle(99999, false, &ms, &s); }
    const double exit2 = Release(again);
    DestroyWindow(again.hwnd);
    fprintf(out, "{\"kind\":\"release-reopen\",\"releaseBrowserExitMs\":%.3f,\"reopened\":%s,\"reopenFirstFrameMs\":%.3f,\"reopenShownFrames\":%d,\"dom\":%d,\"finalBrowserExitMs\":%.3f}\n",
        exit, reopened ? "true" : "false", reopenMs, frames, again.dom, exit2);
    fflush(out);
    CoUninitialize();
}
}

extern "C" __declspec(dllexport) HRESULT susu_latency_run(const wchar_t* folder, const wchar_t* userData, int coldSamples, int hotSamples, int cycles, const wchar_t* outputPath) {
    if (!folder || !userData || !outputPath) return E_INVALIDARG;
    FILE* out = nullptr;
    if (_wfopen_s(&out, outputPath, L"w") || !out) return E_ACCESSDENIED;
    HRESULT result = S_OK;
    std::thread sta([&] { Run(folder, userData, coldSamples, hotSamples, cycles, out, &result); });
    sta.join();
    fclose(out);
    return result;
}
