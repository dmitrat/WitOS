#include "user.h"
#include "witos/platform.h"

static WitArchFrame *owned_context(WitUserProcess *process, WitUserThread *target)
{
    const WitU32 index = (WitU32)(target - process->Threads);
    WitArchFrame *saved = target->Context;
    if (!wit_arch_frame_owned(saved, process->Slot, index) ||
        !wit_arch_frame_returns_to_user(saved) ||
        wit_arch_frame_sp(saved) < target->StackBottom ||
        wit_arch_frame_sp(saved) >= target->StackTop ||
        !wit_user_space_physical(&process->Space, wit_arch_frame_pc(saved), 0, 1)) {
        wit_panic("Context snapshot lost owning kernel/user frame");
    }
    return saved;
}

/* The flags a context of the thread carries: the register profile and the thread's suspension, wait and fault state;
 * THREAD_QUERY reports the same word so user space can build a context prefix. */
WitU32 wit_user_context_flags(const WitUserThread *target)
{
    return wit_arch_context_profile() |
        (target->SuspendCount ? WIT_THREAD_CONTEXT_SUSPENDED : 0) |
        (target->WaitKind != WitWaitNone ? WIT_THREAD_CONTEXT_SERVICE_ACTIVE : 0) |
        (target->Exception.Token ? WIT_THREAD_CONTEXT_EXCEPTION_ACTIVE : 0);
}

static void describe(WitThreadContext *snapshot, const WitUserThread *target)
{
    snapshot->Version = WIT_THREAD_CONTEXT_VERSION;
    snapshot->Size = sizeof(*snapshot);
    snapshot->ThreadId = target->Handle;
    snapshot->StackLow = target->StackBottom;
    snapshot->StackHigh = target->StackTop;
    snapshot->State = target->State == WitThreadRunning ? WIT_THREAD_CONTEXT_RUNNING
        : target->State == WitThreadWaiting             ? WIT_THREAD_CONTEXT_WAITING
                                                        : WIT_THREAD_CONTEXT_READY;
    snapshot->SuspendCount = target->SuspendCount;
    snapshot->Flags = wit_user_context_flags(target);
    wit_arch_context_describe(snapshot);
}

void wit_user_context_snapshot(WitThreadContext *snapshot, const WitUserThread *target, const WitArchFrame *saved)
{
    for (WitU32 i = 0; i < sizeof(*snapshot); ++i) {
        ((WitU8 *)snapshot)[i] = 0;
    }
    describe(snapshot, target);
    wit_arch_context_capture(snapshot, saved);
}

