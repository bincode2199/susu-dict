#include <windows.h>
#include <objbase.h>
#include <oleacc.h>
#include <servprov.h>
#include <wrl.h>
#include <algorithm>
#include <string>
#include <cstdio>

using Microsoft::WRL::ComPtr;
// ABI defined by LinuxA11y/IAccessible2 api/AccessibleText.idl. Only the
// read-only selection slots are called; no generated COM interop is used.
static constexpr GUID TextId={0x24fd2ffb,0x3aad,0x4a08,{0x83,0x35,0xa3,0xad,0x89,0xc0,0xfb,0x4b}};
using CountCall=HRESULT(STDMETHODCALLTYPE*)(void*,LONG*);
using RangeCall=HRESULT(STDMETHODCALLTYPE*)(void*,LONG,LONG*,LONG*);
using TextCall=HRESULT(STDMETHODCALLTYPE*)(void*,LONG,LONG,BSTR*);

static HRESULT ReadText(void* object,wchar_t* output,unsigned int capacity,int* reason){
    output[0]=0;*reason=3;
    auto table=*static_cast<void***>(object);
    LONG count=0;
    HRESULT hr=reinterpret_cast<CountCall>(table[7])(object,&count);
    if(FAILED(hr))return hr;
    if(count<0||count>32)return HRESULT_FROM_WIN32(ERROR_BUFFER_OVERFLOW);
    std::wstring selected;
    for(LONG index=0;index<count;++index){
        LONG start=0,end=0;
        hr=reinterpret_cast<RangeCall>(table[9])(object,index,&start,&end);
        if(FAILED(hr))return hr;
        if(start<0||end<0)return E_INVALIDARG;
        if(start==end)continue;
        if(end<start)std::swap(start,end);
        if(static_cast<unsigned long>(end-start)>=capacity)return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
        BSTR value=nullptr;
        hr=reinterpret_cast<TextCall>(table[10])(object,start,end,&value);
        const unsigned int length=SysStringLen(value);
        if(FAILED(hr)){SysFreeString(value);return hr;}
        if(length>=capacity||selected.size()+length+(selected.empty()?0:1)>=capacity){SysFreeString(value);return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);}
        if(length){
            // The output C ABI is NUL terminated: reject embedded NUL instead of truncating.
            if(wmemchr(value,0,length)){SysFreeString(value);return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);}
            if(!selected.empty())selected+=L'\n';
            selected.append(value,length);
        }
        SysFreeString(value);
    }
    if(!selected.empty()){memcpy(output,selected.c_str(),(selected.size()+1)*sizeof(wchar_t));*reason=0;}
    return S_OK;
}

