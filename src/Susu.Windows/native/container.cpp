// Disposable plugin-host launcher: AppContainer profile, scoped read ACL,
// authenticated named pipe and Job object. Validated as a prototype in F00
// (X01-X07, 54/54); shipped to production from F04.1 (Susu.Plugins.HostSession).
#include <windows.h>
#include <userenv.h>
#include <aclapi.h>
#include <sddl.h>
#include <psapi.h>
#include <algorithm>
#include <string>
#include <vector>

namespace {
struct LocalFreer { void operator()(void* p) const { if (p) LocalFree(p); } };

struct Container {
    std::wstring name;
    std::wstring resource;
    PSID sid = nullptr;
    bool created = false;
    PSECURITY_DESCRIPTOR original = nullptr;
    PACL oldAcl = nullptr;
    bool granted = false;
};

DWORD GrantRead(Container* c) {
    DWORD error = GetNamedSecurityInfoW(c->resource.data(), SE_FILE_OBJECT, DACL_SECURITY_INFORMATION, nullptr, nullptr, &c->oldAcl, nullptr, &c->original);
    if (error) return error;
    EXPLICIT_ACCESSW access{};
    access.grfAccessPermissions = FILE_GENERIC_READ | FILE_GENERIC_EXECUTE;
    access.grfAccessMode = GRANT_ACCESS;
    access.grfInheritance = SUB_CONTAINERS_AND_OBJECTS_INHERIT;
    access.Trustee.TrusteeForm = TRUSTEE_IS_SID;
    access.Trustee.TrusteeType = TRUSTEE_IS_UNKNOWN;
    access.Trustee.ptstrName = reinterpret_cast<LPWSTR>(c->sid);
    PACL acl = nullptr;
    error = SetEntriesInAclW(1, &access, c->oldAcl, &acl);
    if (!error) {
        error = SetNamedSecurityInfoW(c->resource.data(), SE_FILE_OBJECT, DACL_SECURITY_INFORMATION, nullptr, nullptr, acl, nullptr);
        c->granted = error == ERROR_SUCCESS;
    }
    if (acl) LocalFree(acl);
    return error;
}

bool TokenContainerSid(HANDLE process, std::vector<unsigned char>& info, PSID* sid, DWORD* isContainer) {
    HANDLE token = nullptr;
    if (!OpenProcessToken(process, TOKEN_QUERY, &token)) return false;
    DWORD length = 0;
    bool ok = GetTokenInformation(token, TokenIsAppContainer, isContainer, sizeof(*isContainer), &length) != FALSE;
    *sid = nullptr;
    if (ok && *isContainer) {
        GetTokenInformation(token, TokenAppContainerSid, nullptr, 0, &length);
        info.resize(length);
        ok = GetTokenInformation(token, TokenAppContainerSid, info.data(), length, &length) != FALSE;
        if (ok) *sid = reinterpret_cast<TOKEN_APPCONTAINER_INFORMATION*>(info.data())->TokenAppContainer;
    }
    CloseHandle(token);
    return ok;
}
}

// Creates the profile, or derives the SID of an existing one when reuse is
// requested (restart path). Grants read/execute on resourceDirectory only.
extern "C" __declspec(dllexport) HRESULT susu_container_open(const wchar_t* name, const wchar_t* resourceDirectory, int allowExisting, void** handle, int* created) {
    if (!name || !resourceDirectory || !handle || !created) return E_INVALIDARG;
    *handle = nullptr; *created = 0;
    auto c = new (std::nothrow) Container{};
    if (!c) return E_OUTOFMEMORY;
    c->name = name; c->resource = resourceDirectory;
    HRESULT hr = CreateAppContainerProfile(name, name, L"Disposable Su-Su F00 plugin host probe", nullptr, 0, &c->sid);
    if (hr == HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS) && allowExisting) hr = DeriveAppContainerSidFromAppContainerName(name, &c->sid);
    else if (SUCCEEDED(hr)) c->created = true;
    if (FAILED(hr)) { delete c; return hr; }
    DWORD error = GrantRead(c);
    if (error) { FreeSid(c->sid); if (c->created) DeleteAppContainerProfile(name); delete c; return HRESULT_FROM_WIN32(error); }
    *created = c->created ? 1 : 0;
    *handle = c;
    return S_OK;
}

