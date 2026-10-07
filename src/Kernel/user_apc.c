#include "user.h"
#include "witos/platform.h"

/* This module is wired into the dispatcher with the extended object waits.
 * Callbacks run only in user space after a checked dequeue, never in CPL0. */
void wit_user_apc_initialize(WitUserThread *thread)
{
    thread->ApcCount = 0;
    for (WitU32 i = 0; i < WIT_APC_CAPACITY; ++i) {
        thread->Apcs[i].Callback = 0;
        thread->Apcs[i].Argument = 0;
    }
}

WitU64 wit_user_apc_queue(WitUserProcess *process, WitU64 reference, WitU64 callback, WitU64 argument)
{
    WitUserThread *target = 0;
    const WitU64 status = wit_user_reference_target(process, reference, WIT_RIGHT_SET_CONTEXT, &target);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    if (callback < process->ImageBase ||
        callback - process->ImageBase >= process->ImageSize ||
        !wit_user_space_physical(&process->Space, callback, 0, 1)) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    if (target->ApcCount == WIT_APC_CAPACITY) {
        return WIT_STATUS_NO_MEMORY;
    }
    target->Apcs[target->ApcCount].Callback = callback;
    target->Apcs[target->ApcCount].Argument = argument;
    ++target->ApcCount;
    // An already completed event/timeout result is never overwritten.
    if (target->State == WitThreadWaiting && target->WaitAlertable) {
        wit_user_wait_complete(target, WIT_STATUS_INTERRUPTED, 0);
    }
    return WIT_STATUS_OK;
}

WitU64 wit_user_apc_dequeue(WitUserProcess *process, WitU64 output, WitU64 size)
{
    WitUserThread *thread = &process->Threads[process->CurrentThread];
    if (size != sizeof(WitUserApc)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (!wit_user_buffer_writable(&process->Space, output, sizeof(WitUserApc))) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    if (!thread->ApcCount) {
        return WIT_STATUS_TIMED_OUT;
    }
    const WitUserApc value = thread->Apcs[0];
    for (WitU32 i = 1; i < thread->ApcCount; ++i) {
        thread->Apcs[i - 1] = thread->Apcs[i];
    }
    --thread->ApcCount;
    thread->Apcs[thread->ApcCount].Callback = 0;
    thread->Apcs[thread->ApcCount].Argument = 0;
    if (!wit_user_copy_to(&process->Space, output, (const WitU8 *)&value, sizeof(value))) {
        wit_panic("Validated APC dequeue copy failed");
    }
    return WIT_STATUS_OK;
}
