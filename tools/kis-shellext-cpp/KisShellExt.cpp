// KisShellExt.cpp — Windows 11 modern context menu (IExplorerCommand) for KIS.
//
// Top-level "KIS" entry on every file/folder/.kis archive, with a submenu:
//   Compress to .kis
//   Compress to .kis (encrypted)…
//   Extract here          (only on .kis/.kes selections)
//   Verify integrity
//   Show info
//
// Each verb shells out to kis.exe (sibling of this DLL).

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shobjidl_core.h>
#include <shlwapi.h>
#include <wrl/module.h>
#include <wrl/implements.h>
#include <wrl/client.h>
#include <string>
#include <vector>
#include <wchar.h>

#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "runtimeobject.lib")

using namespace Microsoft::WRL;

// ─── helpers ────────────────────────────────────────────────────────────
static std::wstring DllDir()
{
    HMODULE hm = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                       GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                       reinterpret_cast<LPCWSTR>(&DllDir), &hm);
    wchar_t buf[MAX_PATH] = L"";
    GetModuleFileNameW(hm, buf, MAX_PATH);
    PathRemoveFileSpecW(buf);
    return buf;
}

static void Quote(std::wstring& s) { s.insert(s.begin(), L'"'); s.push_back(L'"'); }

static void RunDetached(const std::wstring& cmdLine, const std::wstring& workDir)
{
    STARTUPINFOW si{};        si.cb = sizeof(si);
    PROCESS_INFORMATION pi{};
    std::wstring cl = cmdLine;        // CreateProcess wants writable buffer
    if (CreateProcessW(nullptr, cl.data(), nullptr, nullptr, FALSE,
                       CREATE_NEW_CONSOLE, nullptr,
                       workDir.empty() ? nullptr : workDir.c_str(), &si, &pi))
    {
        CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
    }
}

static std::vector<std::wstring> GetSelection(IShellItemArray* items)
{
    std::vector<std::wstring> out;
    if (!items) return out;
    DWORD n = 0; if (FAILED(items->GetCount(&n))) return out;
    for (DWORD i = 0; i < n; i++)
    {
        ComPtr<IShellItem> it;
        if (SUCCEEDED(items->GetItemAt(i, &it)))
        {
            PWSTR p = nullptr;
            if (SUCCEEDED(it->GetDisplayName(SIGDN_FILESYSPATH, &p)) && p)
            {
                out.emplace_back(p);
                CoTaskMemFree(p);
            }
        }
    }
    return out;
}

static bool EndsWithI(const std::wstring& s, const wchar_t* suffix)
{
    size_t n = wcslen(suffix);
    if (s.size() < n) return false;
    return _wcsicmp(s.c_str() + s.size() - n, suffix) == 0;
}

static bool AllAreKisArchives(const std::vector<std::wstring>& v)
{
    if (v.empty()) return false;
    for (auto& p : v) if (!EndsWithI(p, L".kis") && !EndsWithI(p, L".kes")) return false;
    return true;
}

// ─── Base IExplorerCommand ─────────────────────────────────────────────
class CommandBase : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IExplorerCommand>
{
public:
    virtual PCWSTR Title() const = 0;
    virtual PCWSTR IconRef() const { return nullptr; } // "path,-N" or full path; null = kis.exe
    virtual void Run(const std::vector<std::wstring>& paths) = 0;
    virtual EXPCMDSTATE StateFor(const std::vector<std::wstring>&) { return ECS_ENABLED; }
    virtual EXPCMDFLAGS Flags() const { return ECF_DEFAULT; }

    IFACEMETHODIMP GetTitle(IShellItemArray*, PWSTR* out) override
    { return SHStrDupW(Title(), out); }

    IFACEMETHODIMP GetIcon(IShellItemArray*, PWSTR* out) override
    {
        PCWSTR ic = IconRef();
        if (ic && *ic) return SHStrDupW(ic, out);
        std::wstring p = DllDir() + L"\\kis.exe";
        return SHStrDupW(p.c_str(), out);
    }

    IFACEMETHODIMP GetToolTip(IShellItemArray*, PWSTR* out) override
    { *out = nullptr; return E_NOTIMPL; }

    IFACEMETHODIMP GetCanonicalName(GUID* g) override { *g = GUID_NULL; return S_OK; }

    IFACEMETHODIMP GetState(IShellItemArray* items, BOOL, EXPCMDSTATE* st) override
    { *st = StateFor(GetSelection(items)); return S_OK; }

    IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx*) override
    {
        Run(GetSelection(items));
        return S_OK;
    }

    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* f) override { *f = Flags(); return S_OK; }

    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** e) override
    { *e = nullptr; return E_NOTIMPL; }
};

