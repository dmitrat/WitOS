#include <string.h>
#include <system_error>
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
extern "C" {
#include "bootstrap.h"
#include "../User/protocol.h"
}
#include "stl_trace.h"

/* The guest half of P6.4.i1: tests/User.X64/stl_scenarios.cpp on the separately compiled sources of the pinned
 * microsoft/STL, the WitOS C++ runtime and the guest must print the trace msvcp140 prints on Windows, WIT_STL_TRACE,
 * which the tool generates. The trace grows in the report page, so the kernel can print how far a failed run got.
 * Windows error messages come from the guest's own catalogue, which the last checks show, together with the contracts
 * of the guest's FormatMessageA and GetLocaleInfoEx. The report's first word holds the processor level the vectorized
 * algorithms run at. */
extern "C" void stl_scenarios_run();
extern "C" void wit_cxx_initialize_isa(void);
extern "C" int __isa_available;

namespace {
constexpr unsigned TRACE_OFFSET = 0x100, TRACE_CAPACITY = 0xE00;

char line[2048];
unsigned used;
} // namespace

extern "C" void stl_trace(const char *text)
{
    if (used && used < sizeof(line) - 1) {
        line[used++] = ' ';
    }
    while (*text && used < sizeof(line) - 1) {
        line[used++] = *text++;
    }
    line[used] = 0;
    volatile char *report = (volatile char *)(WIT_GC_INFO_REPORT + TRACE_OFFSET);
    for (unsigned i = 0; i <= used && i < TRACE_CAPACITY; ++i) {
        report[i] = line[i];
    }
}

extern "C" WitU64 wit_stl_scenarios_probe()
{
    wit_cxx_initialize_isa(); // as a component's startup does before the vectorized algorithms run
    *(volatile WitU64 *)WIT_GC_INFO_REPORT = (WitU64)__isa_available; // the level the kernel reports
    stl_scenarios_run();
    const char *expected = WIT_STL_TRACE;
    unsigned same = 0;
    while (expected[same] && expected[same] == line[same]) {
        ++same;
    }
    if (expected[same] || same != used) {
        return 4000 + (same < 1999 ? same : 1999);
    }
    // FormatMessageA with an allocated buffer and LocalFree; an unknown code falls back to the STL's own text.
    if (std::system_category().message(5) != "Access to the requested resource was denied." ||
        std::system_category().message(12345) != "unknown error") {
        return 6001;
    }
    // A caller's buffer of the exact size keeps the previous error; one character less, a missing buffer and a request
    // the wide form rejects first change nothing else.
    const DWORD flags = FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS;
    static const char denied[] = "Access to the requested resource was denied.";
    char text[sizeof(denied)];
    SetLastError(77);
    if (FormatMessageA(flags, nullptr, ERROR_ACCESS_DENIED, 0, text, sizeof(text), nullptr) != sizeof(denied) - 1 ||
        memcmp(text, denied, sizeof(denied)) != 0 ||
        GetLastError() != 77) {
        return 6002;
    }
    text[0] = 'x';
    if (FormatMessageA(flags, nullptr, ERROR_ACCESS_DENIED, 0, text, sizeof(text) - 1, nullptr) ||
        GetLastError() != ERROR_INSUFFICIENT_BUFFER ||
        text[0] != 'x') {
        return 6003;
    }
    if (FormatMessageA(flags, nullptr, ERROR_ACCESS_DENIED, 0, nullptr, 0, nullptr) ||
        GetLastError() != ERROR_INVALID_PARAMETER ||
        FormatMessageA(flags | FORMAT_MESSAGE_FROM_STRING, nullptr, ERROR_ACCESS_DENIED, 0, nullptr, 0, nullptr) ||
        GetLastError() != ERROR_NOT_SUPPORTED ||
        FormatMessageA(flags, nullptr, ERROR_ACCESS_DENIED, 0x407, text, sizeof(text), nullptr) ||
        GetLastError() != ERROR_RESOURCE_LANG_NOT_FOUND) {
        return 6004;
    }
    DWORD language = 0;
    if (GetLocaleInfoEx(LOCALE_NAME_SYSTEM_DEFAULT, LOCALE_ILANGUAGE | LOCALE_RETURN_NUMBER, (LPWSTR)&language,
            sizeof(language) / sizeof(wchar_t)) ||
        GetLastError() != ERROR_NOT_SUPPORTED ||
        language) {
        return 6005;
    }
    return 42;
}
