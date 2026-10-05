#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <wchar.h>

/* Windows harness for tests/User.X64/stl_scenarios.cpp (P6.4.i). The reference build uses the toolset's STL, msvcp140,
 * vcruntime and UCRT and enters through main. The WitOS build compiles the scenarios against the pinned microsoft/STL
 * headers and links its separately compiled sources with the WitOS C++ runtime and UCRT subset over kernel32 and
 * ntdll; it enters through StlTestStart, which does what a module's startup does first. Both print one TRACE line. */
extern "C" void stl_scenarios_run();

namespace {
char line[16384];
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
}

static int finish()
{
    return fwprintf(stdout, L"TRACE: %hs\n", line) > 0 ? 0 : 1;
}

#if defined(STL_REFERENCE)
int main()
{
    stl_scenarios_run();
    return finish();
}
#else
extern "C" void wit_crt_initialize_stdio_options(void);
extern "C" void wit_cxx_initialize_isa(void);
extern "C" int wit_cxx_run_initializers(void);

/* Static objects register their destructors, which run after the trace in the reference and not at all here, so
 * registration alone keeps the traces equal; the guest has the native atexit. */
extern "C" int __cdecl atexit(void(__cdecl *)(void))
{
    return 0;
}

extern "C" void __stdcall StlTestStart()
{
    wit_crt_initialize_stdio_options();
    wit_cxx_initialize_isa();
    if (wit_cxx_run_initializers()) {
        ExitProcess(3);
    }
    stl_scenarios_run();
    ExitProcess((UINT)finish());
}
#endif
