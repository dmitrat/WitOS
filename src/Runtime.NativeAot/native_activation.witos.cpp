#include "pal.witos.h"
#include "native_activation.witos.h"
#include "witos/exception.h"

// The one fault callback of the process (EXCEPTION_REGISTER): zero, this module's activation-only entry or the
// exception dispatcher's. The gate serializes registration; it is never held across a kernel wait.
static volatile WitU32 gate;
static WitU64 registered;

extern "C" WitU64 wit_native_fault_callback_register(WitU64 entry)
{
    while (!wit_native_try_lock(&gate)) {
        wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr);
    }
    WitU64 status = WIT_STATUS_OK;
    const bool yields = registered == 0 || registered == (WitU64)&wit_native_activation_entry;
    if (registered != entry && yields) {
        status = wit_native_call(WIT_CALL_EXCEPTION_REGISTER, entry, WIT_EXCEPTION_VERSION, 0, nullptr);
        if (status == WIT_STATUS_OK) {
            registered = entry;
        }
    }
    wit_native_unlock(&gate);
    return status;
}

extern "C" WitU64 wit_native_activation_install(void)
{
    return wit_native_fault_callback_register((WitU64)&wit_native_activation_entry);
}

[[noreturn]] static void reject(WitU64 token)
{
    wit_native_call(WIT_CALL_EXCEPTION_REJECT, token, 0, 0, nullptr);
    wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}

extern "C" void wit_native_activation_dispatch(WitU64 token, WitU64 address)
{
    WitUserExceptionInfo info;
    WitUserThreadInfo self;
    if (wit_native_call(WIT_CALL_EXCEPTION_QUERY, token, (WitU64)&info, sizeof(info), nullptr) != WIT_STATUS_OK ||
        info.Version != WIT_EXCEPTION_VERSION ||
        info.Size != sizeof(info) ||
        info.Token != token ||
        info.Vector != WIT_EXCEPTION_ACTIVATION_VECTOR ||
        info.Address != address ||
        !info.Address ||
        wit_native_thread_query(WIT_THREAD_SELF, &self) != WIT_STATUS_OK ||
        info.Context.ThreadId != self.ThreadId ||
        !(info.Context.Flags & WIT_THREAD_CONTEXT_EXCEPTION_ACTIVE)) {
        reject(token);
    }
    // The requester's callback runs on the interrupted thread, below the interrupted frame, with no kernel gate
    // held; it may queue further activations, which follow after this one continues.
    ((PAPCFUNC)(uintptr_t)info.Address)((ULONG_PTR)info.Error);
    WitUserExceptionTransfer transfer = {};
    transfer.Version = WIT_EXCEPTION_TRANSFER_VERSION;
    transfer.Size = sizeof(transfer);
    transfer.RetireThroughToken = token;
    transfer.Context = info.Context;
    wit_native_call(WIT_CALL_EXCEPTION_CONTINUE, token, (WitU64)&transfer, sizeof(transfer), nullptr);
    reject(token);
}

extern "C" void wit_native_activation_entry(WitU64 token, WitU64 vector, WitU64 address)
{
    if (vector == WIT_EXCEPTION_ACTIVATION_VECTOR) {
        wit_native_activation_dispatch(token, address);
    }
    // No exception dispatcher is linked into this module: the fault stays the kernel's to report.
    reject(token);
}
