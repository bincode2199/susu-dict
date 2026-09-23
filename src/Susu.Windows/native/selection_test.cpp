// F00 SEL01 harness-only code (never used by the product helper): creates and reads selections in
// another program without keyboard input, for sessions where the desktop is not interactive.
// The product path is focus-based (selection.cpp / ia2.cpp); this searches providers in a window.
#include <windows.h>
#include <objbase.h>
#include <oleauto.h>
#include <oleacc.h>
#include <servprov.h>
#include <UIAutomation.h>
#include <wrl.h>
#include <string>
#include <vector>
#include <deque>

using Microsoft::WRL::ComPtr;

namespace {
static constexpr GUID TextId = {0x24fd2ffb, 0x3aad, 0x4a08, {0x83, 0x35, 0xa3, 0xad, 0x89, 0xc0, 0xfb, 0x4b}};
// IAccessibleText vtable slots (IUnknown 0-2): addSelection 3, nSelections 7, selection 9, text 10,
// setSelection 16, nCharacters 17. Same ABI as ia2.cpp.
using AddSelectionCall = HRESULT(STDMETHODCALLTYPE*)(void*, LONG, LONG);
using CountCall = HRESULT(STDMETHODCALLTYPE*)(void*, LONG*);
using RangeCall = HRESULT(STDMETHODCALLTYPE*)(void*, LONG, LONG*, LONG*);
using TextCall = HRESULT(STDMETHODCALLTYPE*)(void*, LONG, LONG, BSTR*);
using SetSelectionCall = HRESULT(STDMETHODCALLTYPE*)(void*, LONG, LONG, LONG);

struct Mta { HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED); ~Mta() { if (SUCCEEDED(hr)) CoUninitialize(); } };

HRESULT UiaTextElements(HWND top, ComPtr<IUIAutomation>& automation, ComPtr<IUIAutomationElementArray>& found) {
    HRESULT hr = CoCreateInstance(CLSID_CUIAutomation8, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&automation));
    if (FAILED(hr)) return hr;
    ComPtr<IUIAutomation2> automation2;
    if (SUCCEEDED(automation.As(&automation2))) { automation2->put_ConnectionTimeout(2000); automation2->put_TransactionTimeout(5000); }
    ComPtr<IUIAutomationElement> root;
    hr = automation->ElementFromHandle(top, &root);
    if (FAILED(hr)) return hr;
    VARIANT yes{}; yes.vt = VT_BOOL; yes.boolVal = VARIANT_TRUE;
    ComPtr<IUIAutomationCondition> condition;
    hr = automation->CreatePropertyCondition(UIA_IsTextPatternAvailablePropertyId, yes, &condition);
    if (FAILED(hr)) return hr;
    return root->FindAll(TreeScope_Subtree, condition.Get(), &found);
}

// Breadth-first MSAA walk (bounded) collecting objects that expose IAccessibleText.
void Ia2TextObjects(HWND top, std::vector<ComPtr<IUnknown>>& out) {
    ComPtr<IAccessible> root;
    if (FAILED(AccessibleObjectFromWindow(top, static_cast<DWORD>(OBJID_CLIENT), IID_IAccessible, reinterpret_cast<void**>(root.GetAddressOf())))) return;
    std::deque<ComPtr<IAccessible>> queue{root};
    int visited = 0;
    while (!queue.empty() && visited < 3000) {
        ComPtr<IAccessible> node = queue.front(); queue.pop_front(); ++visited;
        ComPtr<IServiceProvider> provider;
        if (SUCCEEDED(node.As(&provider))) {
            ComPtr<IUnknown> text;
            if (SUCCEEDED(provider->QueryService(IID_IAccessible, TextId, reinterpret_cast<void**>(text.GetAddressOf()))) && text) out.push_back(text);
        }
        LONG count = 0;
        if (FAILED(node->get_accChildCount(&count)) || count <= 0 || count > 500) continue;
        std::vector<VARIANT> children(static_cast<size_t>(count));
        LONG obtained = 0;
        if (FAILED(AccessibleChildren(node.Get(), 0, count, children.data(), &obtained))) continue;
        for (LONG i = 0; i < obtained; ++i) {
            if (children[i].vt == VT_DISPATCH && children[i].pdispVal) {
                ComPtr<IAccessible> child;
                if (SUCCEEDED(children[i].pdispVal->QueryInterface(IID_PPV_ARGS(&child)))) queue.push_back(child);
            }
            VariantClear(&children[i]);
        }
    }
}
}

