#include <windows.h>
#include <objbase.h>
#include <oleacc.h>
#include <servprov.h>
#include <wrl.h>
#include <algorithm>
#include <string>
#include <vector>
#include <cstdio>

using Microsoft::WRL::ComPtr;
// ABI defined by LinuxA11y/IAccessible2 api/AccessibleText.idl. Only the
// read-only selection slots are called; no generated COM interop is used.
static constexpr GUID TextId={0x24fd2ffb,0x3aad,0x4a08,{0x83,0x35,0xa3,0xad,0x89,0xc0,0xfb,0x4b}};
using CountCall=HRESULT(STDMETHODCALLTYPE*)(void*,LONG*);
using RangeCall=HRESULT(STDMETHODCALLTYPE*)(void*,LONG,LONG*,LONG*);
using TextCall=HRESULT(STDMETHODCALLTYPE*)(void*,LONG,LONG,BSTR*);

// IAccessibleHypertext (inherits IAccessibleText, slots 3-21): get_hyperlink 23, get_hyperlinkIndex 24.
static constexpr GUID HypertextId={0x6b4f8bbf,0xf1f2,0x418a,{0xb3,0x5e,0xa1,0x95,0xbc,0x41,0x03,0xb9}};
using HyperlinkCall=HRESULT(STDMETHODCALLTYPE*)(void*,LONG,IUnknown**);
using HyperlinkIndexCall=HRESULT(STDMETHODCALLTYPE*)(void*,LONG,LONG*);
using CharactersCall=HRESULT(STDMETHODCALLTYPE*)(void*,LONG*);

// Browser documents represent child objects as U+FFFC in their text; expand each placeholder in
// [start,end) with the child's text (its own selection when it has one), bounded in depth and size.
static void ExpandEmbedded(IUnknown* textObject,const std::wstring& raw,LONG start,std::wstring& out,size_t limit,int depth){
    ComPtr<IUnknown> hypertext;
    if(depth<8)textObject->QueryInterface(HypertextId,reinterpret_cast<void**>(hypertext.GetAddressOf()));
    for(size_t i=0;i<raw.size()&&out.size()<limit;++i){
        if(raw[i]!=0xFFFC||!hypertext){out.push_back(raw[i]);continue;}
        auto table=*reinterpret_cast<void***>(hypertext.Get());
        LONG link=-1;ComPtr<IUnknown> child;ComPtr<IUnknown> childText;
        if(FAILED(reinterpret_cast<HyperlinkIndexCall>(table[24])(hypertext.Get(),start+static_cast<LONG>(i),&link))||link<0)continue;
        if(FAILED(reinterpret_cast<HyperlinkCall>(table[23])(hypertext.Get(),link,child.GetAddressOf()))||!child)continue;
        if(FAILED(child->QueryInterface(TextId,reinterpret_cast<void**>(childText.GetAddressOf())))||!childText)continue;
        auto t=*reinterpret_cast<void***>(childText.Get());
        LONG from=0,to=0,selections=0,characters=0;
        if(FAILED(reinterpret_cast<CharactersCall>(t[17])(childText.Get(),&characters))||characters<=0)continue;
        to=characters;
        if(SUCCEEDED(reinterpret_cast<CountCall>(t[7])(childText.Get(),&selections))&&selections>0){
            LONG s=0,e=0;
            if(SUCCEEDED(reinterpret_cast<RangeCall>(t[9])(childText.Get(),0,&s,&e))&&s>=0&&e>s){from=s;to=e;}
        }
        BSTR value=nullptr;
        if(FAILED(reinterpret_cast<TextCall>(t[10])(childText.Get(),from,to,&value))||!value)continue;
        std::wstring nested(value,SysStringLen(value));
        SysFreeString(value);
        if(!out.empty()&&out.back()!=L'\n')out.push_back(L'\n');
        ExpandEmbedded(childText.Get(),nested,from,out,limit,depth+1);
    }
}

