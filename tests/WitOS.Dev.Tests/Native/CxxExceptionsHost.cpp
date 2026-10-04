#define WIN32_LEAN_AND_MEAN
#include <windows.h>

/* Windows harness for tests/User.X64/cxx_exceptions.cpp and cxx_runtime.cpp (P6.4.e, P6.4.g). The reference build
 * links the normal CRT and vcruntime and enters through main; the WitOS build links no CRT or vcruntime, only the
 * WitOS C++ runtime over kernel32 and ntdll, and enters through CxxTestStart. Both print one TRACE line with the same
 * tokens. */
extern "C" int cxx_exceptions_run();
extern "C" void cxx_runtime_run();

namespace {
char line[8192];
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
}

static int finish(int live)
{
    static const char prefix[] = "TRACE: ";
    DWORD written = 0;
    const HANDLE output = GetStdHandle(STD_OUTPUT_HANDLE);
    line[used++] = '\n';
    const BOOL ok = WriteFile(output, prefix, sizeof(prefix) - 1, &written, nullptr) &&
        WriteFile(output, line, used, &written, nullptr);
    return ok && live == 0 ? 0 : 1;
}

#if defined(CXX_REFERENCE)
int main()
{
    const int live = cxx_exceptions_run();
    cxx_runtime_run();
    return finish(live);
}
#else
/* atexit belongs to the C runtime (P6.4.i): static objects register their destructors, which run after the trace in
 * the reference and not at all here, so registration alone keeps the traces equal. */
extern "C" int __cdecl atexit(void(__cdecl *)(void))
{
    return 0;
}

extern "C" void __stdcall CxxTestStart()
{
    const int live = cxx_exceptions_run();
    cxx_runtime_run();
    ExitProcess((UINT)finish(live));
}
#endif