// Selects the paragraph containing marker. method: 1 UIA, 2 IA2, 0 not found.
// providers: number of UIA TextPattern elements found (or IA2 text objects when method 2).
extern "C" __declspec(dllexport) HRESULT susu_test_select_marker(HWND top, const wchar_t* marker, int* method, int* providers) {
    if (!top || !marker || !method || !providers) return E_INVALIDARG;
    *method = 0; *providers = 0;
    Mta mta;
    if (FAILED(mta.hr)) return mta.hr;
    ComPtr<IUIAutomation> automation;
    ComPtr<IUIAutomationElementArray> found;
    if (SUCCEEDED(UiaTextElements(top, automation, found)) && found) {
        int length = 0; found->get_Length(&length);
        *providers = length;
        for (int i = 0; i < length; ++i) {
            ComPtr<IUIAutomationElement> element;
            ComPtr<IUIAutomationTextPattern> pattern;
            ComPtr<IUIAutomationTextRange> document, range;
            if (FAILED(found->GetElement(i, &element)) || FAILED(element->GetCurrentPatternAs(UIA_TextPatternId, IID_PPV_ARGS(&pattern))) || !pattern) continue;
            if (FAILED(pattern->get_DocumentRange(&document)) || !document) continue;
            BSTR text = SysAllocString(marker);
            HRESULT hr = document->FindText(text, FALSE, FALSE, &range);
            SysFreeString(text);
            if (FAILED(hr) || !range) continue;
            element->SetFocus();
            range->ExpandToEnclosingUnit(TextUnit_Paragraph);
            if (SUCCEEDED(range->Select())) { *method = 1; return S_OK; }
        }
    }
    std::vector<ComPtr<IUnknown>> texts;
    Ia2TextObjects(top, texts);
    *providers = static_cast<int>(texts.size());
    for (auto& object : texts) {
        auto table = *reinterpret_cast<void***>(object.Get());
        LONG characters = 0;
        if (FAILED(reinterpret_cast<CountCall>(table[17])(object.Get(), &characters)) || characters <= 0 || characters > 100000) continue;
        BSTR value = nullptr;
        if (FAILED(reinterpret_cast<TextCall>(table[10])(object.Get(), 0, characters, &value)) || !value) continue;
        std::wstring content(value, SysStringLen(value));
        SysFreeString(value);
        size_t at = content.find(marker);
        if (at == std::wstring::npos) continue;
        size_t end = content.find_first_of(L"\n\r\xFFFC", at);
        if (end == std::wstring::npos) end = content.size();
        HRESULT hr = reinterpret_cast<SetSelectionCall>(table[16])(object.Get(), 0, static_cast<LONG>(at), static_cast<LONG>(end));
        if (FAILED(hr)) hr = reinterpret_cast<AddSelectionCall>(table[3])(object.Get(), static_cast<LONG>(at), static_cast<LONG>(end));
        if (SUCCEEDED(hr)) { *method = 2; return S_OK; }
    }
    return S_OK;
}

// Reads the first non-empty selection in the window. source: 1 UIA, 2 IA2, 0 none.
// Also reports whether any UIA element in the window is flagged IsPassword (provider capability).
extern "C" __declspec(dllexport) HRESULT susu_test_read_window(HWND top, int forceIa2, wchar_t* text, unsigned int capacity, int* source, int* passwordElements) {
    if (!top || !text || capacity < 2 || !source || !passwordElements) return E_INVALIDARG;
    text[0] = 0; *source = 0; *passwordElements = 0;
    Mta mta;
    if (FAILED(mta.hr)) return mta.hr;
    if (!forceIa2) {
        ComPtr<IUIAutomation> automation;
        ComPtr<IUIAutomationElementArray> found;
        if (SUCCEEDED(UiaTextElements(top, automation, found)) && found) {
            int length = 0; found->get_Length(&length);
            for (int i = 0; i < length && !*source; ++i) {
                ComPtr<IUIAutomationElement> element;
                ComPtr<IUIAutomationTextPattern> pattern;
                ComPtr<IUIAutomationTextRangeArray> ranges;
                if (FAILED(found->GetElement(i, &element)) || FAILED(element->GetCurrentPatternAs(UIA_TextPatternId, IID_PPV_ARGS(&pattern))) || !pattern) continue;
                if (FAILED(pattern->GetSelection(&ranges)) || !ranges) continue;
                int count = 0; ranges->get_Length(&count);
                for (int r = 0; r < count && r < 32; ++r) {
                    ComPtr<IUIAutomationTextRange> range;
                    BSTR value = nullptr;
                    if (FAILED(ranges->GetElement(r, &range)) || FAILED(range->GetText(static_cast<int>(capacity - 1), &value))) continue;
                    UINT n = SysStringLen(value);
                    if (n && n < capacity) { memcpy(text, value, n * sizeof(wchar_t)); text[n] = 0; *source = 1; }
                    SysFreeString(value);
                    if (*source) break;
                }
            }
            ComPtr<IUIAutomationElement> root;
            VARIANT yes{}; yes.vt = VT_BOOL; yes.boolVal = VARIANT_TRUE;
            ComPtr<IUIAutomationCondition> condition;
            ComPtr<IUIAutomationElementArray> passwords;
            if (SUCCEEDED(automation->ElementFromHandle(top, &root)) && SUCCEEDED(automation->CreatePropertyCondition(UIA_IsPasswordPropertyId, yes, &condition)) &&
                SUCCEEDED(root->FindAll(TreeScope_Subtree, condition.Get(), &passwords)) && passwords) passwords->get_Length(passwordElements);
            if (*source) return S_OK;
        }
    }
    std::vector<ComPtr<IUnknown>> texts;
    Ia2TextObjects(top, texts);
    for (auto& object : texts) {
        auto table = *reinterpret_cast<void***>(object.Get());
        LONG count = 0;
        if (FAILED(reinterpret_cast<CountCall>(table[7])(object.Get(), &count)) || count <= 0 || count > 32) continue;
        LONG start = 0, end = 0;
        if (FAILED(reinterpret_cast<RangeCall>(table[9])(object.Get(), 0, &start, &end)) || start < 0 || end <= start || static_cast<unsigned>(end - start) >= capacity) continue;
        BSTR value = nullptr;
        if (FAILED(reinterpret_cast<TextCall>(table[10])(object.Get(), start, end, &value)) || !value) continue;
        UINT n = SysStringLen(value);
        if (n && n < capacity) { memcpy(text, value, n * sizeof(wchar_t)); text[n] = 0; *source = 2; }
        SysFreeString(value);
        if (*source) return S_OK;
    }
    return S_OK;
}
