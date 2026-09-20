#include "tls.h"

typedef struct ThreadStart {
    WitNativeThreadMain Entry;
    WitU64 Argument;
} ThreadStart;
static ThreadStart starts[4];
static volatile WitU32 gate;
static void lock(void)
{
    while (!wit_native_try_lock(&gate))
        if (wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, 0) != WIT_STATUS_OK)
            wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}
static void run(WitU64 slot)
{
    WitNativeThreadMain entry;
    WitU64 argument;
    if (slot >= 4) wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    lock();
    entry = starts[slot].Entry;
    argument = starts[slot].Argument;
    starts[slot].Entry = 0;
    wit_native_unlock(&gate);
    if (!wit_native_tls_code_pointer((WitU64)entry)) wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    wit_native_tls_enter();
    wit_native_thread_exit(entry(argument));
}
WitU64 wit_native_thread_create(WitNativeThreadMain entry, WitU64 argument, WitU64 *handle)
{
    WitU32 slot;
    WitU64 status;
    if (!handle) return WIT_STATUS_INVALID_ARGUMENT;
    *handle = 0;
    if (!wit_native_tls_code_pointer((WitU64)entry)) return WIT_STATUS_BAD_ADDRESS;
    lock();
    for (slot = 0; slot < 4; ++slot) if (!starts[slot].Entry) break;
    if (slot == 4) { wit_native_unlock(&gate); return WIT_STATUS_NO_MEMORY; }
    starts[slot].Argument = argument;
    starts[slot].Entry = entry;
    // ThreadCreate does not park. Keep this slot locked through success/failure
    // publication so a preempted creator cannot erase a slot already reused.
    status = wit_native_call(WIT_CALL_THREAD_CREATE, (WitU64)run, slot, 0, handle);
    if (status != WIT_STATUS_OK) starts[slot].Entry = 0;
    wit_native_unlock(&gate);
    return status;
}
WIT_NORETURN void wit_native_thread_exit(WitU64 code)
{
    wit_native_tls_leave();
    (void)wit_native_call(WIT_CALL_THREAD_EXIT, code, 0, 0, 0);
    wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}