// ─── Verb runners ──────────────────────────────────────────────────────
static bool IsDirectory(const std::wstring& p)
{
    DWORD a = GetFileAttributesW(p.c_str());
    return a != INVALID_FILE_ATTRIBUTES && (a & FILE_ATTRIBUTE_DIRECTORY);
}

static std::wstring DefaultArchiveName(const std::wstring& src)
{
    std::wstring out = src;
    if (IsDirectory(src))
    {
        while (!out.empty() && (out.back() == L'\\' || out.back() == L'/')) out.pop_back();
        out += L".kis";
    }
    else
    {
        size_t dot = out.find_last_of(L'.');
        size_t sl  = out.find_last_of(L"\\/");
        if (dot != std::wstring::npos && (sl == std::wstring::npos || dot > sl)) out = out.substr(0, dot);
        out += L".kis";
    }
    return out;
}

// Run kis.exe with the given arg suffix in a console window that pauses.
// `kisArgs` already contains all kis.exe args (no leading space, no exe path).
static void RunKisInConsole(const std::wstring& kisArgs)
{
    std::wstring kis = DllDir() + L"\\kis.exe";
    std::wstring qKis = kis; Quote(qKis);
    // cmd.exe /s /c "<everything between first and last quote is the command>"
    std::wstring full = L"cmd.exe /s /c \" " + qKis + L" " + kisArgs + L" & echo. & pause \"";
    RunDetached(full, DllDir());
}

static void RunCompress(const std::wstring& src)
{
    std::wstring out = DefaultArchiveName(src);
    std::wstring qOut = out; Quote(qOut);
    std::wstring qSrc = src; Quote(qSrc);
    RunKisInConsole(L"create " + qOut + L" " + qSrc + L" -c lzma");
}

static void RunCompressEncrypted(const std::wstring& src)
{
    std::wstring out = DefaultArchiveName(src);
    std::wstring kis = DllDir() + L"\\kis.exe";
    // PowerShell prompts for a password securely, then invokes kis.exe with -p.
    std::wstring ps =
        L"$ErrorActionPreference='Stop';"
        L"$s=Read-Host 'Password' -AsSecureString;"
        L"$b=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($s);"
        L"$pw=[Runtime.InteropServices.Marshal]::PtrToStringBSTR($b);"
        L"& '" + kis + L"' create '" + out + L"' '" + src + L"' -c lzma -p $pw;"
        L"Write-Host '';Read-Host 'Press Enter to close'";
    std::wstring full = L"powershell.exe -NoProfile -ExecutionPolicy Bypass -Command \"" + ps + L"\"";
    RunDetached(full, DllDir());
}

static void RunExtract(const std::wstring& archive)
{
    // Extract into a sibling folder named after the archive (without .kis/.kes).
    std::wstring dest = archive;
    size_t dot = dest.find_last_of(L'.');
    size_t sl  = dest.find_last_of(L"\\/");
    if (dot != std::wstring::npos && (sl == std::wstring::npos || dot > sl)) dest = dest.substr(0, dot);
    std::wstring qArc = archive; Quote(qArc);
    std::wstring qDst = dest;    Quote(qDst);
    RunKisInConsole(L"extract " + qArc + L" -o " + qDst);
}

static void RunVerify(const std::wstring& archive)
{
    std::wstring q = archive; Quote(q);
    RunKisInConsole(L"verify " + q);
}

static void RunInfo(const std::wstring& archive)
{
    std::wstring q = archive; Quote(q);
    RunKisInConsole(L"info " + q);
}

// ─── Sub-commands ──────────────────────────────────────────────────────
class CompressCmd : public CommandBase
{
public:
    PCWSTR Title() const override { return L"Compress to .kis"; }
    PCWSTR IconRef() const override { return L"%SystemRoot%\\System32\\imageres.dll,-174"; }
    EXPCMDSTATE StateFor(const std::vector<std::wstring>& v) override { return v.empty() ? ECS_HIDDEN : ECS_ENABLED; }
    void Run(const std::vector<std::wstring>& v) override { for (auto& p : v) RunCompress(p); }
};

class CompressEncCmd : public CommandBase
{
public:
    PCWSTR Title() const override { return L"Compress to .kis (encrypted)\u2026"; }
    PCWSTR IconRef() const override { return L"%SystemRoot%\\System32\\imageres.dll,-105"; }
    EXPCMDSTATE StateFor(const std::vector<std::wstring>& v) override { return v.empty() ? ECS_HIDDEN : ECS_ENABLED; }
    void Run(const std::vector<std::wstring>& v) override { for (auto& p : v) RunCompressEncrypted(p); }
};