extern "C" __declspec(dllexport) HRESULT susu_container_sid(void* handle, wchar_t* text, unsigned int capacity) {
    auto c = static_cast<Container*>(handle);
    if (!c || !text || capacity < 2) return E_INVALIDARG;
    LPWSTR value = nullptr;
    if (!ConvertSidToStringSidW(c->sid, &value)) return HRESULT_FROM_WIN32(GetLastError());
    size_t length = wcslen(value);
    HRESULT hr = length < capacity ? S_OK : HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
    if (SUCCEEDED(hr)) memcpy(text, value, (length + 1) * sizeof(wchar_t));
    LocalFree(value);
    return hr;
}

// Restores the resource ACL; optionally deletes the profile (uninstall path).
extern "C" __declspec(dllexport) HRESULT susu_container_close(void* handle, int deleteProfile) {
    auto c = static_cast<Container*>(handle);
    if (!c) return E_INVALIDARG;
    HRESULT result = S_OK;
    if (c->granted) {
        DWORD error = SetNamedSecurityInfoW(c->resource.data(), SE_FILE_OBJECT, DACL_SECURITY_INFORMATION, nullptr, nullptr, c->oldAcl, nullptr);
        if (error) result = HRESULT_FROM_WIN32(error);
    }
    if (c->original) LocalFree(c->original);
    if (deleteProfile) { HRESULT hr = DeleteAppContainerProfile(c->name.c_str()); if (FAILED(hr) && SUCCEEDED(result)) result = hr; }
    if (c->sid) FreeSid(c->sid);
    delete c;
    return result;
}

// Server end of the host pipe. The DACL grants only this container SID (the
// creating server already holds its handle), the Low mandatory label permits
// the Low-IL container client to write, remote clients are rejected, and the
// first-instance flag makes a pre-existing (squatted) name a hard failure.
// extraSid (optional, SDDL form) exists only so tests can build a control pipe.
extern "C" __declspec(dllexport) HRESULT susu_container_pipe(void* handle, const wchar_t* pipeName, const wchar_t* extraSid, int rejectRemote, HANDLE* pipe) {
    auto c = static_cast<Container*>(handle);
    if (!pipeName || !pipe) return E_INVALIDARG;
    *pipe = INVALID_HANDLE_VALUE;
    std::wstring sddl = L"D:P";
    if (c) {
        LPWSTR sid = nullptr;
        if (!ConvertSidToStringSidW(c->sid, &sid)) return HRESULT_FROM_WIN32(GetLastError());
        sddl += L"(A;;GRGW;;;" + std::wstring(sid) + L")";
        LocalFree(sid);
    }
    if (extraSid && *extraSid) sddl += L"(A;;GRGW;;;" + std::wstring(extraSid) + L")";
    sddl += L"S:(ML;;NW;;;LW)";
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), SDDL_REVISION_1, &descriptor, nullptr)) return HRESULT_FROM_WIN32(GetLastError());
    SECURITY_ATTRIBUTES security{sizeof(security), descriptor, FALSE};
    DWORD mode = PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | (rejectRemote ? PIPE_REJECT_REMOTE_CLIENTS : 0);
    HANDLE value = CreateNamedPipeW(pipeName, PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE | FILE_FLAG_OVERLAPPED, mode, 1, 64 * 1024, 64 * 1024, 0, &security);
    DWORD error = GetLastError();
    LocalFree(descriptor);
    if (value == INVALID_HANDLE_VALUE) return HRESULT_FROM_WIN32(error);
    *pipe = value;
    return S_OK;
}

