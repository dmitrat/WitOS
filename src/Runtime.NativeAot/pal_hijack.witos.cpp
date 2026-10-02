#include "common.h"
#include "gcenv.h"
#include "regdisplay.h"
#include "StackFrameIterator.h"
#include "thread.h"
#include "NativeContext.h"
#include "pal.witos.h"
#include <errno.h>
#include "hijack_evidence.witos.h"

static WitHijackEvidence evidence;

WitHijackEvidence wit_pal_hijack_evidence()
{
    return evidence;
}

// WitOS uses the pinned PalMinWin suspend/context fallback. Special asynchronous
// Windows APC delivery is absent; ordinary alertable APCs are not a substitute.
// ThreadStore serializes the caller. The return-address walk itself owns a
// stack lease; Redirect must remain free to perform validated context mutation.
void PalHijack(Thread *target)
{
    if (!target) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    WitUserThreadInfo owner;
    if (wit_native_call(WIT_CALL_THREAD_QUERY, (WitU64)&owner, sizeof(owner), WIT_THREAD_INFO_VERSION, nullptr) !=
            WIT_STATUS_OK ||
        owner.Version != WIT_THREAD_INFO_VERSION ||
        owner.Size != sizeof(owner) ||
        !owner.ThreadId ||
        !owner.CompilerTls) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    const DWORD saved_error = GetLastError();
    const int saved_errno = errno;
    ++evidence.Attempts;
    const HANDLE handle = target->GetOSThreadHandle();
    if (handle != INVALID_HANDLE_VALUE && SuspendThread(handle) != (DWORD)-1) {
        NATIVE_CONTEXT context = {};
        context.ctx.ContextFlags = CONTEXT_FULL | CONTEXT_DEBUG_REGISTERS | CONTEXT_EXCEPTION_REQUEST;
        if (PalGetCompleteThreadContext(handle, &context) &&
            (context.ctx.ContextFlags & CONTEXT_EXCEPTION_REPORTING) &&
            !(context.ctx.ContextFlags & (CONTEXT_SERVICE_ACTIVE | CONTEXT_EXCEPTION_ACTIVE))) {
            const auto before = context.GetIp();
            const bool hijacked = target->IsHijacked();
            Thread::HijackCallback(&context, target, false);
            if (!hijacked && target->IsHijacked()) {
                ++evidence.ReturnHijacks;
            }
            NATIVE_CONTEXT after = {};
            after.ctx.ContextFlags = CONTEXT_FULL | CONTEXT_DEBUG_REGISTERS | CONTEXT_EXCEPTION_REQUEST;
            if (PalGetCompleteThreadContext(handle, &after) && after.GetIp() != before) {
                ++evidence.Redirects;
            }
        } else if (context.ctx.ContextFlags & (CONTEXT_SERVICE_ACTIVE | CONTEXT_EXCEPTION_ACTIVE)) {
            ++evidence.UnsafeSnapshots;
        }
        // Every successful suspension owns exactly one resume, including unsafe
        // service/exception snapshots and callback no-op/failed-redirection paths.
        if (ResumeThread(handle) == (DWORD)-1) {
            wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
        }
    }
    errno = saved_errno;
    SetLastError(saved_error);
}
