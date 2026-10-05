#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cwchar>
extern "C" {
#include "library.h"
#include "../User/protocol.h"
}

/* A C++ library in a process (P6.4.j3c), mode 27 of the host runtime fixture: the fixture loads host/cxxlib.dll
 * (tests/User.X64/cxx_library.cpp) from the boot package, whose startup prepares its own C++ runtime, C runtime and
 * STL before the load returns; the library and the fixture see one environment; and the unload destroys the library's
 * static objects newest first, each reporting to a hook in the fixture. */
namespace {
int exits[4];
int exit_count;

void on_exit(int id)
{
    if (exit_count < 4) {
        exits[exit_count] = id;
    }
    ++exit_count;
}

template <typename Function> Function symbol(WitU64 library, const char *name)
{
    WitU64 address = 0;
    WitU32 bytes = 0;
    while (name[bytes]) {
        ++bytes;
    }
    return wit_native_library_symbol(library, name, bytes, 0, &address) == WIT_STATUS_OK
        ? reinterpret_cast<Function>(address)
        : nullptr;
}
} // namespace

extern "C" WitU64 wit_cxx_library_probe()
{
    if (!SetEnvironmentVariableW(L"WITOS_FROM_MAIN", L"main")) {
        return 2810;
    }
    WitU64 library = 0;
    if (wit_native_library_load("/host/cxxlib.dll", 16, &library) != WIT_STATUS_OK) {
        return 2811;
    }
    const auto set_exit = symbol<void (*)(void (*)(int))>(library, "CxxLibrarySetExit");
    const auto probe = symbol<unsigned long long (*)()>(library, "CxxLibraryProbe");
    if (!set_exit || !probe) {
        return 2812;
    }
    set_exit(&on_exit);
    const unsigned long long code = probe();
    if (code) {
        return code;
    }
    wchar_t value[16];
    if (GetEnvironmentVariableW(L"WITOS_FROM_LIBRARY", value, 16) != 7 || std::wcscmp(value, L"library")) {
        return 2813; // the library's change is the process's
    }
    if (exit_count || wit_native_library_unload(library) != WIT_STATUS_OK) {
        return 2814;
    }
    if (exit_count != 3 || exits[0] != 3 || exits[1] != 2 || exits[2] != 1) {
        return 2815; // the function-local static first, then the globals in reverse order of construction
    }
    return WIT_TEST_EXIT_CODE;
}
