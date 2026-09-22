#include <windows.h>
#include <userenv.h>
#include <aclapi.h>
#include <string>
#include <vector>
#include <memory>

struct Handle {
    HANDLE value = nullptr;
    ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
};
struct Profile {
    std::wstring name;
    PSID sid = nullptr;
    ~Profile() { if (sid) { DeleteAppContainerProfile(name.c_str()); FreeSid(sid); } }
};
struct Attributes {
    std::vector<unsigned char> storage;
    LPPROC_THREAD_ATTRIBUTE_LIST list = nullptr;
    ~Attributes() { if (list) DeleteProcThreadAttributeList(list); }
};
struct DirectoryGrant {
    std::wstring path;
    PSECURITY_DESCRIPTOR original = nullptr;
    PACL oldAcl = nullptr;
    bool applied = false;
    ~DirectoryGrant() {
        if (applied) SetNamedSecurityInfoW(path.data(), SE_FILE_OBJECT, DACL_SECURITY_INFORMATION, nullptr, nullptr, oldAcl, nullptr);
        if (original) LocalFree(original);
    }
    DWORD Grant(PSID sid) {
        DWORD error = GetNamedSecurityInfoW(path.data(), SE_FILE_OBJECT, DACL_SECURITY_INFORMATION, nullptr, nullptr, &oldAcl, nullptr, &original);
        if (error) return error;
        EXPLICIT_ACCESSW access{};
        access.grfAccessPermissions = FILE_GENERIC_READ | FILE_GENERIC_EXECUTE;
        access.grfAccessMode = GRANT_ACCESS;
        access.grfInheritance = SUB_CONTAINERS_AND_OBJECTS_INHERIT;
        access.Trustee.TrusteeForm = TRUSTEE_IS_SID;
        access.Trustee.TrusteeType = TRUSTEE_IS_UNKNOWN;
        access.Trustee.ptstrName = reinterpret_cast<LPWSTR>(sid);
        PACL acl = nullptr;
        error = SetEntriesInAclW(1, &access, oldAcl, &acl);
        if (!error) {
            error = SetNamedSecurityInfoW(path.data(), SE_FILE_OBJECT, DACL_SECURITY_INFORMATION, nullptr, nullptr, acl, nullptr);
            applied = error == ERROR_SUCCESS;
        }
        if (acl) LocalFree(acl);
        return error;
    }
};

extern "C" __declspec(dllexport) HRESULT susu_is_appcontainer(int* value) {
    if (!value) return E_POINTER;
    Handle token;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token.value)) return HRESULT_FROM_WIN32(GetLastError());
    DWORD isContainer = 0, length = 0;
    if (!GetTokenInformation(token.value, TokenIsAppContainer, &isContainer, sizeof(isContainer), &length)) return HRESULT_FROM_WIN32(GetLastError());
    *value = isContainer ? 1 : 0;
    return S_OK;
}

