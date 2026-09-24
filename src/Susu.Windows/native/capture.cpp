// F00 screen-capture backend probe (ARCHITECTURE 9): GDI BitBlt vs DXGI Desktop Duplication,
// per-monitor DPI inventory and a physical-coordinate check with a marker window. Probe only.
#include <windows.h>
#include <shellscalingapi.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <wrl.h>
#include <vector>
#include <string>
#include <cstdio>
#include <algorithm>

using Microsoft::WRL::ComPtr;

namespace {
double Now() { LARGE_INTEGER c, f; QueryPerformanceCounter(&c); QueryPerformanceFrequency(&f); return c.QuadPart * 1000.0 / f.QuadPart; }

struct Monitor { RECT rect; UINT dpiX; UINT dpiY; bool primary; };
BOOL CALLBACK Collect(HMONITOR monitor, HDC, LPRECT, LPARAM data) {
    MONITORINFO info{sizeof(info)};
    GetMonitorInfoW(monitor, &info);
    UINT x = 96, y = 96;
    GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, &x, &y);
    reinterpret_cast<std::vector<Monitor>*>(data)->push_back({info.rcMonitor, x, y, (info.dwFlags & MONITORINFOF_PRIMARY) != 0});
    return TRUE;
}

// Returns the fraction of pixels that are not pure black and, when found, the top-left of a
// magenta (255,0,255) marker block in the 32-bit top-down BGRA buffer.
double Inspect(const unsigned char* pixels, int width, int height, int stride, POINT* marker) {
    long long nonBlack = 0;
    marker->x = marker->y = -1;
    for (int y = 0; y < height; ++y) {
        const unsigned char* row = pixels + static_cast<size_t>(y) * stride;
        for (int x = 0; x < width; ++x) {
            const unsigned char b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
            if (r | g | b) ++nonBlack;
            if (marker->x < 0 && r == 255 && g == 0 && b == 255) { marker->x = x; marker->y = y; }
        }
    }
    return static_cast<double>(nonBlack) / (static_cast<double>(width) * height);
}

LRESULT CALLBACK MarkerProc(HWND hwnd, UINT message, WPARAM w, LPARAM l) {
    if (message == WM_PAINT) {
        PAINTSTRUCT ps; HDC dc = BeginPaint(hwnd, &ps);
        RECT r; GetClientRect(hwnd, &r);
        HBRUSH brush = CreateSolidBrush(RGB(255, 0, 255)); FillRect(dc, &r, brush); DeleteObject(brush);
        EndPaint(hwnd, &ps);
        return 0;
    }
    return DefWindowProcW(hwnd, message, w, l);
}
}

