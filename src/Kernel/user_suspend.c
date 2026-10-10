#include "user.h"

WitU64 wit_user_thread_suspend(WitUserProcess *process, WitU64 handle, int resume, WitU64 *previous)
{
    WitUserThread *target = 0;
    *previous = 0;
    if (wit_processors_scheduling() != 1) {
        return WIT_STATUS_UNSUPPORTED;
    }
    const WitU64 status = wit_user_reference_target(process, handle, WIT_RIGHT_SUSPEND_RESUME, &target);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    if (!resume && target->SuspendCount == WIT_THREAD_SUSPEND_MAX) {
        return WIT_STATUS_TOO_LARGE;
    }
    *previous = target->SuspendCount;
    // Diagnostic evidence of an actual parked foreign context, not a user
    // readiness flag. Exclude the main thread's ordinary join waits.
    if (!resume &&
        !target->SuspendCount &&
        target != &process->Threads[0] &&
        target->State == WitThreadWaiting &&
        target->WaitKind == WitWaitObjects) {
        ++process->ForeignObjectWaitSuspends;
    }
    if (resume) {
        if (target->SuspendCount) {
            --target->SuspendCount;
        }
    } else {
        ++target->SuspendCount;
    }
    // Wait completion stays independent. Scheduler eligibility alone is gated.
    return WIT_STATUS_OK;
}