// Disposable F00 harness, not production IPC. Only one stdout handle is inherited.
extern "C" __declspec(dllexport) HRESULT susu_sandbox_probe(
    const wchar_t* executable, const wchar_t* resourceDirectory, const wchar_t* profileName,
    const wchar_t* arguments, char* output, unsigned int capacity, unsigned int* exitCode, int* stage) {
    if (!executable || !resourceDirectory || !profileName || !arguments || !output || capacity < 2 || !exitCode || !stage) return E_INVALIDARG;
    output[0] = 0;
    *stage = 1;
    Profile profile{profileName};
    HRESULT hr = CreateAppContainerProfile(profileName, profileName, L"Disposable Su-Su F00 probe", nullptr, 0, &profile.sid);
    if (FAILED(hr)) return hr; // Never fall back to an unrestricted process.
    *stage = 2;
    DirectoryGrant grant{resourceDirectory};
    DWORD error = grant.Grant(profile.sid);
    if (error) return HRESULT_FROM_WIN32(error);
    *stage = 3;
    Handle read, write, job;
    SECURITY_ATTRIBUTES security{sizeof(SECURITY_ATTRIBUTES), nullptr, TRUE};
    if (!CreatePipe(&read.value, &write.value, &security, 4096)) return HRESULT_FROM_WIN32(GetLastError());
    if (!SetHandleInformation(read.value, HANDLE_FLAG_INHERIT, 0)) return HRESULT_FROM_WIN32(GetLastError());
    Attributes attributes;
    SIZE_T bytes = 0;
    InitializeProcThreadAttributeList(nullptr, 2, 0, &bytes);
    attributes.storage.resize(bytes);
    auto list = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(attributes.storage.data());
    if (!InitializeProcThreadAttributeList(list, 2, 0, &bytes)) return HRESULT_FROM_WIN32(GetLastError());
    attributes.list = list;
    SECURITY_CAPABILITIES capabilities{};
    capabilities.AppContainerSid = profile.sid;
    if (!UpdateProcThreadAttribute(list, 0, PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES, &capabilities, sizeof(capabilities), nullptr, nullptr)) return HRESULT_FROM_WIN32(GetLastError());
    if (!UpdateProcThreadAttribute(list, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, &write.value, sizeof(write.value), nullptr, nullptr)) return HRESULT_FROM_WIN32(GetLastError());
    job.value = CreateJobObjectW(nullptr, nullptr);
    if (!job.value) return HRESULT_FROM_WIN32(GetLastError());
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_ACTIVE_PROCESS | JOB_OBJECT_LIMIT_PROCESS_MEMORY;
    limits.BasicLimitInformation.ActiveProcessLimit = 1;
    limits.ProcessMemoryLimit = 128 * 1024 * 1024;
    if (!SetInformationJobObject(job.value, JobObjectExtendedLimitInformation, &limits, sizeof(limits))) return HRESULT_FROM_WIN32(GetLastError());
    STARTUPINFOEXW startup{};
    startup.StartupInfo.cb = sizeof(startup);
    startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
    startup.StartupInfo.hStdOutput = write.value;
    startup.StartupInfo.hStdError = write.value;
    startup.lpAttributeList = list;
    PROCESS_INFORMATION process{};
    std::wstring command = L"\"" + std::wstring(executable) + L"\" " + arguments;
    *stage = 4;
    if (!CreateProcessW(executable, command.data(), nullptr, nullptr, TRUE,
        EXTENDED_STARTUPINFO_PRESENT | CREATE_SUSPENDED | CREATE_NO_WINDOW, nullptr, resourceDirectory, &startup.StartupInfo, &process)) return HRESULT_FROM_WIN32(GetLastError());
    Handle child{process.hProcess}, thread{process.hThread};
    *stage = 5;
    if (!AssignProcessToJobObject(job.value, child.value)) {
        error = GetLastError(); TerminateProcess(child.value, 1); WaitForSingleObject(child.value, 2000); return HRESULT_FROM_WIN32(error);
    }
    if (ResumeThread(thread.value) == static_cast<DWORD>(-1)) return HRESULT_FROM_WIN32(GetLastError());
    *stage = 6;
    CloseHandle(write.value); write.value = nullptr;
    DWORD wait = WaitForSingleObject(child.value, 10000);
    if (wait != WAIT_OBJECT_0) { TerminateJobObject(job.value, 1); WaitForSingleObject(child.value, 2000); return HRESULT_FROM_WIN32(wait == WAIT_TIMEOUT ? WAIT_TIMEOUT : GetLastError()); }
    DWORD code = 0;
    if (!GetExitCodeProcess(child.value, &code)) return HRESULT_FROM_WIN32(GetLastError());
    *exitCode = code;
    DWORD available = 0;
    if (!PeekNamedPipe(read.value, nullptr, 0, nullptr, &available, nullptr)) {
        error = GetLastError();
        if (error == ERROR_BROKEN_PIPE && code != 0) return S_OK;
        return HRESULT_FROM_WIN32(error);
    }
    if (available >= capacity) return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
    DWORD received = 0;
    if (available && !ReadFile(read.value, output, available, &received, nullptr)) return HRESULT_FROM_WIN32(GetLastError());
    output[received] = 0;
    return S_OK;
}
