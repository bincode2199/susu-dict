// F00 clipboard-borrowing prototype (PLAN 3.1 level 3). Snapshot runs in a killable helper
// process; listening, candidate checks and restore run in the main process. Not production code.
#include <windows.h>
#include <shellapi.h>
#include <string>
#include <vector>
#include <cstdint>
#include <cstdio>

namespace {
constexpr size_t MaxSnapshot = 16u << 20;

// Formats the prototype can restore losslessly. CF_TEXT/CF_OEMTEXT/CF_BITMAP/CF_PALETTE/CF_LOCALE are
// synthesized by Windows from CF_UNICODETEXT/CF_DIB(V5) and are therefore not stored separately.
bool IsSkippedSynthesized(UINT format) { return format == CF_TEXT || format == CF_OEMTEXT || format == CF_BITMAP || format == CF_PALETTE || format == CF_LOCALE; }

bool Allowed(UINT format, const std::wstring& name) {
    if (format == CF_UNICODETEXT || format == CF_DIB || format == CF_DIBV5 || format == CF_HDROP) return true;
    return name == L"HTML Format" || name == L"Rich Text Format" ||
           name == L"ExcludeClipboardContentFromMonitorProcessing" || name == L"CanIncludeInClipboardHistory" || name == L"CanUploadToCloudClipboard";
}

std::wstring FormatName(UINT format) {
    wchar_t buffer[256];
    int n = GetClipboardFormatNameW(format, buffer, 256);
    return n > 0 ? std::wstring(buffer, n) : std::wstring();
}

bool OpenWithRetry(HWND owner, int milliseconds) {
    const ULONGLONG deadline = GetTickCount64() + milliseconds;
    do { if (OpenClipboard(owner)) return true; Sleep(5); } while (GetTickCount64() < deadline);
    return false;
}

bool WriteAll(HANDLE out, const void* data, DWORD size) {
    const auto* p = static_cast<const unsigned char*>(data);
    while (size) { DWORD written = 0; if (!WriteFile(out, p, size, &written, nullptr) || !written) return false; p += written; size -= written; }
    return true;
}
}

// Helper-process side. Writes: u32 status, u32 sequence, then records
// [u32 format][u16 nameChars][name UTF-16][u32 bytes][bytes] and a terminating format 0.
// Status: 0 ok, 10 unsupported (private) format, 11 virtual files, 12 over 16 MiB, 13 cannot open,
// 14 GetClipboardData failed (e.g. delayed rendering refused).
extern "C" __declspec(dllexport) int susu_clip_snapshot(HANDLE out, wchar_t* offending, unsigned int capacity) {
    if (offending && capacity) offending[0] = 0;
    DWORD sequence = GetClipboardSequenceNumber();
    std::vector<unsigned char> body;
    auto put = [&](const void* data, size_t n) { auto p = static_cast<const unsigned char*>(data); body.insert(body.end(), p, p + n); };
    int status = 0;
    if (!OpenWithRetry(nullptr, 50)) status = 13;
    else {
        UINT format = 0;
        size_t total = 0;
        while (status == 0 && (format = EnumClipboardFormats(format)) != 0) {
            if (IsSkippedSynthesized(format)) continue;
            std::wstring name = format >= 0xC000 ? FormatName(format) : std::wstring();
            if (name == L"FileGroupDescriptorW" || name == L"FileGroupDescriptor" || name == L"FileContents") { status = 11; }
            else if (!Allowed(format, name)) { status = 10; }
            if (status) { if (offending && capacity) wcsncpy_s(offending, capacity, name.empty() ? L"(standard format)" : name.c_str(), _TRUNCATE); break; }
            HANDLE data = GetClipboardData(format); // may trigger delayed rendering; the parent enforces the deadline
            if (!data) { status = 14; break; }
            SIZE_T bytes = GlobalSize(data);
            total += bytes;
            if (total > MaxSnapshot) { status = 12; break; }
            void* p = GlobalLock(data);
            if (!p) { status = 14; break; }
            uint32_t f = format; uint16_t nameChars = static_cast<uint16_t>(name.size()); uint32_t size = static_cast<uint32_t>(bytes);
            put(&f, 4); put(&nameChars, 2); put(name.data(), name.size() * 2); put(&size, 4); put(p, bytes);
            GlobalUnlock(data);
        }
        CloseClipboard();
    }
    uint32_t header[2] = {static_cast<uint32_t>(status), sequence};
    uint32_t end = 0;
    if (!WriteAll(out, header, 8)) return 20;
    if (status == 0 && (!WriteAll(out, body.data(), static_cast<DWORD>(body.size())) || !WriteAll(out, &end, 4))) return 20;
    return status;
}

// ---- Main-process side ----

