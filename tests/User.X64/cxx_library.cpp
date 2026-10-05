#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdio>
#include <cwchar>
#include <map>
#include <sstream>
#include <string>
#include <vector>

/* A C++ library in the guest (P6.4.j3c), linked as the .NET host's libraries are: its own copy of the WitOS C++
 * runtime, the C runtime and the STL, and the library startup as its entry point. The host runtime fixture's mode 27
 * loads it from the boot package and calls these exports. Its static objects are constructed before the load returns
 * and destroyed when it unloads, through the module's own atexit, newest first; each destructor reports to the hook the
 * fixture sets. */
namespace {
using ExitHook = void (*)(int);
ExitHook exit_hook;

struct Static {
    explicit Static(int id) : Id(id), Text(L"constructed") {}

    ~Static()
    {
        if (exit_hook) {
            exit_hook(Id);
        }
    }

    int Id;
    std::wstring Text;
};

Static first(1);
Static second(2);
thread_local int calls;

DWORD WINAPI worker(void *)
{
    return 0;
}
} // namespace

extern "C" __declspec(dllexport) void CxxLibrarySetExit(ExitHook hook)
{
    exit_hook = hook;
}

extern "C" __declspec(dllexport) unsigned long long CxxLibraryProbe()
{
    if (first.Text != L"constructed" || second.Id != 2 || ++calls != 1) {
        return 2801; // the static initializers and the loading thread's thread_local objects
    }
    // The STL on the module's own heap.
    std::map<std::wstring, std::vector<int>> table;
    for (int i = 0; i < 64; ++i) {
        table[std::to_wstring(i % 8)].push_back(i);
    }
    if (table.size() != 8 || table[L"3"].size() != 8 || table[L"7"].back() != 63) {
        return 2802;
    }
    wchar_t text[32];
    if (swprintf(text, 32, L"%ls=%d", L"sum", 63) != 6 || std::wcscmp(text, L"sum=63")) {
        return 2803; // the module's own C runtime formats
    }
    std::wostringstream stream;
    stream << L"sum=" << 63;
    if (stream.str() != text) {
        return 2807; // the module's own locale objects, which its initializers created
    }
    // The process's environment, which the fixture set through its own copy of the adapter.
    wchar_t value[16];
    if (GetEnvironmentVariableW(L"WITOS_FROM_MAIN", value, 16) != 4 ||
        std::wcscmp(value, L"main") ||
        !SetEnvironmentVariableW(L"WITOS_FROM_LIBRARY", L"library")) {
        return 2804;
    }
    // A function-local static, whose destructor the module's atexit registers on first use: it is destroyed first.
    static Static late(3);
    if (late.Id != 3) {
        return 2805;
    }
    // A library starts no thread yet: a thread's lifecycle belongs to the process entry.
    if (CreateThread(nullptr, 0, &worker, nullptr, 0, nullptr) || GetLastError() != ERROR_INVALID_ADDRESS) {
        return 2808;
    }
    // The process console through the module's C runtime.
    if (std::fputs("[CXX-LIBRARY] ready\n", stdout) < 0 || std::fflush(stdout)) {
        return 2806;
    }
    return 0;
}
