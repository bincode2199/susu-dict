// Product (susu_selection.dll, F08.1): level 1 of the selection helper, UIA TextPattern read
// (ARCHITECTURE 4.1). Loaded only by `susu.exe --selection-host`, which calls it on its own MTA
// thread; the parent process enforces the hard deadline by terminating the helper. Level 2 (IA2)
// is ia2.cpp. Read-only: never sets focus, selection or clipboard.
#include <windows.h>
#include <objbase.h>
#include <oleauto.h>
#include <UIAutomation.h>
#include <wrl.h>
#include <algorithm>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;
struct Apartment { HRESULT status=CoInitializeEx(nullptr,COINIT_MULTITHREADED); ~Apartment(){if(SUCCEEDED(status))CoUninitialize();} };

// reason: 0 selected, 1 password, 2 unsupported (no TextPattern), 3 empty selection, 4 focus changed.
static HRESULT ReadSelection(HWND target,unsigned int timeoutMs,wchar_t* text,unsigned int capacity,int* reason,double* rect,int* ranges){
    if(!target||!text||capacity<2||capacity>65537||!reason||!rect||!ranges)return E_INVALIDARG;
    text[0]=0;*reason=0;*ranges=0;
    rect[0]=rect[1]=rect[2]=rect[3]=0;
    Apartment apartment;
    if(FAILED(apartment.status))return apartment.status;
    ComPtr<IUIAutomation> automation;
    HRESULT hr=CoCreateInstance(CLSID_CUIAutomation8,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&automation));
    if(FAILED(hr))return hr;
    ComPtr<IUIAutomation2> automation2;
    const DWORD budget=std::clamp(timeoutMs,50u,5000u);
    if(SUCCEEDED(automation.As(&automation2))){automation2->put_ConnectionTimeout(std::min<DWORD>(150,budget));automation2->put_TransactionTimeout(budget);}
    ComPtr<IUIAutomationElement> element;
    const bool foreground=target==GetForegroundWindow();
    if(foreground){
        hr=automation->GetFocusedElement(&element);
        if(SUCCEEDED(hr)){
            // Compare with the target window's UIA provider process, not GetWindowThreadProcessId:
            // console windows report the attached client (cmd.exe) while conhost provides UIA.
            int processId=0,expected=0;
            ComPtr<IUIAutomationElement> windowElement;
            hr=automation->ElementFromHandle(target,&windowElement);
            if(SUCCEEDED(hr))hr=windowElement->get_CurrentProcessId(&expected);
            if(SUCCEEDED(hr))hr=element->get_CurrentProcessId(&processId);
            if(SUCCEEDED(hr)&&processId!=expected){*reason=4;return S_OK;}
        }
    }else hr=automation->ElementFromHandle(target,&element);
    if(FAILED(hr))return hr;
    // SEL03: a password field is never read, whatever its provider says about its selection.
    BOOL password=FALSE;
    hr=element->get_CurrentIsPassword(&password);
    if(FAILED(hr))return hr;
    if(password){*reason=1;return S_OK;}
    ComPtr<IUIAutomationTextPattern> pattern;
    hr=element->GetCurrentPatternAs(UIA_TextPatternId,IID_PPV_ARGS(&pattern));
    if(FAILED(hr)||!pattern){
        // PLAN 3.1: walk up from the focused element to the nearest TextPattern provider
        // (e.g. a browser document around a focused link), bounded and within the same window.
        ComPtr<IUIAutomationTreeWalker> walker;
        pattern.Reset();
        if(SUCCEEDED(automation->get_ControlViewWalker(&walker))){
            ComPtr<IUIAutomationElement> current=element;
            for(int depth=0;depth<8&&!pattern;++depth){
                ComPtr<IUIAutomationElement> parent;
                if(FAILED(walker->GetParentElement(current.Get(),&parent))||!parent)break;
                UIA_HWND native=nullptr;
                parent->get_CurrentNativeWindowHandle(&native);
                if(native&&GetAncestor(static_cast<HWND>(native),GA_ROOT)!=GetAncestor(target,GA_ROOT))break;
                ComPtr<IUIAutomationTextPattern> candidate;
                if(SUCCEEDED(parent->GetCurrentPatternAs(UIA_TextPatternId,IID_PPV_ARGS(&candidate)))&&candidate)pattern=candidate;
                current=parent;
            }
        }
        if(!pattern){*reason=2;return S_OK;}
    }
    ComPtr<IUIAutomationTextRangeArray> array;
    hr=pattern->GetSelection(&array);
    if(FAILED(hr))return hr;
    int count=0;
    hr=array->get_Length(&count);
    if(FAILED(hr))return hr;
    if(count<0||count>32)return HRESULT_FROM_WIN32(ERROR_BUFFER_OVERFLOW);
    std::vector<ComPtr<IUIAutomationTextRange>> list;
    for(int i=0;i<count;++i){
        ComPtr<IUIAutomationTextRange> range;
        hr=array->GetElement(i,&range);if(FAILED(hr))return hr;
        list.push_back(range);
    }
    // Multiple selections (ARCHITECTURE 4.1): merged in document order, which is the reading order
    // of one text provider, each separated by an explicit line break.
    std::stable_sort(list.begin(),list.end(),[](const ComPtr<IUIAutomationTextRange>& a,const ComPtr<IUIAutomationTextRange>& b){
        int order=0;
        return SUCCEEDED(a->CompareEndpoints(TextPatternRangeEndpoint_Start,b.Get(),TextPatternRangeEndpoint_Start,&order))&&order<0;
    });
    std::wstring selected;
    for(auto& range:list){
        BSTR value=nullptr;
        hr=range->GetText(static_cast<int>(capacity),&value);if(FAILED(hr))return hr;
        const auto length=SysStringLen(value);
        if(length&&wmemchr(value,0,length)){SysFreeString(value);return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);}
        if(length>0){if(!selected.empty())selected+=L"\n";selected.append(value,length);++*ranges;}
        SysFreeString(value);
        if(selected.size()>=capacity)return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
        if(length==0)continue;
        // Union of the selection's bounding rectangles (physical screen pixels: the helper is
        // per-monitor DPI aware) for the float anchor.
        SAFEARRAY* boxes=nullptr;
        if(SUCCEEDED(range->GetBoundingRectangles(&boxes))&&boxes){
            double* values=nullptr;
            LONG upper=-1;
            if(SUCCEEDED(SafeArrayGetUBound(boxes,1,&upper))&&SUCCEEDED(SafeArrayAccessData(boxes,reinterpret_cast<void**>(&values)))){
                for(LONG k=0;k+3<=upper;k+=4){
                    const double l=values[k],t=values[k+1],r=l+values[k+2],b=t+values[k+3];
                    if(values[k+2]<=0||values[k+3]<=0)continue;
                    if(rect[2]<=rect[0]){rect[0]=l;rect[1]=t;rect[2]=r;rect[3]=b;}
                    else{rect[0]=std::min(rect[0],l);rect[1]=std::min(rect[1],t);rect[2]=std::max(rect[2],r);rect[3]=std::max(rect[3],b);}
                }
                SafeArrayUnaccessData(boxes);
            }
            SafeArrayDestroy(boxes);
        }
    }
    // SEL03: an empty selection is an explicit failure; the caret paragraph is never substituted.
    if(selected.empty()){*reason=3;*ranges=0;rect[0]=rect[1]=rect[2]=rect[3]=0;return S_OK;}
    if(foreground){
        ComPtr<IUIAutomationElement> current;
        BOOL same=FALSE;
        hr=automation->GetFocusedElement(&current);
        if(SUCCEEDED(hr))hr=automation->CompareElements(element.Get(),current.Get(),&same);
        if(FAILED(hr))return hr;
        if(GetForegroundWindow()!=target||!same){*reason=4;*ranges=0;return S_OK;}
    }
    memcpy(text,selected.c_str(),(selected.size()+1)*sizeof(wchar_t));
    return S_OK;
}

// rect: left, top, right, bottom in physical screen pixels; all zero when unavailable.
extern "C" __declspec(dllexport) HRESULT susu_selection_uia(HWND target,unsigned int timeoutMs,wchar_t* text,unsigned int capacity,int* reason,double* rect,int* ranges){
    return ReadSelection(target,timeoutMs,text,capacity,reason,rect,ranges);
}