namespace {
struct Listener {
    HWND hwnd = nullptr;
    int updates = 0;
    DWORD lastSequence = 0;
};
LRESULT CALLBACK ListenerProc(HWND hwnd, UINT message, WPARAM w, LPARAM l) {
    if (message == WM_CLIPBOARDUPDATE) {
        auto listener = reinterpret_cast<Listener*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
        if (listener) { listener->updates++; listener->lastSequence = GetClipboardSequenceNumber(); }
        return 0;
    }
    return DefWindowProcW(hwnd, message, w, l);
}
}

// Creates a message-only window with AddClipboardFormatListener (event-driven, no polling).
extern "C" __declspec(dllexport) void* susu_clip_listen() {
    WNDCLASSW cls{};
    cls.lpfnWndProc = ListenerProc;
    cls.hInstance = GetModuleHandleW(nullptr);
    cls.lpszClassName = L"SusuF00ClipboardListener";
    RegisterClassW(&cls);
    auto listener = new Listener{};
    listener->hwnd = CreateWindowExW(0, cls.lpszClassName, L"", 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, cls.hInstance, nullptr);
    if (!listener->hwnd) { delete listener; return nullptr; }
    SetWindowLongPtrW(listener->hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(listener));
    if (!AddClipboardFormatListener(listener->hwnd)) { DestroyWindow(listener->hwnd); delete listener; return nullptr; }
    return listener;
}

// Pumps this thread's messages until at least one clipboard update after `sinceSequence` arrives
// or the deadline passes. Returns the number of updates seen, the latest sequence and owner PID.
extern "C" __declspec(dllexport) int susu_clip_wait(void* handle, DWORD sinceSequence, int timeoutMs, int settleMs, DWORD* sequence, DWORD* ownerPid) {
    auto listener = static_cast<Listener*>(handle);
    if (!listener || !sequence || !ownerPid) return -1;
    listener->updates = 0;
    const ULONGLONG deadline = GetTickCount64() + timeoutMs;
    ULONGLONG settleUntil = 0;
    MSG msg{};
    for (;;) {
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&msg); DispatchMessageW(&msg); }
        ULONGLONG now = GetTickCount64();
        if (listener->updates > 0 && GetClipboardSequenceNumber() != sinceSequence) {
            if (!settleUntil) settleUntil = now + settleMs; // collect racing updates briefly (C04/C05)
            if (now >= settleUntil) break;
        }
        if (now >= deadline) break;
        const ULONGLONG until = settleUntil ? settleUntil : deadline;
        MsgWaitForMultipleObjectsEx(0, nullptr, static_cast<DWORD>(until > now ? until - now : 0), QS_ALLINPUT, MWMO_INPUTAVAILABLE);
    }
    *sequence = GetClipboardSequenceNumber();
    *ownerPid = 0;
    HWND owner = GetClipboardOwner();
    if (owner) GetWindowThreadProcessId(owner, ownerPid);
    return listener->updates;
}

extern "C" __declspec(dllexport) void susu_clip_unlisten(void* handle) {
    auto listener = static_cast<Listener*>(handle);
    if (!listener) return;
    RemoveClipboardFormatListener(listener->hwnd);
    DestroyWindow(listener->hwnd);
    delete listener;
}

// Reads CF_UNICODETEXT (bounded). Returns 0 ok, 1 no text, 2 cannot open, 3 too large.
extern "C" __declspec(dllexport) int susu_clip_read_text(wchar_t* text, unsigned int capacity) {
    if (!text || capacity < 2) return 3;
    text[0] = 0;
    if (!OpenWithRetry(nullptr, 50)) return 2;
    int result = 1;
    HANDLE data = GetClipboardData(CF_UNICODETEXT);
    if (data) {
        auto p = static_cast<const wchar_t*>(GlobalLock(data));
        if (p) {
            size_t n = wcsnlen(p, GlobalSize(data) / sizeof(wchar_t));
            if (n >= capacity) result = 3;
            else { memcpy(text, p, n * sizeof(wchar_t)); text[n] = 0; result = n ? 0 : 1; }
            GlobalUnlock(data);
        }
    }
    CloseClipboard();
    return result;
}

// Restores a snapshot only if the clipboard still holds the accepted candidate (same sequence).
// Returns 0 restored, 1 newer content present (kept), 2 could not open within retryMs, 3 bad snapshot,
// 4 SetClipboardData failed. *after receives the sequence produced by this restore (to ignore our own event).
extern "C" __declspec(dllexport) int susu_clip_restore(const unsigned char* snapshot, unsigned int length, DWORD expectedSequence, int retryMs, DWORD* after) {
    if (!snapshot || !after) return 3;
    *after = 0;
    if (!OpenWithRetry(nullptr, retryMs)) return 2;
    if (GetClipboardSequenceNumber() != expectedSequence) { CloseClipboard(); return 1; }
    EmptyClipboard();
    size_t offset = 0;
    int result = 0;
    while (offset + 4 <= length) {
        uint32_t format; memcpy(&format, snapshot + offset, 4); offset += 4;
        if (!format) break;
        if (offset + 2 > length) { result = 3; break; }
        uint16_t nameChars; memcpy(&nameChars, snapshot + offset, 2); offset += 2;
        std::wstring name(reinterpret_cast<const wchar_t*>(snapshot + offset), nameChars);
        offset += nameChars * 2u;
        if (offset + 4 > length) { result = 3; break; }
        uint32_t size; memcpy(&size, snapshot + offset, 4); offset += 4;
        if (offset + size > length) { result = 3; break; }
        UINT target = name.empty() ? format : RegisterClipboardFormatW(name.c_str());
        HGLOBAL memory = GlobalAlloc(GMEM_MOVEABLE, size ? size : 1);
        if (!memory) { result = 4; break; }
        void* p = GlobalLock(memory);
        memcpy(p, snapshot + offset, size);
        GlobalUnlock(memory);
        if (!SetClipboardData(target, memory)) { GlobalFree(memory); result = 4; break; }
        offset += size;
    }
    CloseClipboard();
    *after = GetClipboardSequenceNumber();
    return result;
}

