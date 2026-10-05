#include <errno.h>
#include <process.h>
#include <string.h>
#include <chrono>
#include <system_error>
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
extern "C" {
#include "bootstrap.h"
#include "../User/protocol.h"
}
#include "stl_trace.h"

/* The guest half of P6.4.i: tests/User.X64/stl_scenarios.cpp on the separately compiled sources of the pinned
 * microsoft/STL, the WitOS C++ and C runtimes and the guest must print the trace msvcp140 prints on Windows,
 * WIT_STL_TRACE, which the tool generates. The trace grows in the report page, so the kernel can print how far a
 * failed run got. Windows error messages come from the guest's own catalogue, which the checks after the trace show,
 * together with the contracts of the guest's own Win32 functions under the STL: FormatMessageA, GetLocaleInfoEx, SRW
 * locks and condition variables, thread exit codes, SwitchToThread and GetNativeSystemInfo, which the hosted builds
 * take from Windows. The report's first word holds the processor level the vectorized algorithms run at. */
extern "C" void stl_scenarios_run();
extern "C" void wit_cxx_initialize_isa(void);
extern "C" int __isa_available;

namespace {
constexpr unsigned TRACE_OFFSET = 0x100, TRACE_CAPACITY = 0xE00;

char line[2048];
unsigned used;

struct Shared {
    SRWLOCK Lock;
    CONDITION_VARIABLE Woken, Finish;
    int Stage;
};

/* Takes the lock, which the main thread's wait released, wakes it, then waits until it may end. */
unsigned __stdcall Waker(void *context)
{
    auto &shared = *static_cast<Shared *>(context);
    AcquireSRWLockExclusive(&shared.Lock);
    shared.Stage = 1;
    WakeConditionVariable(&shared.Woken);
    while (shared.Stage != 2) {
        if (!SleepConditionVariableSRW(&shared.Finish, &shared.Lock, INFINITE, 0)) {
            ReleaseSRWLockExclusive(&shared.Lock);
            return 1;
        }
    }
    ReleaseSRWLockExclusive(&shared.Lock);
    return 5;
}

unsigned __stdcall Mark(void *context)
{
    *static_cast<volatile int *>(context) = 1;
    return 0;
}

WitU64 Synchronization()
{
    static Shared shared = {SRWLOCK_INIT, CONDITION_VARIABLE_INIT, CONDITION_VARIABLE_INIT, 0};
    SRWLOCK &lock = shared.Lock;
    if (!TryAcquireSRWLockExclusive(&lock) || TryAcquireSRWLockExclusive(&lock)) {
        return 6010;
    }
    // A wait nothing wakes ends at its timeout with ERROR_TIMEOUT and the lock held again.
    SetLastError(0);
    if (SleepConditionVariableSRW(&shared.Woken, &lock, 30, 0) ||
        GetLastError() != ERROR_TIMEOUT ||
        TryAcquireSRWLockExclusive(&lock) ||
        SleepConditionVariableSRW(&shared.Woken, &lock, 0, 0) ||
        GetLastError() != ERROR_TIMEOUT) {
        return 6011;
    }
    // Shared mode is not implemented: the wait fails at once and the lock stays held.
    if (SleepConditionVariableSRW(&shared.Woken, &lock, 0, CONDITION_VARIABLE_LOCKMODE_SHARED) ||
        GetLastError() != ERROR_NOT_SUPPORTED ||
        TryAcquireSRWLockExclusive(&lock)) {
        return 6012;
    }
    // Timeouts return their events unsignaled: the wait below parks until its wake.
    for (DWORD i = 0; i < 20; ++i) {
        (void)SleepConditionVariableSRW(&shared.Woken, &lock, i % 3, 0);
    }
    // A wait that another thread wakes long before its timeout; the thread's exit code once it ends.
    const uintptr_t thread = _beginthreadex(nullptr, 0, Waker, &shared, 0, nullptr);
    if (!thread) {
        return 6013;
    }
    const BOOL woken = SleepConditionVariableSRW(&shared.Woken, &lock, 5000, 0);
    const int stage = shared.Stage;
    DWORD code = 0;
    const BOOL running = GetExitCodeThread((HANDLE)thread, &code);
    shared.Stage = 2;
    WakeAllConditionVariable(&shared.Finish);
    ReleaseSRWLockExclusive(&lock);
    if (!woken || stage != 1 || !running || code != STILL_ACTIVE) {
        return 6014;
    }
    if (WaitForSingleObject((HANDLE)thread, INFINITE) != WAIT_OBJECT_0 ||
        !GetExitCodeThread((HANDLE)thread, &code) ||
        code != 5 ||
        GetExitCodeThread((HANDLE)thread, nullptr) ||
        GetLastError() != ERROR_INVALID_PARAMETER ||
        !CloseHandle((HANDLE)thread) ||
        GetExitCodeThread((HANDLE)thread, &code)) {
        return 6015;
    }
    // _beginthreadex reports what the guest's CreateThread rejects through errno.
    SECURITY_ATTRIBUTES security = {sizeof(security), nullptr, FALSE};
    volatile int marked = 0;
    errno = 0;
    if (_beginthreadex(&security, 0, Mark, (void *)&marked, 0, nullptr) || errno != EINVAL) {
        return 6016;
    }
    errno = 0;
    if (_beginthreadex(nullptr, 0, Mark, (void *)&marked, 0x8, nullptr) || errno != EINVAL || marked) {
        return 6017;
    }
    // A ready thread runs when this one yields; SwitchToThread reports that it switched.
    unsigned id = 0;
    const uintptr_t ready = _beginthreadex(nullptr, 0, Mark, (void *)&marked, 0, &id);
    if (!ready ||
        !id ||
        !SwitchToThread() ||
        WaitForSingleObject((HANDLE)ready, INFINITE) != WAIT_OBJECT_0 ||
        !marked ||
        !CloseHandle((HANDLE)ready)) {
        return 6018;
    }
    SYSTEM_INFO info;
    GetNativeSystemInfo(&info);
    if (info.wProcessorArchitecture != PROCESSOR_ARCHITECTURE_AMD64 ||
        info.dwNumberOfProcessors != 1 ||
        info.dwActiveProcessorMask != 1 ||
        info.dwPageSize != 4096 ||
        info.dwAllocationGranularity != 65536 ||
        info.dwProcessorType != PROCESSOR_AMD_X8664 ||
        !info.wProcessorLevel ||
        info.lpMinimumApplicationAddress >= info.lpMaximumApplicationAddress) {
        return 6019;
    }
    return 42;
}
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
    return Synchronization();
}

/* The guest has no UTC clock: system_clock ends the process, as the Windows function under it cannot fail. */
extern "C" WitU64 wit_stl_no_utc_probe()
{
    (void)std::chrono::system_clock::now();
    return 6100;
}
