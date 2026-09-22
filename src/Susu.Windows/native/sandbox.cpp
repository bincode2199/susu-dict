#include <windows.h>
#include <userenv.h>
#include <aclapi.h>
#include <string>
#include <vector>
#include <memory>
#include <sddl.h>
#include <objbase.h>

struct Handle {
    HANDLE value = nullptr;
    ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
};
struct Profile {
    std::wstring name;
    PSID sid = nullptr;
    bool deleted=false;
    HRESULT Remove(){HRESULT hr=DeleteAppContainerProfile(name.c_str());deleted=SUCCEEDED(hr);return hr;}
    ~Profile() { if (sid) { if(!deleted)DeleteAppContainerProfile(name.c_str()); FreeSid(sid); } }
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
    DWORD Restore(){if(!applied)return ERROR_SUCCESS;DWORD error=SetNamedSecurityInfoW(path.data(),SE_FILE_OBJECT,DACL_SECURITY_INFORMATION,nullptr,nullptr,oldAcl,nullptr);if(!error)applied=false;return error;}
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
extern "C" __declspec(dllexport) HRESULT susu_sandbox_probe_v2(
    const wchar_t* executable, const wchar_t* resourceDirectory, const wchar_t* profileName,
    const wchar_t* arguments, char* output, unsigned int capacity, unsigned int* exitCode, int* stage,int closeJob) {
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
    if(closeJob){
        // Wait for the child to demonstrate that it actually ran in its container
        // before closing our only Job handle. The child intentionally stays alive.
        DWORD available=0;
        const ULONGLONG deadline=GetTickCount64()+2000;
        do{
            if(!PeekNamedPipe(read.value,nullptr,0,nullptr,&available,nullptr))return HRESULT_FROM_WIN32(GetLastError());
            if(available)break;
            if(WaitForSingleObject(child.value,10)==WAIT_OBJECT_0)return E_UNEXPECTED;
        }while(GetTickCount64()<deadline);
        if(!available)return HRESULT_FROM_WIN32(WAIT_TIMEOUT);
        CloseHandle(job.value);job.value=nullptr;
        if(WaitForSingleObject(child.value,2000)!=WAIT_OBJECT_0)return HRESULT_FROM_WIN32(WAIT_TIMEOUT);
    }
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
    *stage=7;
    CloseHandle(thread.value);thread.value=nullptr;
    CloseHandle(child.value);child.value=nullptr;
    if(job.value){CloseHandle(job.value);job.value=nullptr;}
    error=grant.Restore();if(error)return HRESULT_FROM_WIN32(error);
    hr=profile.Remove();if(FAILED(hr))return hr;
    return S_OK;
}

extern "C" __declspec(dllexport) HRESULT susu_job_memory_probe(int* error){
    if(!error)return E_POINTER;
    void* allocation=VirtualAlloc(nullptr,256ull*1024*1024,MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE);
    *error=allocation?0:static_cast<int>(GetLastError());
    if(allocation)VirtualFree(allocation,0,MEM_RELEASE);
    return S_OK;
}

extern "C" __declspec(dllexport) HRESULT susu_container_storage(wchar_t* folder,unsigned int capacity){
    if(!folder||capacity<2)return E_INVALIDARG;
    folder[0]=0;
    int container=0;HRESULT hr=susu_is_appcontainer(&container);
    if(FAILED(hr)||!container)return E_ACCESSDENIED;
    Handle token;
    if(!OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY,&token.value))return HRESULT_FROM_WIN32(GetLastError());
    DWORD bytes=0;
    GetTokenInformation(token.value,TokenAppContainerSid,nullptr,0,&bytes);
    std::vector<unsigned char> info(bytes);
    if(!GetTokenInformation(token.value,TokenAppContainerSid,info.data(),bytes,&bytes))return HRESULT_FROM_WIN32(GetLastError());
    auto sid=reinterpret_cast<TOKEN_APPCONTAINER_INFORMATION*>(info.data())->TokenAppContainer;
    LPWSTR sidText=nullptr;
    if(!ConvertSidToStringSidW(sid,&sidText))return HRESULT_FROM_WIN32(GetLastError());
    PWSTR path=nullptr;hr=GetAppContainerFolderPath(sidText,&path);LocalFree(sidText);
    if(FAILED(hr))return hr;
    const size_t length=wcslen(path);
    if(length>=capacity){CoTaskMemFree(path);return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);}
    memcpy(folder,path,(length+1)*sizeof(wchar_t));CoTaskMemFree(path);
    HKEY root=nullptr,key=nullptr;
    hr=GetAppContainerRegistryLocation(KEY_READ|KEY_WRITE,&root);
    if(FAILED(hr))return hr;
    DWORD disposition=0;
    LONG error=RegCreateKeyExW(root,L"SusuF00Probe",0,nullptr,0,KEY_READ|KEY_WRITE,nullptr,&key,&disposition);
    if(error){RegCloseKey(root);return HRESULT_FROM_WIN32(error);}
    DWORD expected=42,actual=0,type=0,size=sizeof(actual);
    error=RegSetValueExW(key,L"Synthetic",0,REG_DWORD,reinterpret_cast<const BYTE*>(&expected),sizeof(expected));
    if(!error)error=RegQueryValueExW(key,L"Synthetic",nullptr,&type,reinterpret_cast<BYTE*>(&actual),&size);
    RegCloseKey(key);
    LONG cleanup=RegDeleteKeyW(root,L"SusuF00Probe");RegCloseKey(root);
    if(error)return HRESULT_FROM_WIN32(error);
    if(cleanup)return HRESULT_FROM_WIN32(cleanup);
    return type==REG_DWORD&&size==sizeof(actual)&&actual==expected?S_OK:E_UNEXPECTED;
}