// Writes one JSON document describing monitors, per-backend timings and marker positions.
extern "C" __declspec(dllexport) HRESULT susu_capture_probe(int samples, const wchar_t* outputPath) {
    if (!outputPath || samples < 1 || samples > 200) return E_INVALIDARG;
    DPI_AWARENESS_CONTEXT previous = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    std::vector<Monitor> monitors;
    EnumDisplayMonitors(nullptr, nullptr, Collect, reinterpret_cast<LPARAM>(&monitors));
    const int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
    const int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN), vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);

    // Marker window at a known physical position on the primary monitor (topmost, no activation).
    WNDCLASSW cls{}; cls.lpfnWndProc = MarkerProc; cls.hInstance = GetModuleHandleW(nullptr); cls.lpszClassName = L"SusuF00CaptureMarker";
    RegisterClassW(&cls);
    const POINT expected{vx + 237, vy + 173};
    HWND marker = CreateWindowExW(WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, cls.lpszClassName, L"", WS_POPUP, expected.x, expected.y, 40, 40, nullptr, nullptr, cls.hInstance, nullptr);
    ShowWindow(marker, SW_SHOWNOACTIVATE);
    UpdateWindow(marker);
    for (int i = 0; i < 20; ++i) { MSG msg; while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) DispatchMessageW(&msg); Sleep(10); }

    // GDI: BitBlt(CAPTUREBLT) of the full virtual screen into a top-down 32-bit DIB section.
    std::vector<double> gdi;
    double gdiValid = 0; POINT gdiMarker{-1, -1};
    {
        HDC screen = GetDC(nullptr);
        HDC memory = CreateCompatibleDC(screen);
        BITMAPINFO info{}; info.bmiHeader.biSize = sizeof(info.bmiHeader); info.bmiHeader.biWidth = vw; info.bmiHeader.biHeight = -vh;
        info.bmiHeader.biPlanes = 1; info.bmiHeader.biBitCount = 32; info.bmiHeader.biCompression = BI_RGB;
        void* bits = nullptr;
        HBITMAP bitmap = CreateDIBSection(screen, &info, DIB_RGB_COLORS, &bits, nullptr, 0);
        HGDIOBJ old = SelectObject(memory, bitmap);
        for (int i = 0; i < samples; ++i) {
            const double t = Now();
            BOOL ok = BitBlt(memory, 0, 0, vw, vh, screen, vx, vy, SRCCOPY | CAPTUREBLT);
            GdiFlush();
            gdi.push_back(ok ? Now() - t : -1);
        }
        gdiValid = Inspect(static_cast<unsigned char*>(bits), vw, vh, vw * 4, &gdiMarker);
        SelectObject(memory, old); DeleteObject(bitmap); DeleteDC(memory); ReleaseDC(nullptr, screen);
    }

    // DXGI Desktop Duplication on the primary output (first adapter/output).
    std::vector<double> dxgi;
    double dxgiValid = 0; POINT dxgiMarker{-1, -1};
    HRESULT dxgiStatus = S_OK;
    {
        ComPtr<ID3D11Device> device; ComPtr<ID3D11DeviceContext> context;
        D3D_FEATURE_LEVEL level;
        dxgiStatus = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &device, &level, &context);
        if (FAILED(dxgiStatus)) dxgiStatus = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &device, &level, &context);
        ComPtr<IDXGIDevice> dxgiDevice; ComPtr<IDXGIAdapter> adapter; ComPtr<IDXGIOutput> output; ComPtr<IDXGIOutput1> output1; ComPtr<IDXGIOutputDuplication> duplication;
        if (SUCCEEDED(dxgiStatus)) dxgiStatus = device.As(&dxgiDevice);
        if (SUCCEEDED(dxgiStatus)) dxgiStatus = dxgiDevice->GetAdapter(&adapter);
        if (SUCCEEDED(dxgiStatus)) dxgiStatus = adapter->EnumOutputs(0, &output);
        if (SUCCEEDED(dxgiStatus)) dxgiStatus = output.As(&output1);
        if (SUCCEEDED(dxgiStatus)) dxgiStatus = output1->DuplicateOutput(device.Get(), &duplication);
        DXGI_OUTDUPL_DESC desc{};
        if (duplication) duplication->GetDesc(&desc);
        ComPtr<ID3D11Texture2D> staging;
        if (SUCCEEDED(dxgiStatus)) {
            D3D11_TEXTURE2D_DESC td{}; td.Width = desc.ModeDesc.Width; td.Height = desc.ModeDesc.Height; td.MipLevels = 1; td.ArraySize = 1;
            td.Format = DXGI_FORMAT_B8G8R8A8_UNORM; td.SampleDesc.Count = 1; td.Usage = D3D11_USAGE_STAGING; td.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            dxgiStatus = device->CreateTexture2D(&td, nullptr, &staging);
        }
        for (int i = 0; SUCCEEDED(dxgiStatus) && i < samples; ++i) {
            const double t = Now();
            DXGI_OUTDUPL_FRAME_INFO frame{}; ComPtr<IDXGIResource> resource;
            HRESULT hr = duplication->AcquireNextFrame(i == 0 ? 1000 : 100, &frame, &resource);
            if (hr == DXGI_ERROR_WAIT_TIMEOUT) { dxgi.push_back(-2); continue; } // no new frame: desktop unchanged
            if (FAILED(hr)) { dxgiStatus = hr; break; }
            ComPtr<ID3D11Texture2D> texture; resource.As(&texture);
            context->CopyResource(staging.Get(), texture.Get());
            D3D11_MAPPED_SUBRESOURCE mapped{};
            if (SUCCEEDED(context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped))) {
                dxgi.push_back(Now() - t);
                if (dxgiValid == 0) dxgiValid = Inspect(static_cast<unsigned char*>(mapped.pData), desc.ModeDesc.Width, desc.ModeDesc.Height, mapped.RowPitch, &dxgiMarker);
                context->Unmap(staging.Get(), 0);
            }
            duplication->ReleaseFrame();
            InvalidateRect(marker, nullptr, TRUE); UpdateWindow(marker); // produce a new frame for the next sample
        }
    }
    DestroyWindow(marker);
    SetThreadDpiAwarenessContext(previous);

    FILE* out = nullptr;
    if (_wfopen_s(&out, outputPath, L"w") || !out) return E_ACCESSDENIED;
    fprintf(out, "{\"virtualScreen\":[%d,%d,%d,%d],\"monitors\":[", vx, vy, vw, vh);
    for (size_t i = 0; i < monitors.size(); ++i)
        fprintf(out, "%s{\"rect\":[%ld,%ld,%ld,%ld],\"dpi\":%u,\"primary\":%s}", i ? "," : "", monitors[i].rect.left, monitors[i].rect.top, monitors[i].rect.right, monitors[i].rect.bottom, monitors[i].dpiX, monitors[i].primary ? "true" : "false");
    fprintf(out, "],\"markerExpected\":[%ld,%ld],\"gdi\":{\"validFraction\":%.4f,\"marker\":[%ld,%ld],\"ms\":[", expected.x - vx, expected.y - vy, gdiValid, gdiMarker.x, gdiMarker.y);
    for (size_t i = 0; i < gdi.size(); ++i) fprintf(out, "%s%.3f", i ? "," : "", gdi[i]);
    fprintf(out, "]},\"dxgi\":{\"status\":\"0x%08lx\",\"validFraction\":%.4f,\"marker\":[%ld,%ld],\"ms\":[", static_cast<unsigned long>(dxgiStatus), dxgiValid, dxgiMarker.x, dxgiMarker.y);
    for (size_t i = 0; i < dxgi.size(); ++i) fprintf(out, "%s%.3f", i ? "," : "", dxgi[i]);
    fprintf(out, "]}}\n");
    fclose(out);
    return S_OK;
}
