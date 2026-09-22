#include <windows.h>
#include <objbase.h>
#include <oleauto.h>
#include <UIAutomation.h>
#include <wrl.h>
#include <string>
#include <cstdio>
#include <thread>

using Microsoft::WRL::ComPtr;
struct Apartment { HRESULT status=CoInitializeEx(nullptr,COINIT_MULTITHREADED); ~Apartment(){if(SUCCEEDED(status))CoUninitialize();} };

extern "C" __declspec(dllexport) HRESULT susu_read_selection(HWND target, wchar_t* text, unsigned int capacity, int* reason) {
    if(!target||!text||capacity<2||capacity>65537||!reason)return E_INVALIDARG;
    text[0]=0;*reason=0;
    Apartment apartment;
    if(FAILED(apartment.status))return apartment.status;
    ComPtr<IUIAutomation> automation;
    HRESULT hr=CoCreateInstance(CLSID_CUIAutomation8,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&automation));
    if(FAILED(hr))return hr;
    ComPtr<IUIAutomation2> automation2;
    if(SUCCEEDED(automation.As(&automation2))){automation2->put_ConnectionTimeout(150);automation2->put_TransactionTimeout(300);}
    ComPtr<IUIAutomationElement> element;
    const bool foreground=target==GetForegroundWindow();
    if(foreground){
        hr=automation->GetFocusedElement(&element);
        if(SUCCEEDED(hr)){
            int processId=0;DWORD expected=0;
            GetWindowThreadProcessId(target,&expected);
            hr=element->get_CurrentProcessId(&processId);
            if(SUCCEEDED(hr)&&static_cast<DWORD>(processId)!=expected){*reason=4;return S_OK;}
        }
    }else hr=automation->ElementFromHandle(target,&element);
    if(FAILED(hr))return hr;
    BOOL password=FALSE;
    hr=element->get_CurrentIsPassword(&password);
    if(FAILED(hr))return hr;
    if(password){*reason=1;return S_OK;}
    ComPtr<IUIAutomationTextPattern> pattern;
    hr=element->GetCurrentPatternAs(UIA_TextPatternId,IID_PPV_ARGS(&pattern));
    if(FAILED(hr)){*reason=2;return S_OK;}
    ComPtr<IUIAutomationTextRangeArray> ranges;
    hr=pattern->GetSelection(&ranges);
    if(FAILED(hr))return hr;
    int count=0;
    hr=ranges->get_Length(&count);
    if(FAILED(hr))return hr;
    if(count<0||count>32)return HRESULT_FROM_WIN32(ERROR_BUFFER_OVERFLOW);
    std::wstring selected;
    for(int i=0;i<count;++i){
        ComPtr<IUIAutomationTextRange> range;
        hr=ranges->GetElement(i,&range);if(FAILED(hr))return hr;
        BSTR value=nullptr;
        hr=range->GetText(static_cast<int>(capacity),&value);if(FAILED(hr))return hr;
        const auto length=SysStringLen(value);
        if(length&&wmemchr(value,0,length)){SysFreeString(value);return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);}
        if(length>0){if(!selected.empty())selected+=L"\n";selected.append(value,length);}
        SysFreeString(value);
        if(selected.size()>=capacity)return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
    }
    if(selected.empty()){*reason=3;return S_OK;}
    if(foreground){
        ComPtr<IUIAutomationElement> current;
        BOOL same=FALSE;
        hr=automation->GetFocusedElement(&current);
        if(SUCCEEDED(hr))hr=automation->CompareElements(element.Get(),current.Get(),&same);
        if(FAILED(hr))return hr;
        if(GetForegroundWindow()!=target||!same){*reason=4;return S_OK;}
    }
    memcpy(text,selected.c_str(),(selected.size()+1)*sizeof(wchar_t));
    return S_OK;
}

static void Target(bool password,bool empty){
    HWND parent=CreateWindowExW(WS_EX_NOACTIVATE,L"STATIC",L"Su-Su synthetic selection target",WS_OVERLAPPEDWINDOW,
        50,50,500,200,nullptr,nullptr,GetModuleHandleW(nullptr),nullptr);
    if(!parent){printf("0\n");fflush(stdout);return;}
    HWND edit=CreateWindowExW(0,L"EDIT",L"prefix selected text suffix",WS_CHILD|WS_VISIBLE|(password?(ES_PASSWORD|ES_AUTOHSCROLL):ES_MULTILINE),
        10,10,450,100,parent,nullptr,GetModuleHandleW(nullptr),nullptr);
    if(!edit){DestroyWindow(parent);printf("0\n");fflush(stdout);return;}
    SendMessageW(edit,EM_SETSEL,7,empty?7:20);
    ShowWindow(parent,SW_SHOWNOACTIVATE);
    printf("%llu\n",reinterpret_cast<unsigned long long>(edit));fflush(stdout);
    const ULONGLONG deadline=GetTickCount64()+15000;
    while(IsWindow(parent)&&GetTickCount64()<deadline){
        MSG msg{};while(PeekMessageW(&msg,nullptr,0,0,PM_REMOVE)){TranslateMessage(&msg);DispatchMessageW(&msg);}
        MsgWaitForMultipleObjectsEx(0,nullptr,20,QS_ALLINPUT,MWMO_INPUTAVAILABLE);
    }
    if(IsWindow(parent))DestroyWindow(parent);
}
extern "C" __declspec(dllexport) void susu_selection_target(int password,int empty){std::thread worker([&]{Target(password!=0,empty!=0);});worker.join();}