// Launches executable suspended inside this container and a new Job
// (kill-on-close, one active process, per-process memory cap), then resumes.
// Only the handles in inherit[] are inherited. There is no unsandboxed fallback.
extern "C" __declspec(dllexport) HRESULT susu_container_launch(void* handle, const wchar_t* executable, const wchar_t* arguments,
    const HANDLE* inherit, int inheritCount, HANDLE stdIn, HANDLE stdOut, unsigned long long memoryLimit,
    HANDLE* processOut, HANDLE* jobOut, DWORD* pidOut) {
    auto c = static_cast<Container*>(handle);
    if (!c || !executable || !arguments || !processOut || !jobOut || !pidOut || inheritCount < 0 || inheritCount > 8) return E_INVALIDARG;
    *processOut = nullptr; *jobOut = nullptr; *pidOut = 0;
    SIZE_T bytes = 0;
    InitializeProcThreadAttributeList(nullptr, 2, 0, &bytes);
    std::vector<unsigned char> storage(bytes);
    auto list = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(storage.data());
    if (!InitializeProcThreadAttributeList(list, 2, 0, &bytes)) return HRESULT_FROM_WIN32(GetLastError());
    SECURITY_CAPABILITIES capabilities{};
    capabilities.AppContainerSid = c->sid; // No capabilities: no internet/LAN/loopback.
    HRESULT hr = S_OK;
    std::vector<HANDLE> handles(inherit, inherit + inheritCount);
    if (!UpdateProcThreadAttribute(list, 0, PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES, &capabilities, sizeof(capabilities), nullptr, nullptr)) hr = HRESULT_FROM_WIN32(GetLastError());
    if (SUCCEEDED(hr) && !handles.empty() && !UpdateProcThreadAttribute(list, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handles.data(), handles.size() * sizeof(HANDLE), nullptr, nullptr)) hr = HRESULT_FROM_WIN32(GetLastError());
    HANDLE job = nullptr;
    if (SUCCEEDED(hr)) {
        job = CreateJobObjectW(nullptr, nullptr);
        if (!job) hr = HRESULT_FROM_WIN32(GetLastError());
    }
    if (SUCCEEDED(hr)) {
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_ACTIVE_PROCESS | JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION | (memoryLimit ? JOB_OBJECT_LIMIT_PROCESS_MEMORY : 0);
        limits.BasicLimitInformation.ActiveProcessLimit = 1;
        limits.ProcessMemoryLimit = static_cast<SIZE_T>(memoryLimit);
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, &limits, sizeof(limits))) hr = HRESULT_FROM_WIN32(GetLastError());
    }
    PROCESS_INFORMATION process{};
    if (SUCCEEDED(hr)) {
        STARTUPINFOEXW startup{};
        startup.StartupInfo.cb = sizeof(startup);
        startup.lpAttributeList = list;
        if (stdIn || stdOut) {
            startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
            startup.StartupInfo.hStdInput = stdIn;
            startup.StartupInfo.hStdOutput = stdOut;
            startup.StartupInfo.hStdError = stdOut;
        }
        std::wstring command = L"\"" + std::wstring(executable) + L"\" " + arguments;
        if (!CreateProcessW(executable, command.data(), nullptr, nullptr, handles.empty() ? FALSE : TRUE,
            EXTENDED_STARTUPINFO_PRESENT | CREATE_SUSPENDED | CREATE_NO_WINDOW, nullptr, c->resource.c_str(), &startup.StartupInfo, &process)) hr = HRESULT_FROM_WIN32(GetLastError());
    }
    if (SUCCEEDED(hr) && !AssignProcessToJobObject(job, process.hProcess)) {
        hr = HRESULT_FROM_WIN32(GetLastError());
        TerminateProcess(process.hProcess, 1);
        WaitForSingleObject(process.hProcess, 2000);
    }
    if (SUCCEEDED(hr) && ResumeThread(process.hThread) == static_cast<DWORD>(-1)) {
        hr = HRESULT_FROM_WIN32(GetLastError());
        TerminateJobObject(job, 1);
    }
    DeleteProcThreadAttributeList(list);
    if (process.hThread) CloseHandle(process.hThread);
    if (FAILED(hr)) {
        if (process.hProcess) CloseHandle(process.hProcess);
        if (job) CloseHandle(job);
        return hr;
    }
    *processOut = process.hProcess; *jobOut = job; *pidOut = process.dwProcessId;
    return S_OK;
}

// Parent-side client authentication after ConnectNamedPipe: the connected
// client must be exactly the launched process and must carry this container SID.
// result: 1 ok, 2 PID mismatch, 3 not an AppContainer, 4 SID mismatch.
extern "C" __declspec(dllexport) HRESULT susu_container_verify_client(void* handle, HANDLE pipe, HANDLE expectedProcess, int* result, DWORD* clientPid) {
    auto c = static_cast<Container*>(handle);
    if (!c || !pipe || !expectedProcess || !result || !clientPid) return E_INVALIDARG;
    *result = 0; *clientPid = 0;
    if (!GetNamedPipeClientProcessId(pipe, clientPid)) return HRESULT_FROM_WIN32(GetLastError());
    if (*clientPid != GetProcessId(expectedProcess)) { *result = 2; return S_OK; }
    std::vector<unsigned char> info;
    PSID sid = nullptr; DWORD isContainer = 0;
    if (!TokenContainerSid(expectedProcess, info, &sid, &isContainer)) return HRESULT_FROM_WIN32(GetLastError());
    if (!isContainer) { *result = 3; return S_OK; }
    *result = EqualSid(sid, c->sid) ? 1 : 4;
    return S_OK;
}

