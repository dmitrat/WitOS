#include <new>
#include "cxx_runtime.h"

/* The C++ runtime's platform functions on Windows, for the hosted reference build (P6.4.e, P6.4.g): fail-fast, the
 * statics lock and, in place of the guest's native heap, the nothrow allocator over the process heap. */
namespace WitCxx {

namespace {
SRWLOCK statics = SRWLOCK_INIT;
CONDITION_VARIABLE finished = CONDITION_VARIABLE_INIT;
} // namespace

void Fatal()
{
    __fastfail(FAST_FAIL_FATAL_APP_EXIT);
}

void StaticsLock()
{
    AcquireSRWLockExclusive(&statics);
}

void StaticsUnlock()
{
    ReleaseSRWLockExclusive(&statics);
}

void StaticsWait()
{
    SleepConditionVariableSRW(&finished, &statics, INFINITE, 0);
}

void StaticsNotify()
{
    WakeAllConditionVariable(&finished);
}

} // namespace WitCxx

namespace std {
const nothrow_t nothrow{};
}

void *__cdecl operator new(size_t size, const std::nothrow_t &) noexcept
{
    return HeapAlloc(GetProcessHeap(), 0, size ? size : 1);
}

void *__cdecl operator new[](size_t size, const std::nothrow_t &) noexcept
{
    return HeapAlloc(GetProcessHeap(), 0, size ? size : 1);
}

void __cdecl operator delete(void *address) noexcept
{
    if (address) {
        HeapFree(GetProcessHeap(), 0, address);
    }
}

void __cdecl operator delete[](void *address) noexcept
{
    operator delete(address);
}

void __cdecl operator delete(void *address, size_t) noexcept
{
    operator delete(address);
}

void __cdecl operator delete[](void *address, size_t) noexcept
{
    operator delete(address);
}

void __cdecl operator delete(void *address, const std::nothrow_t &) noexcept
{
    operator delete(address);
}

void __cdecl operator delete[](void *address, const std::nothrow_t &) noexcept
{
    operator delete(address);
}