// ---- Test fixtures (harness only) ----

namespace {
UINT g_delayMs = 0;
LRESULT CALLBACK DelayedOwnerProc(HWND hwnd, UINT message, WPARAM w, LPARAM l) {
    if (message == WM_RENDERFORMAT && w == CF_UNICODETEXT) {
        Sleep(g_delayMs); // slow delayed rendering
        const wchar_t text[] = L"Su-Su delayed-render original";
        HGLOBAL memory = GlobalAlloc(GMEM_MOVEABLE, sizeof(text));
        memcpy(GlobalLock(memory), text, sizeof(text)); GlobalUnlock(memory);
        SetClipboardData(CF_UNICODETEXT, memory);
        return 0;
    }
    if (message == WM_RENDERALLFORMATS) return 0;
    return DefWindowProcW(hwnd, message, w, l);
}
}

// Mode 1: become the owner of a delayed-rendered CF_UNICODETEXT (render takes delayMs), pump for lifetimeMs.
// Mode 2: open the clipboard and hold it for lifetimeMs (blocks restore). Mode 3: put text, then exit.
// Mode 4: wait for the next clipboard change, then put text (racing copy). Mode 5: wait for the next
// change, then hold the clipboard open for delayMs (blocks the conditional restore).
extern "C" __declspec(dllexport) int susu_clip_fixture(int mode, int delayMs, int lifetimeMs, const wchar_t* text) {
    WNDCLASSW cls{};
    cls.lpfnWndProc = DelayedOwnerProc;
    cls.hInstance = GetModuleHandleW(nullptr);
    cls.lpszClassName = L"SusuF00ClipboardFixture";
    RegisterClassW(&cls);
    HWND hwnd = CreateWindowExW(0, cls.lpszClassName, L"", 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, cls.hInstance, nullptr);
    g_delayMs = static_cast<UINT>(delayMs);
    if (mode == 4 || mode == 5) {
        const DWORD start = GetClipboardSequenceNumber();
        puts("armed"); fflush(stdout);
        const ULONGLONG until = GetTickCount64() + lifetimeMs;
        while (GetClipboardSequenceNumber() == start && GetTickCount64() < until) Sleep(1);
        if (GetClipboardSequenceNumber() == start) return 3;
        if (!OpenWithRetry(hwnd, 1000)) return 2;
        if (mode == 5) { Sleep(delayMs); CloseClipboard(); DestroyWindow(hwnd); return 0; }
        EmptyClipboard();
        size_t bytes = (wcslen(text) + 1) * sizeof(wchar_t);
        HGLOBAL memory = GlobalAlloc(GMEM_MOVEABLE, bytes);
        memcpy(GlobalLock(memory), text, bytes); GlobalUnlock(memory);
        SetClipboardData(CF_UNICODETEXT, memory);
        CloseClipboard();
        DestroyWindow(hwnd);
        return 0;
    }
    if (!OpenWithRetry(hwnd, 1000)) return 2;
    if (mode == 2) { puts("armed"); fflush(stdout); }
    if (mode == 1 || mode == 3) {
        EmptyClipboard();
        if (mode == 1) SetClipboardData(CF_UNICODETEXT, nullptr);
        else {
            size_t bytes = (wcslen(text) + 1) * sizeof(wchar_t);
            HGLOBAL memory = GlobalAlloc(GMEM_MOVEABLE, bytes);
            memcpy(GlobalLock(memory), text, bytes); GlobalUnlock(memory);
            SetClipboardData(CF_UNICODETEXT, memory);
        }
        CloseClipboard();
        if (mode == 3) return 0;
    }
    const ULONGLONG deadline = GetTickCount64() + lifetimeMs;
    MSG msg{};
    while (GetTickCount64() < deadline) {
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&msg); DispatchMessageW(&msg); }
        MsgWaitForMultipleObjectsEx(0, nullptr, 20, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
    }
    if (mode == 2) CloseClipboard();
    DestroyWindow(hwnd);
    return 0;
}
