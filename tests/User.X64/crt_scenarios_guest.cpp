#include <errno.h>
#include <io.h>
#include <share.h>
#include <stdio.h>
#include <time.h>
extern "C" {
#include "bootstrap.h"
#include "../User/protocol.h"
}
#include "crt_trace.h"

/* The guest half of P6.4.h: tests/User.X64/crt_scenarios.cpp on the WitOS UCRT subset, the native heap and the
 * process console must print the trace UCRT prints on Windows, WIT_CRT_TRACE, which the tool generates. The trace
 * grows in the report page, so the kernel can print how far a failed run got. The guest has no UTC clock and
 * read-only storage, which the last checks show. */
extern "C" void crt_scenarios_run();
extern "C" void wit_crt_initialize_stdio_options(void);

namespace {
constexpr unsigned TRACE_OFFSET = 0x100, TRACE_CAPACITY = 0xE00;

char line[4096];
unsigned used;
} // namespace

extern "C" void crt_trace(const char *text)
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

extern "C" WitU64 wit_crt_scenarios_probe()
{
    wit_crt_initialize_stdio_options(); // as a component's startup sets them before formatted output
    crt_scenarios_run();
    const char *expected = WIT_CRT_TRACE;
    unsigned same = 0;
    while (expected[same] && expected[same] == line[same]) {
        ++same;
    }
    if (expected[same] || same != used) {
        return 2300 + (same < 1699 ? same : 1699);
    }
    if (_time64(nullptr) != -1) {
        return 2201;
    }
    errno = 0;
    if (_wfsopen(L"boot:/crt.txt", L"w", _SH_DENYNO) || errno != EACCES) {
        return 2202;
    }
    errno = 0;
    if (_wremove(L"boot:/crt.txt") != -1 || errno != EACCES || _wrename(L"boot:/a", L"boot:/b") != -1) {
        return 2203;
    }
    return 42;
}
