#define WIN32_LEAN_AND_MEAN
#include <windows.h>

/* Windows harness for tests/User.X64/cxx_exceptions.cpp (P6.4.e). The reference build links the normal CRT and
 * vcruntime and enters through main; the WitOS build links no CRT or vcruntime, only the WitOS C++ runtime over
 * kernel32 and ntdll, and enters through CxxTestStart. Both print one TRACE line with the same tokens. */
extern "C" int cxx_exceptions_run();

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
    return finish(cxx_exceptions_run());
}
#else
/* The deleting destructors of the scenario classes refer to sized delete, which the scenarios never call; the
 * runtime's own new and delete arrive with P6.4.g. */
void operator delete(void *, size_t) noexcept
{
    __fastfail(FAST_FAIL_FATAL_APP_EXIT);
}

extern "C" void __stdcall CxxTestStart()
{
    ExitProcess((UINT)finish(cxx_exceptions_run()));
}
#endif
