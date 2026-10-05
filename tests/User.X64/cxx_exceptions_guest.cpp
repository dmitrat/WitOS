extern "C" {
#include "bootstrap.h"
#include "../User/protocol.h"
}
#include "cxx_exception_trace.h"

/* The guest half of P6.4.f and P6.4.g: tests/User.X64/cxx_exceptions.cpp and cxx_runtime.cpp on the WitOS C++
 * runtime, the guest's own exception dispatch, RtlUnwindEx consolidation, GS check and native heap must print the
 * trace vcruntime prints on Windows, WIT_CXX_EXCEPTION_TRACE, which the tool generates. The trace grows in the report
 * page, so the kernel can print how far a failed run got. */
extern "C" int cxx_exceptions_run();
extern "C" void cxx_runtime_run();

namespace {
constexpr unsigned TRACE_OFFSET = 0x100, TRACE_CAPACITY = 0xE00;

char line[1024];
unsigned used;
} // namespace

extern "C" void cxx_trace(const char *text)
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

/* atexit belongs to the C runtime (P6.4.i): static objects register their destructors, which run after the trace on
 * Windows, so registration alone keeps the traces equal. */
extern "C" int __cdecl atexit(void(__cdecl *)(void))
{
    return 0;
}

extern "C" WitU64 wit_cxx_exceptions_probe()
{
    const int live = cxx_exceptions_run();
    cxx_runtime_run();
    const char *expected = WIT_CXX_EXCEPTION_TRACE;
    unsigned same = 0;
    while (expected[same] && expected[same] == line[same]) {
        ++same;
    }
    if (!live && !expected[same] && same == used) {
        return 42;
    }
    return 2100 + (same < 899 ? same : 899);
}
