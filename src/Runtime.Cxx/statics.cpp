#include "cxx_runtime.h"

/* Thread-safe initialization of function-local statics (P6.4.g), the contract MSVC compiles against. Each static has
 * a guard: 0 before initialization, -1 while one thread initializes it, then the global epoch at which it completed.
 * Compiled code skips the runtime while the guard is not above the thread's epoch (_Init_thread_epoch); otherwise
 * _Init_thread_header either claims the initialization (leaving the guard at -1) or waits for another thread and
 * advances the thread's epoch. _Init_thread_footer publishes a completed initialization, _Init_thread_abort reopens
 * one whose initializer threw, so the next call retries. */
namespace {
constexpr int UNINITIALIZED = 0, BEING_INITIALIZED = -1, EPOCH_START = (-2147483647 - 1);
int global_epoch = EPOCH_START;
} // namespace

extern "C" __declspec(thread) int _Init_thread_epoch = EPOCH_START;

using namespace WitCxx;

extern "C" void __cdecl _Init_thread_header(int *once) noexcept
{
    StaticsLock();
    if (*once == UNINITIALIZED) {
        *once = BEING_INITIALIZED;
    } else {
        while (*once == BEING_INITIALIZED) {
            StaticsWait();
            if (*once == UNINITIALIZED) {
                *once = BEING_INITIALIZED; // The other initializer threw: this thread retries.
                StaticsUnlock();
                return;
            }
        }
        _Init_thread_epoch = global_epoch;
    }
    StaticsUnlock();
}

extern "C" void __cdecl _Init_thread_footer(int *once) noexcept
{
    StaticsLock();
    *once = ++global_epoch;
    _Init_thread_epoch = global_epoch;
    StaticsUnlock();
    StaticsNotify();
}

extern "C" void __cdecl _Init_thread_abort(int *once) noexcept
{
    StaticsLock();
    *once = UNINITIALIZED;
    StaticsUnlock();
    StaticsNotify();
}