static HRESULT ReadText(void* object,wchar_t* output,unsigned int capacity,int* reason,int depthLimit=6){
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
            std::wstring raw(value,length);
            if(raw.find(static_cast<wchar_t>(0xFFFC))!=std::wstring::npos){
                std::wstring expanded;
                ExpandEmbedded(static_cast<IUnknown*>(object),raw,start,expanded,capacity-1,0);
                raw.swap(expanded);
            }
            if(wmemchr(raw.data(),0,raw.size())){SysFreeString(value);return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);}
            if(!selected.empty())selected+=L'\n';
            selected.append(raw);
            if(selected.size()>=capacity){SysFreeString(value);return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);}
        }
        SysFreeString(value);
    }
    if(count==0&&depthLimit>0){
        // Firefox keeps a document-wide selection on the child hypertext objects and reports
        // nSelections=0 on the focused document itself: descend into embedded children (bounded)
        // and collect only their real selection ranges.
        ComPtr<IUnknown> hypertext;
        if(SUCCEEDED(static_cast<IUnknown*>(object)->QueryInterface(HypertextId,reinterpret_cast<void**>(hypertext.GetAddressOf())))&&hypertext){
            auto h=*reinterpret_cast<void***>(hypertext.Get());
            LONG links=0;
            HRESULT linksHr=reinterpret_cast<CountCall>(h[22])(hypertext.Get(),&links);
            if(GetEnvironmentVariableW(L"SUSU_IA2_TRACE",nullptr,0)>0)fwprintf(stderr,L"descend depth=%d nHyperlinks hr=0x%08lx links=%ld\n",depthLimit,static_cast<unsigned long>(linksHr),links);
            if(SUCCEEDED(linksHr)&&links>0){
                for(LONG i=0;i<links&&i<256;++i){
                    ComPtr<IUnknown> child,childText;
                    if(FAILED(reinterpret_cast<HyperlinkCall>(h[23])(hypertext.Get(),i,child.GetAddressOf()))||!child)continue;
                    if(FAILED(child->QueryInterface(TextId,reinterpret_cast<void**>(childText.GetAddressOf())))||!childText)continue;
                    std::vector<wchar_t> part(capacity,0);
                    int partReason=3;
                    HRESULT partHr=ReadText(childText.Get(),part.data(),capacity,&partReason,depthLimit-1);
                    if(GetEnvironmentVariableW(L"SUSU_IA2_TRACE",nullptr,0)>0)fwprintf(stderr,L"  child %ld hr=0x%08lx reason=%d\n",i,static_cast<unsigned long>(partHr),partReason);
                    if(FAILED(partHr)||partReason!=0)continue;
                    const size_t n=wcslen(part.data());
                    if(selected.size()+n+1>=capacity)return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
                    if(!selected.empty())selected+=L'\n';
                    selected.append(part.data(),n);
                }
            }
        }
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
    const bool trace=GetEnvironmentVariableW(L"SUSU_IA2_TRACE",nullptr,0)>0; // diagnostics only (stderr)
    for(int depth=0;depth<32;++depth){
        VARIANT self{};self.vt=VT_I4;self.lVal=CHILDID_SELF;
        if(trace){BSTR name=nullptr;VARIANT role{};VARIANT me{};me.vt=VT_I4;me.lVal=CHILDID_SELF;accessible->get_accName(me,&name);accessible->get_accRole(me,&role);fwprintf(stderr,L"depth=%d role=%ld name=%.40s\n",depth,role.vt==VT_I4?role.lVal:-1,name?name:L"");SysFreeString(name);VariantClear(&role);}
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
        if(trace&&text){auto t=*reinterpret_cast<void***>(text.Get());LONG n=0,c=0;reinterpret_cast<CountCall>(t[7])(text.Get(),&n);reinterpret_cast<CountCall>(t[17])(text.Get(),&c);fwprintf(stderr,L"text object: nSelections=%ld nCharacters=%ld\n",n,c);}
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
        HRESULT hr=ReadText(&fake,text,64,&reason,0);
        if(mode==2||mode==3||mode==4||mode==5||mode==8){if(SUCCEEDED(hr)||text[0]!=0)return E_FAIL;}
        else if(mode==1||mode==7){if(FAILED(hr)||reason!=3||text[0]!=0||fake.calls!=(mode==1?0:1))return E_FAIL;}
        else if(FAILED(hr)||reason!=0||wcscmp(text,mode==6?L"first\nnext":L"first")!=0)return E_FAIL;
    }
    return S_OK;
}