extern "C" __declspec(dllexport) HRESULT susu_read_ia2(HWND target,wchar_t* output,unsigned int capacity,int* reason){
    if(!target||!output||capacity<2||capacity>65537||!reason)return E_INVALIDARG;
    output[0]=0;*reason=2;
    HRESULT hr=CoInitializeEx(nullptr,COINIT_MULTITHREADED);
    if(FAILED(hr))return hr;
    struct Apartment{~Apartment(){CoUninitialize();}} apartment;
    ComPtr<IAccessible> accessible;
    hr=AccessibleObjectFromWindow(target,static_cast<DWORD>(OBJID_CLIENT),IID_IAccessible,reinterpret_cast<void**>(accessible.GetAddressOf()));
    if(FAILED(hr)){fprintf(stderr,"MSAA stage=AccessibleObjectFromWindow\n");return hr;}
    // Follow only the provider's focus chain, never search arbitrary document text.
    for(int depth=0;depth<32;++depth){
        VARIANT self{};self.vt=VT_I4;self.lVal=CHILDID_SELF;
        VARIANT state{};
        hr=accessible->get_accState(self,&state);
        const bool protectedField=SUCCEEDED(hr)&&state.vt==VT_I4&&(state.lVal&STATE_SYSTEM_PROTECTED)!=0;
        VariantClear(&state);
        if(FAILED(hr)){fprintf(stderr,"MSAA stage=get_accState\n");return hr;}
        if(protectedField){*reason=1;return S_OK;}
        VARIANT focus{};
        hr=accessible->get_accFocus(&focus);
        if(FAILED(hr)){VariantClear(&focus);fprintf(stderr,"MSAA stage=get_accFocus\n");return hr;}
        ComPtr<IAccessible> child;
        if(focus.vt==VT_DISPATCH&&focus.pdispVal)hr=focus.pdispVal->QueryInterface(IID_PPV_ARGS(&child));
        else if(focus.vt==VT_I4&&focus.lVal!=CHILDID_SELF){
            VARIANT childState{};
            hr=accessible->get_accState(focus,&childState);
            bool childProtected=SUCCEEDED(hr)&&childState.vt==VT_I4&&(childState.lVal&STATE_SYSTEM_PROTECTED)!=0;
            VariantClear(&childState);
            if(childProtected){VariantClear(&focus);*reason=1;return S_OK;}
            ComPtr<IDispatch> dispatch;
            if(SUCCEEDED(hr))hr=accessible->get_accChild(focus,&dispatch);
            if(SUCCEEDED(hr)&&dispatch)hr=dispatch.As(&child);
            if(SUCCEEDED(hr)&&!child){VariantClear(&focus);return S_OK;}
        }
        VariantClear(&focus);
        if(FAILED(hr)){fprintf(stderr,"MSAA stage=focused-child\n");return hr;}
        if(child){accessible=child;continue;}
        ComPtr<IServiceProvider> provider;
        hr=accessible.As(&provider);
        if(hr==E_NOINTERFACE)return S_OK;
        if(FAILED(hr)){fprintf(stderr,"MSAA stage=IServiceProvider\n");return hr;}
        ComPtr<IUnknown> text;
        hr=provider->QueryService(IID_IAccessible,TextId,reinterpret_cast<void**>(text.GetAddressOf()));
        // The Windows standard Edit accessibility provider returns E_INVALIDARG
        // for this unsupported service; retain other COM failures as failures.
        if(hr==E_NOINTERFACE||hr==E_INVALIDARG)return S_OK;
        if(FAILED(hr)){fprintf(stderr,"MSAA stage=IAccessibleText-QueryService\n");return hr;}
        return text?ReadText(text.Get(),output,capacity,reason):E_POINTER;
    }
    return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
}

// Controlled ABI fixture. This verifies slot/signature/bounds handling only;
// actual Firefox/Electron out-of-process providers need separate evidence.
struct FakeText{void** table;int mode;int calls=0;};
static HRESULT STDMETHODCALLTYPE Count(void* object,LONG* count){
    int mode=static_cast<FakeText*>(object)->mode;
    *count=mode==1?0:mode==2?33:mode==6?2:1;return mode==5?E_FAIL:S_OK;
}
static HRESULT STDMETHODCALLTYPE Range(void* object,LONG index,LONG* start,LONG* end){
    auto fake=static_cast<FakeText*>(object);++fake->calls;
    *start=fake->mode==3?-1:0;*end=fake->mode==4?100000:fake->mode==7?0:index==0?5:4;return S_OK;
}
static HRESULT STDMETHODCALLTYPE Text(void* object,LONG,LONG end,BSTR* value){
    auto fake=static_cast<FakeText*>(object);++fake->calls;
    *value=fake->mode==8?SysAllocStringLen(L"a\0b",3):SysAllocString(end==5?L"first":L"next");return *value?S_OK:E_OUTOFMEMORY;
}
extern "C" __declspec(dllexport) HRESULT susu_ia2_abi_probe(){
    void* table[11]{};
    table[7]=reinterpret_cast<void*>(&Count);table[9]=reinterpret_cast<void*>(&Range);table[10]=reinterpret_cast<void*>(&Text);
    for(int mode=0;mode<=8;++mode){
        FakeText fake{table,mode};wchar_t text[64]{};int reason=-1;
        HRESULT hr=ReadText(&fake,text,64,&reason);
        if(mode==2||mode==3||mode==4||mode==5||mode==8){if(SUCCEEDED(hr)||text[0]!=0)return E_FAIL;}
        else if(mode==1||mode==7){if(FAILED(hr)||reason!=3||text[0]!=0||fake.calls!=(mode==1?0:1))return E_FAIL;}
        else if(FAILED(hr)||reason!=0||wcscmp(text,mode==6?L"first\nnext":L"first")!=0)return E_FAIL;
    }
    return S_OK;
}
