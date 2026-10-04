#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <wchar.h>

/* Windows harness for tests/User.X64/crt_scenarios.cpp (P6.4.h). The reference build links UCRT and enters through
 * main; the WitOS build links no C runtime, only the WitOS UCRT subset over kernel32, and enters through CrtTestStart,
 * which sets the stdio options a UCRT module's startup sets. Both print one TRACE line with the C runtime under
 * test. */
extern "C" void crt_scenarios_run();

namespace {
char line[16384];
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
}

static int finish()
{
    return fwprintf(stdout, L"TRACE: %hs\n", line) > 0 ? 0 : 1;
}

#if defined(CRT_REFERENCE)
int main()
{
    crt_scenarios_run();
    return finish();
}
#else
extern "C" void wit_crt_initialize_stdio_options(void);

extern "C" void __stdcall CrtTestStart()
{
    wit_crt_initialize_stdio_options();
    crt_scenarios_run();
    ExitProcess((UINT)finish());
}
#endif
