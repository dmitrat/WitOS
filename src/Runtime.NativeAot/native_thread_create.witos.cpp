#include "pal.witos.h"
#include "tls.h"
extern "C" int WINAPI wit_native_thread_priority(HANDLE);
extern "C" DWORD WINAPI wit_native_wait_multiple(DWORD, const HANDLE *, BOOL, DWORD, BOOL);

namespace {
struct ManagedStart {
    LPTHREAD_START_ROUTINE Callback;
    void *Argument;
};

ManagedStart starts[4];
volatile WitU32 gate;

void lock()
{
    while (!wit_native_try_lock(&gate)) {
        if (wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr) != WIT_STATUS_OK) {
            wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
        }
    }
}

WitU64 run(WitU64 slot)
{
    if (slot >= 4) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    lock();
    const auto callback = starts[slot].Callback;
    void *argument = starts[slot].Argument;
    starts[slot].Callback = nullptr;
    wit_native_unlock(&gate);
    if (!wit_native_tls_code_pointer((WitU64)callback)) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    return callback(argument); // Preserve the actual DWORD WINAPI callback ABI.
}
}

extern "C" HANDLE WINAPI wit_native_create_thread(LPSECURITY_ATTRIBUTES attributes, SIZE_T stackBytes,
    LPTHREAD_START_ROUTINE callback, LPVOID argument, DWORD flags, LPDWORD nativeId)
{
    if (attributes || (flags & ~(CREATE_SUSPENDED | STACK_SIZE_PARAM_IS_A_RESERVATION))) {
        SetLastError(ERROR_NOT_SUPPORTED);
        return nullptr;
    }
    if (!wit_native_tls_code_pointer((WitU64)callback)) {
        SetLastError(ERROR_INVALID_ADDRESS);
        return nullptr;
    }
    lock();
    unsigned slot = 0;
    while (slot < 4 && starts[slot].Callback) {
        ++slot;
    }
    if (slot == 4) {
        wit_native_unlock(&gate);
        SetLastError(ERROR_NOT_ENOUGH_MEMORY);
        return nullptr;
    }
    starts[slot] = {callback, argument};
    WitU64 reference = 0;
    // System.Native's real wrapper enters/leaves TLS and invokes runtime exit
    // notification before THREAD_COMPLETE. Kernel publication is atomic.
    const auto status = wit_native_thread_create_reference(
        run, slot, stackBytes, flags & CREATE_SUSPENDED ? WIT_THREAD_START_SUSPENDED : 0, (WitU64)nativeId, &reference);
    if (status != WIT_STATUS_OK) {
        starts[slot].Callback = nullptr;
    }
    wit_native_unlock(&gate);
    return wit_pal_result(status) ? (HANDLE)reference : nullptr;
}

extern "C" BOOL WINAPI wit_native_set_thread_priority(HANDLE handle, int priority)
{
    if (handle == (HANDLE)(intptr_t)-2) {
        if (wit_native_thread_priority(handle) == THREAD_PRIORITY_ERROR_RETURN) {
            return FALSE;
        }
    } else {
        WitThreadReferenceInfo info;
        WitU64 copied = 0;
        if (!wit_pal_result(wit_native_call(
                WIT_CALL_THREAD_REFERENCE_QUERY, (WitU64)handle, (WitU64)&info, sizeof(info), &copied))) {
            return FALSE;
        }
        if (copied != sizeof(info) || info.Version != WIT_THREAD_REFERENCE_VERSION || info.Size != sizeof(info)) {
            wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
        }
        if (info.State == WIT_THREAD_REFERENCE_EXITED) {
            SetLastError(ERROR_INVALID_HANDLE);
            return FALSE;
        }
    }
    // Normal is the sole implemented scheduling class, so this is an identity
    // operation after validation. Other priorities are explicitly unsupported.
    if (priority != THREAD_PRIORITY_NORMAL) {
        SetLastError(ERROR_NOT_SUPPORTED);
        return FALSE;
    }
    return TRUE;
}

extern "C" BOOL WINAPI wit_native_reset_event(HANDLE handle)
{
    return (BOOL)PalResetEvent(handle);
}

extern "C" DWORD WINAPI wit_native_wait_single(HANDLE handle, DWORD timeout)
{
    return wit_native_wait_multiple(1, &handle, FALSE, timeout, FALSE);
}

extern "C" DWORD WINAPI wit_native_wait_single_ex(HANDLE handle, DWORD timeout, BOOL alertable)
{
    return wit_native_wait_multiple(1, &handle, FALSE, timeout, alertable);
}
