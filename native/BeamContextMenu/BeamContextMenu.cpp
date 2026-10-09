// "Send with Beam" in the Windows 11 right-click menu (the top level, not "Show more options").
//
// Windows 11 only shows IExplorerCommand handlers from packaged apps there, so this DLL ships in the MSIX
// (Store) package and is registered in AppxManifest.xml (windows.fileExplorerContextMenus + a COM surrogate
// server). Invoking it starts Beam.exe from the same folder with the selected files:
//   Beam.exe --send "a" "b" ...            (short selections)
//   Beam.exe --send-list <temp file>       (long selections, one UTF-8 path per line)
// The installer version keeps its classic registry entry (under "Show more options").

#include <windows.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <shlwapi.h>
#include <new>
#include <string>
#include <vector>

// {6A7C1D2E-8B3F-4E59-9C41-B3A0D5E7F214} — must match AppxManifest.xml.
static const CLSID CLSID_BeamSendCommand = {0x6a7c1d2e, 0x8b3f, 0x4e59, {0x9c, 0x41, 0xb3, 0xa0, 0xd5, 0xe7, 0xf2, 0x14}};

static HMODULE g_module = nullptr;
static LONG g_objects = 0;

static std::wstring ModuleFolder()
{
    wchar_t path[MAX_PATH * 4];
    DWORD length = GetModuleFileNameW(g_module, path, ARRAYSIZE(path));
    if (length == 0 || length >= ARRAYSIZE(path)) return L"";
    std::wstring folder(path, length);
    size_t slash = folder.find_last_of(L'\\');
    return slash == std::wstring::npos ? L"" : folder.substr(0, slash);
}

// The menu text in the Windows display language (the app's own language setting isn't visible to Explorer).
static const wchar_t* Title()
{
    switch (PRIMARYLANGID(GetUserDefaultUILanguage()))
    {
        case LANG_TURKISH: return L"Beam ile gönder";
        case LANG_RUSSIAN: return L"Отправить через Beam";
        case LANG_UZBEK: return L"Beam orqali yuborish";
        default: return L"Send with Beam";
    }
}

static std::wstring Quote(const std::wstring& arg)
{
    // Paths can't contain quotes; a trailing backslash (e.g. "C:\") must be doubled before the closing quote.
    std::wstring quoted = L"\"" + arg;
    if (!arg.empty() && arg.back() == L'\\') quoted += L'\\';
    return quoted + L"\"";
}

static bool WriteListFile(const std::vector<std::wstring>& paths, std::wstring& file)
{
    wchar_t folder[MAX_PATH + 1];
    if (GetTempPathW(ARRAYSIZE(folder), folder) == 0) return false;
    wchar_t name[MAX_PATH + 1];
    if (GetTempFileNameW(folder, L"bms", 0, name) == 0) return false;
    std::string utf8;
    for (const auto& path : paths)
    {
        int size = WideCharToMultiByte(CP_UTF8, 0, path.c_str(), (int)path.size(), nullptr, 0, nullptr, nullptr);
        std::string line(size, '\0');
        WideCharToMultiByte(CP_UTF8, 0, path.c_str(), (int)path.size(), line.data(), size, nullptr, nullptr);
        utf8 += line;
        utf8 += '\n';
    }
    HANDLE handle = CreateFileW(name, GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_TEMPORARY, nullptr);
    if (handle == INVALID_HANDLE_VALUE) return false;
    DWORD written = 0;
    BOOL ok = WriteFile(handle, utf8.data(), (DWORD)utf8.size(), &written, nullptr) && written == utf8.size();
    CloseHandle(handle);
    if (!ok) return false;
    file = name;
    return true;
}

