#include "user.h"

/* Activations (RFC 0011 section 7.5). THREAD_ACTIVATE marks a target thread with the requester's callback and
 * argument; at the target's next return to user mode the kernel enters the process's fault callback with a record
 * whose vector is WIT_EXCEPTION_ACTIVATION_VECTOR, whose Address and Error carry the callback and the argument and
 * whose context is the interrupted one. The handler runs the callback in user space and EXCEPTION_CONTINUE resumes
 * the context; a wait or sleep the target was parked in ends with INTERRUPTED. Nothing runs in CPL0 for the caller:
 * the kernel only marks, wakes and enters. */

void wit_user_activations_clear(WitUserThread *thread)
{
    thread->ActivationCount = 0;
    for (WitU32 i = 0; i < WIT_ACTIVATION_CAPACITY; ++i) {
        thread->Activations[i].Callback = 0;
        thread->Activations[i].Argument = 0;
    }
}

/* Validates the handle and its ACTIVATE right, the callback (executable, not writable), the registered fault callback
 * and the per-thread quota before it marks the target; a parked target is woken here and learns INTERRUPTED after
 * the handler continues its interrupted context. */
WitU64 wit_user_thread_activate(WitUserProcess *process, WitU64 handle, WitU64 callback, WitU64 argument)
{
    WitUserThread *target = 0;
    const WitU64 status = wit_user_reference_target(process, handle, WIT_RIGHT_ACTIVATE, &target);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    if (!wit_arch_context_supported()) {
        return WIT_STATUS_UNSUPPORTED;
    }
    if (!wit_user_space_physical(&process->Space, callback, 0, 1) ||
        wit_user_space_physical(&process->Space, callback, 1, 0)) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    if (!process->ExceptionCallback) {
        return WIT_STATUS_NOT_FOUND;
    }
    if (target->ActivationCount == WIT_ACTIVATION_CAPACITY) {
        return WIT_STATUS_NO_MEMORY;
    }
    target->Activations[target->ActivationCount].Callback = callback;
    target->Activations[target->ActivationCount].Argument = argument;
    ++target->ActivationCount;
    wit_user_wait_interrupt(process, target);
    return WIT_STATUS_OK;
}

/* Called for the running thread with interrupts disabled just before its frame returns to user mode. A thread inside a
 * delivery (a fault's or an activation's handler) or suspended keeps its activations pending; they follow in order,
 * one per return, each through EXCEPTION_CONTINUE of the previous one. */
int wit_user_activation_deliver(WitUserProcess *process, WitUserThread *thread)
{
    if (!thread->ActivationCount ||
        thread->Exception.Token ||
        thread->SuspendCount ||
        thread->State != WitThreadRunning) {
        return 0;
    }
    WitArchFrame *frame = thread->Context;
    const WitUserActivation activation = thread->Activations[0];
    WitUserExceptionInfo info = {0};
    info.Version = WIT_EXCEPTION_VERSION;
    info.Size = sizeof(info);
    info.Vector = WIT_EXCEPTION_ACTIVATION_VECTOR;
    wit_user_context_snapshot(&info.Context, thread, frame);
    info.Context.Flags |= WIT_THREAD_CONTEXT_EXCEPTION_ACTIVE;
    wit_arch_exception_record(&info, frame, 0);
    info.Error = activation.Argument;
    info.Address = activation.Callback;
    if (!wit_user_exception_enter(process, thread, frame, &info)) {
        /* The interrupted stack has no room for the callback frame (EXCEPTION_REGISTER refuses to drop the callback
         * while an activation is pending, so no other cause remains): the component ends as a fault of the
         * interrupted thread, as a POSIX process ends when its signal frame does not fit. */
        WitArchFaultState state;
        wit_arch_fault_from_frame(&state, frame);
        wit_user_fault_state(WIT_EXCEPTION_ACTIVATION_VECTOR, activation.Argument, activation.Callback, &state);
    }
    for (WitU32 i = 1; i < thread->ActivationCount; ++i) {
        thread->Activations[i - 1] = thread->Activations[i];
    }
    --thread->ActivationCount;
    thread->Activations[thread->ActivationCount].Callback = 0;
    thread->Activations[thread->ActivationCount].Argument = 0;
    ++process->ActivationDeliveries;
    return 1;
}