WitU64 wit_user_thread_context_get(WitUserProcess *process, WitU64 reference, WitU64 address, WitU64 size)
{
    WitUserThread *target = 0;
    if (size != sizeof(WitThreadContext)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    const WitU64 status = wit_user_reference_target(process, reference, WIT_RIGHT_GET_CONTEXT, &target);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    if (!wit_arch_context_supported()) {
        return WIT_STATUS_UNSUPPORTED;
    }
    const WitArchFrame *saved = owned_context(process, target);
    WitThreadContext snapshot;
    wit_user_context_snapshot(&snapshot, target, saved);
    // IF remains clear through snapshot, whole destination validation and copy.
    return wit_user_copy_to(&process->Space, address, (const WitU8 *)&snapshot, sizeof(snapshot))
        ? WIT_STATUS_OK
        : WIT_STATUS_BAD_ADDRESS;
}

WitU64 wit_user_context_validate(
    WitUserProcess *process, WitUserThread *target, const WitThreadContext *input, int restoring)
{
    if (input->Version != WIT_THREAD_CONTEXT_VERSION) {
        return WIT_STATUS_UNSUPPORTED;
    }
    if (input->Size != sizeof(*input) ||
        input->ThreadId != target->Handle ||
        input->StackLow != target->StackBottom ||
        input->StackHigh != target->StackTop ||
        input->Reserved ||
        input->State != (restoring ? WIT_THREAD_CONTEXT_RUNNING : WIT_THREAD_CONTEXT_READY) ||
        input->Flags !=
            (wit_arch_context_profile() |
                (restoring ? 0 : WIT_THREAD_CONTEXT_SUSPENDED) |
                (target->Exception.Token ? WIT_THREAD_CONTEXT_EXCEPTION_ACTIVE : 0)) ||
        input->SuspendCount != target->SuspendCount ||
        !wit_arch_context_registers_valid(input)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    const WitU64 sp = wit_arch_context_sp(input);
    if (sp < target->StackBottom ||
        sp >= target->StackTop ||
        !wit_user_space_physical(&process->Space, sp, 1, 0) ||
        !wit_user_space_physical(&process->Space, wit_arch_context_pc(input), 0, 1)) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    return wit_arch_context_state_valid(input) ? WIT_STATUS_OK : WIT_STATUS_INVALID_ARGUMENT;
}

WitU64 wit_user_thread_context_set(WitUserProcess *process, WitU64 reference, WitU64 address, WitU64 size)
{
    WitUserThread *target = 0;
    if (size != sizeof(WitThreadContext)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    WitU64 status = wit_user_reference_target(process, reference, WIT_RIGHT_SET_CONTEXT, &target);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    if (target == &process->Threads[process->CurrentThread] ||
        !target->SuspendCount ||
        target->State != WitThreadReady ||
        target->WaitKind != WitWaitNone) {
        return WIT_STATUS_BUSY;
    }
    if (target->Exception.Token || wit_user_stack_leased(process, target->Handle, 0)) {
        return WIT_STATUS_BUSY;
    }
    if (!wit_arch_context_supported()) {
        return WIT_STATUS_UNSUPPORTED;
    }
    WitThreadContext input;
    if (!wit_user_copy_from(&process->Space, address, (WitU8 *)&input, sizeof(input))) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    status = wit_user_context_validate(process, target, &input, 0);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    wit_arch_context_apply(owned_context(process, target), &input);
    return WIT_STATUS_OK;
}

WitU64 wit_user_thread_context_restore(WitUserProcess *process, WitU64 address, WitU64 size, WitU64 version)
{
    WitUserThread *target = &process->Threads[process->CurrentThread];
    if (version != WIT_THREAD_CONTEXT_VERSION) {
        return WIT_STATUS_UNSUPPORTED;
    }
    if (size != sizeof(WitThreadContext)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (target->SuspendCount || target->State != WitThreadRunning || target->WaitKind != WitWaitNone) {
        return WIT_STATUS_BUSY;
    }
    if (target->Exception.Token ||
        wit_user_stack_leased(process, target->Handle, 0) ||
        wit_user_stack_leases_owned(process, target->Handle)) {
        return WIT_STATUS_BUSY;
    }
    if (!wit_arch_context_supported()) {
        return WIT_STATUS_UNSUPPORTED;
    }
    WitThreadContext input;
    if (!wit_user_copy_from(&process->Space, address, (WitU8 *)&input, sizeof(input))) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    const WitU64 status = wit_user_context_validate(process, target, &input, 1);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    wit_arch_context_apply(owned_context(process, target), &input);
    return WIT_STATUS_OK;
}

WitU64 wit_user_cpu_context_query(WitUserProcess *process, WitU64 address, WitU64 size, WitU64 version)
{
    WitCpuContextInfo info = {0};
    if (version != WIT_CPU_CONTEXT_VERSION) {
        return WIT_STATUS_UNSUPPORTED;
    }
    if (size != sizeof(info)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (!wit_arch_context_supported() || wit_processors_online() != 1) {
        return WIT_STATUS_UNSUPPORTED;
    }
    wit_arch_cpu_context_describe(&info, process->Threads[process->CurrentThread].Context);
    return wit_user_copy_to(&process->Space, address, (const WitU8 *)&info, sizeof(info)) ? WIT_STATUS_OK
                                                                                          : WIT_STATUS_BAD_ADDRESS;
}