// Child-side server authentication.
extern "C" __declspec(dllexport) HRESULT susu_pipe_server_pid(HANDLE pipe, DWORD* pid) {
    if (!pipe || !pid) return E_INVALIDARG;
    return GetNamedPipeServerProcessId(pipe, pid) ? S_OK : HRESULT_FROM_WIN32(GetLastError());
}

// Launches a normal (non-container) child with an explicit handle list. Used
// for adversarial clients and the parent-crash harness.
extern "C" __declspec(dllexport) HRESULT susu_launch_plain(const wchar_t* executable, const wchar_t* arguments, HANDLE stdOut, HANDLE* processOut, DWORD* pidOut) {
    if (!executable || !arguments || !processOut || !pidOut) return E_INVALIDARG;
    SIZE_T bytes = 0;
    InitializeProcThreadAttributeList(nullptr, 1, 0, &bytes);
    std::vector<unsigned char> storage(bytes);
    auto list = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(storage.data());
    if (!InitializeProcThreadAttributeList(list, 1, 0, &bytes)) return HRESULT_FROM_WIN32(GetLastError());
    HRESULT hr = S_OK;
    if (stdOut && !UpdateProcThreadAttribute(list, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, &stdOut, sizeof(HANDLE), nullptr, nullptr)) hr = HRESULT_FROM_WIN32(GetLastError());
    STARTUPINFOEXW startup{};
    startup.StartupInfo.cb = sizeof(startup);
    startup.lpAttributeList = list;
    if (stdOut) { startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES; startup.StartupInfo.hStdOutput = stdOut; startup.StartupInfo.hStdError = stdOut; }
    PROCESS_INFORMATION process{};
    std::wstring command = L"\"" + std::wstring(executable) + L"\" " + arguments;
    if (SUCCEEDED(hr) && !CreateProcessW(executable, command.data(), nullptr, nullptr, stdOut ? TRUE : FALSE, EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW, nullptr, nullptr, &startup.StartupInfo, &process)) hr = HRESULT_FROM_WIN32(GetLastError());
    DeleteProcThreadAttributeList(list);
    if (FAILED(hr)) return hr;
    CloseHandle(process.hThread);
    *processOut = process.hProcess; *pidOut = process.dwProcessId;
    return S_OK;
}

// Private working set (bytes) of one process via the working-set pages list;
// matches the "private working set" definition used for PER measurements.
extern "C" __declspec(dllexport) HRESULT susu_process_memory(HANDLE process, unsigned long long* privateWorkingSet, unsigned long long* privateBytes, unsigned long long* peakPrivateBytes) {
    if (!process || !privateWorkingSet || !privateBytes || !peakPrivateBytes) return E_INVALIDARG;
    PROCESS_MEMORY_COUNTERS_EX counters{};
    if (!K32GetProcessMemoryInfo(process, reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&counters), sizeof(counters))) return HRESULT_FROM_WIN32(GetLastError());
    *privateBytes = counters.PrivateUsage;
    *peakPrivateBytes = counters.PeakPagefileUsage;
    std::vector<unsigned char> buffer(64 * 1024);
    for (;;) {
        auto info = reinterpret_cast<PSAPI_WORKING_SET_INFORMATION*>(buffer.data());
        if (K32QueryWorkingSet(process, info, static_cast<DWORD>(buffer.size()))) {
            unsigned long long pages = 0;
            for (ULONG_PTR i = 0; i < info->NumberOfEntries; ++i) if (!info->WorkingSetInfo[i].Shared) ++pages;
            SYSTEM_INFO system{}; GetSystemInfo(&system);
            *privateWorkingSet = pages * system.dwPageSize;
            return S_OK;
        }
        DWORD error = GetLastError();
        if (error != ERROR_BAD_LENGTH || buffer.size() > (256u << 20)) return HRESULT_FROM_WIN32(error);
        const size_t needed = sizeof(PSAPI_WORKING_SET_INFORMATION) + (info->NumberOfEntries + 4096) * sizeof(PSAPI_WORKING_SET_BLOCK);
        buffer.resize(std::max(buffer.size() * 2, needed));
    }
}
