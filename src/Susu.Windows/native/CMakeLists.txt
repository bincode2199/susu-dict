cmake_minimum_required(VERSION 3.20)
project(susu_windows_probe CXX)
# Static CRT: no VC++ redistributable is required on a clean Windows 11 system.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
if(NOT DEFINED WEBVIEW_SDK)
  message(FATAL_ERROR "WEBVIEW_SDK must point to the pinned NuGet package build/native folder")
endif()
# Probe/harness code only (screen capture, clipboard, selection, latency, the sandbox test
# harness in sandbox.cpp). Never shipped with production susu.exe.
add_library(susu_windows_probe SHARED probes.cpp sandbox.cpp webview_plugins.cpp windows_measure.cpp selection_test.cpp clipboard.cpp latency.cpp capture.cpp)
target_compile_features(susu_windows_probe PRIVATE cxx_std_20)
target_compile_definitions(susu_windows_probe PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
target_compile_options(susu_windows_probe PRIVATE /W4 /WX /EHsc /utf-8)
target_include_directories(susu_windows_probe PRIVATE "${WEBVIEW_SDK}/include")
target_link_libraries(susu_windows_probe PRIVATE "${WEBVIEW_SDK}/x64/WebView2LoaderStatic.lib" dwmapi d3d11 dxgi shcore ole32 oleaut32 oleacc user32 elscore version shlwapi userenv advapi32 uiautomationcore susu_plugin_sandbox)

# Product DLL (shipped with susu.exe): the WebView shell host.
add_library(susu_native SHARED shell_webview.cpp)
target_compile_features(susu_native PRIVATE cxx_std_20)
target_compile_definitions(susu_native PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
target_compile_options(susu_native PRIVATE /W4 /WX /EHsc /utf-8 /guard:cf)
target_link_options(susu_native PRIVATE /GUARD:CF /DYNAMICBASE /NXCOMPAT /CETCOMPAT)
target_include_directories(susu_native PRIVATE "${WEBVIEW_SDK}/include")
target_link_libraries(susu_native PRIVATE "${WEBVIEW_SDK}/x64/WebView2LoaderStatic.lib" ole32 oleaut32 user32 advapi32 shlwapi version)

# Product DLL (shipped with susu.exe, F04.1): the AppContainer plugin-host sandbox only
# (container.cpp - profile, scoped read ACL, authenticated pipe, Job object, launch/verify).
# Validated as a prototype in F00 (X01-X07, 54/54); every export here is used by
# Susu.Windows.ContainerHost, the production plugin-host launcher (Susu.Plugins.HostSession).
# sandbox.cpp's own exports (susu_sandbox_probe_v2, susu_is_appcontainer, susu_job_memory_probe,
# susu_container_storage) are probe-harness-only and stay out of this module.
add_library(susu_plugin_sandbox SHARED container.cpp)
target_compile_features(susu_plugin_sandbox PRIVATE cxx_std_20)
target_compile_definitions(susu_plugin_sandbox PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
target_compile_options(susu_plugin_sandbox PRIVATE /W4 /WX /EHsc /utf-8 /guard:cf)
target_link_options(susu_plugin_sandbox PRIVATE /GUARD:CF /DYNAMICBASE /NXCOMPAT /CETCOMPAT)
target_link_libraries(susu_plugin_sandbox PRIVATE ole32 advapi32 userenv)

# Product DLL (shipped with susu.exe, F08.1): the selection helper's read-only UIA (selection.cpp)
# and IA2 (ia2.cpp) levels, loaded only by `susu.exe --selection-host` (Susu.Windows.Selection).
add_library(susu_selection SHARED selection.cpp ia2.cpp)
target_compile_features(susu_selection PRIVATE cxx_std_20)
target_compile_definitions(susu_selection PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
target_compile_options(susu_selection PRIVATE /W4 /WX /EHsc /utf-8 /guard:cf)
target_link_options(susu_selection PRIVATE /GUARD:CF /DYNAMICBASE /NXCOMPAT /CETCOMPAT)
target_link_libraries(susu_selection PRIVATE ole32 oleaut32 oleacc user32 uiautomationcore)