static HRESULT LaunchBeam(const std::vector<std::wstring>& paths)
{
    std::wstring exe = ModuleFolder() + L"\\Beam.exe";
    std::wstring command = Quote(exe) + L" --send";
    for (const auto& path : paths) command += L" " + Quote(path);
    if (command.size() > 30000)
    {
        std::wstring list;
        if (!WriteListFile(paths, list)) return E_FAIL;
        command = Quote(exe) + L" --send-list " + Quote(list);
    }

    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process = {};
    std::vector<wchar_t> buffer(command.begin(), command.end());
    buffer.push_back(L'\0');
    if (!CreateProcessW(exe.c_str(), buffer.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &startup, &process))
        return HRESULT_FROM_WIN32(GetLastError());
    AllowSetForegroundWindow(process.dwProcessId);
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return S_OK;
}

class SendCommand final : public IExplorerCommand
{
public:
    SendCommand() { InterlockedIncrement(&g_objects); }

    // IUnknown
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        static const QITAB table[] = {QITABENT(SendCommand, IExplorerCommand), {}};
        return QISearch(this, table, riid, ppv);
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&_refs); }
    IFACEMETHODIMP_(ULONG) Release() override
    {
        LONG refs = InterlockedDecrement(&_refs);
        if (refs == 0) delete this;
        return refs;
    }

    // IExplorerCommand
    IFACEMETHODIMP GetTitle(IShellItemArray*, LPWSTR* name) override { return SHStrDupW(Title(), name); }
    IFACEMETHODIMP GetIcon(IShellItemArray*, LPWSTR* icon) override
    {
        std::wstring resource = ModuleFolder() + L"\\Beam.exe,0";
        return SHStrDupW(resource.c_str(), icon);
    }
    IFACEMETHODIMP GetToolTip(IShellItemArray*, LPWSTR* tip) override { *tip = nullptr; return E_NOTIMPL; }
    IFACEMETHODIMP GetCanonicalName(GUID* guid) override { *guid = CLSID_BeamSendCommand; return S_OK; }
    IFACEMETHODIMP GetState(IShellItemArray*, BOOL, EXPCMDSTATE* state) override { *state = ECS_ENABLED; return S_OK; }
    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override { *flags = ECF_DEFAULT; return S_OK; }
    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** commands) override { *commands = nullptr; return E_NOTIMPL; }

    IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx*) override
    {
        if (!items) return S_OK;
        DWORD count = 0;
        HRESULT hr = items->GetCount(&count);
        if (FAILED(hr)) return hr;
        std::vector<std::wstring> paths;
        for (DWORD i = 0; i < count; i++)
        {
            IShellItem* item = nullptr;
            if (FAILED(items->GetItemAt(i, &item))) continue;
            LPWSTR path = nullptr;
            // Only real files and folders (not e.g. items inside a zip or on a phone).
            if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &path)) && path)
            {
                paths.emplace_back(path);
                CoTaskMemFree(path);
            }
            item->Release();
        }
        return paths.empty() ? S_OK : LaunchBeam(paths);
    }

private:
    ~SendCommand() { InterlockedDecrement(&g_objects); }
    LONG _refs = 1;
};

class Factory final : public IClassFactory
{
public:
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        static const QITAB table[] = {QITABENT(Factory, IClassFactory), {}};
        return QISearch(this, table, riid, ppv);
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return 2; }   // static instance
    IFACEMETHODIMP_(ULONG) Release() override { return 1; }

    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
    {
        *ppv = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        auto* command = new (std::nothrow) SendCommand();
        if (!command) return E_OUTOFMEMORY;
        HRESULT hr = command->QueryInterface(riid, ppv);
        command->Release();
        return hr;
    }
    IFACEMETHODIMP LockServer(BOOL lock) override
    {
        if (lock) InterlockedIncrement(&g_objects);
        else InterlockedDecrement(&g_objects);
        return S_OK;
    }
};

static Factory g_factory;

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, void** ppv)
{
    *ppv = nullptr;
    if (clsid != CLSID_BeamSendCommand) return CLASS_E_CLASSNOTAVAILABLE;
    return g_factory.QueryInterface(riid, ppv);
}

STDAPI DllCanUnloadNow() { return g_objects == 0 ? S_OK : S_FALSE; }