class ExtractCmd : public CommandBase
{
public:
    PCWSTR Title() const override { return L"Extract here"; }
    PCWSTR IconRef() const override { return L"%SystemRoot%\\System32\\imageres.dll,-204"; }
    EXPCMDSTATE StateFor(const std::vector<std::wstring>& v) override { return AllAreKisArchives(v) ? ECS_ENABLED : ECS_HIDDEN; }
    void Run(const std::vector<std::wstring>& v) override { for (auto& p : v) RunExtract(p); }
};

class VerifyCmd : public CommandBase
{
public:
    PCWSTR Title() const override { return L"Verify integrity"; }
    PCWSTR IconRef() const override { return L"%SystemRoot%\\System32\\imageres.dll,-101"; }
    EXPCMDSTATE StateFor(const std::vector<std::wstring>& v) override { return AllAreKisArchives(v) ? ECS_ENABLED : ECS_HIDDEN; }
    void Run(const std::vector<std::wstring>& v) override { for (auto& p : v) RunVerify(p); }
};

class InfoCmd : public CommandBase
{
public:
    PCWSTR Title() const override { return L"Show info"; }
    PCWSTR IconRef() const override { return L"%SystemRoot%\\System32\\imageres.dll,-76"; }
    EXPCMDSTATE StateFor(const std::vector<std::wstring>& v) override { return AllAreKisArchives(v) ? ECS_ENABLED : ECS_HIDDEN; }
    void Run(const std::vector<std::wstring>& v) override { for (auto& p : v) RunInfo(p); }
};

// ─── Sub-command enumerator ────────────────────────────────────────────
class SubEnum : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IEnumExplorerCommand>
{
    std::vector<ComPtr<IExplorerCommand>> _items;
    size_t _i = 0;
public:
    void Add(ComPtr<IExplorerCommand> p) { _items.push_back(p); }

    IFACEMETHODIMP Next(ULONG celt, IExplorerCommand** out, ULONG* fetched) override
    {
        ULONG f = 0;
        for (ULONG k = 0; k < celt && _i < _items.size(); k++, _i++, f++)
            _items[_i].CopyTo(&out[k]);
        if (fetched) *fetched = f;
        return f == celt ? S_OK : S_FALSE;
    }
    IFACEMETHODIMP Skip(ULONG c) override { _i += c; return S_OK; }
    IFACEMETHODIMP Reset() override { _i = 0; return S_OK; }
    IFACEMETHODIMP Clone(IEnumExplorerCommand** out) override
    {
        auto e = Make<SubEnum>();
        e->_items = _items;
        return e.CopyTo(out);
    }
};

// ─── Top-level "KIS" entry hosting the submenu ─────────────────────────
class KisRoot : public CommandBase
{
public:
    PCWSTR Title() const override { return L"KIS"; }
    PCWSTR IconRef() const override
    {
        // Static buffer is fine: SHStrDupW copies it.
        static wchar_t s_icon[MAX_PATH] = L"";
        if (!s_icon[0])
        {
            std::wstring p = DllDir() + L"\\KIS.ico";
            wcsncpy_s(s_icon, p.c_str(), _TRUNCATE);
        }
        return s_icon;
    }
    EXPCMDFLAGS Flags() const override { return ECF_HASSUBCOMMANDS; }
    EXPCMDSTATE StateFor(const std::vector<std::wstring>&) override { return ECS_ENABLED; }
    void Run(const std::vector<std::wstring>&) override {}

    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** out) override
    {
        auto e = Make<SubEnum>();
        e->Add(Make<CompressCmd>());
        e->Add(Make<CompressEncCmd>());
        e->Add(Make<ExtractCmd>());
        e->Add(Make<VerifyCmd>());
        e->Add(Make<InfoCmd>());
        return e.CopyTo(out);
    }
};

// ─── COM registration ──────────────────────────────────────────────────
// Top-level CLSID = {3D1B7E11-1234-4000-9C0A-2A0B0E1F0000}
class DECLSPEC_UUID("3D1B7E11-1234-4000-9C0A-2A0B0E1F0000") KisRootClass : public KisRoot {};

CoCreatableClass(KisRootClass);

STDAPI DllGetActivationFactory(_In_ HSTRING activatableClassId, _COM_Outptr_ IActivationFactory** factory)
{ return Module<ModuleType::InProc>::GetModule().GetActivationFactory(activatableClassId, factory); }

STDAPI DllCanUnloadNow()
{ return Module<InProc>::GetModule().GetObjectCount() == 0 ? S_OK : S_FALSE; }

STDAPI DllGetClassObject(_In_ REFCLSID rclsid, _In_ REFIID riid, _Outptr_ LPVOID* ppv)
{ return Module<InProc>::GetModule().GetClassObject(rclsid, riid, ppv); }

BOOL WINAPI DllMain(HINSTANCE h, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(h);
    return TRUE;
}
