#include "user.h"
static WitU64 next_exception_token = 1;

void wit_user_exception_clear(WitUserThread *thread)
{
    for (WitU32 i = 0; i < sizeof(thread->Exception); ++i) {
        ((WitU8 *)&thread->Exception)[i] = 0;
    }
    for (WitU32 i = 0; i < sizeof(thread->ExceptionParents); ++i) {
        ((WitU8 *)thread->ExceptionParents)[i] = 0;
    }
    thread->ExceptionDepth = 0;
}

void wit_user_exception_initialize(WitUserProcess *p)
{
    p->ExceptionCallback = 0;
    for (WitU32 i = 0; i < WIT_USER_THREAD_CAPACITY; ++i) {
        wit_user_exception_clear(&p->Threads[i]);
    }
}

WitU64 wit_user_exception_register(WitUserProcess *p, WitU64 callback, WitU64 version, WitU64 flags)
{
    if (version != WIT_EXCEPTION_VERSION) {
        return WIT_STATUS_UNSUPPORTED;
    }
    if (flags) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    /* A delivery in flight or an activation waiting for its delivery binds the registered callback. */
    for (WitU32 i = 0; i < WIT_USER_THREAD_CAPACITY; ++i) {
        if (p->Threads[i].Exception.Token || p->Threads[i].ActivationCount) {
            return WIT_STATUS_BUSY;
        }
    }
    if (callback &&
        (!wit_user_space_physical(&p->Space, callback, 0, 1) || wit_user_space_physical(&p->Space, callback, 1, 0))) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    p->ExceptionCallback = callback;
    return WIT_STATUS_OK;
}

WitU64 wit_user_exception_query(WitUserProcess *p, WitU64 token, WitU64 output, WitU64 size)
{
    const WitUserExceptionInfo *info = &p->Threads[p->CurrentThread].Exception;
    if (size != sizeof(*info)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (!token || info->Token != token) {
        return WIT_STATUS_BAD_HANDLE;
    }
    return wit_user_copy_to(&p->Space, output, (const WitU8 *)info, sizeof(*info)) ? WIT_STATUS_OK
                                                                                   : WIT_STATUS_BAD_ADDRESS;
}

/* CONTINUE: resumes the validated context of the current delivery and retires the record together with the
 * abandoned ancestors through RetireThroughToken (the current token retires the current record alone). */
WitU64 wit_user_exception_continue(WitUserProcess *p, WitU64 token, WitU64 input, WitU64 size)
{
    WitUserThread *t = &p->Threads[p->CurrentThread];
    if (size != sizeof(WitUserExceptionTransfer)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (!token || t->Exception.Token != token) {
        return WIT_STATUS_BAD_HANDLE;
    }
    if (t->State != WitThreadRunning ||
        t->SuspendCount ||
        t->WaitKind != WitWaitNone ||
        wit_user_stack_leased(p, t->Handle, 0) ||
        wit_user_stack_leases_owned(p, t->Handle)) {
        return WIT_STATUS_BUSY;
    }
    if (!wit_arch_context_supported()) {
        return WIT_STATUS_UNSUPPORTED;
    }
    WitUserExceptionTransfer request;
    if (!wit_user_copy_from(&p->Space, input, (WitU8 *)&request, sizeof(request))) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    if (request.Version != WIT_EXCEPTION_TRANSFER_VERSION) {
        return WIT_STATUS_UNSUPPORTED;
    }
    if (request.Size != sizeof(request)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    WitU32 retire = t->ExceptionDepth;
    if (request.RetireThroughToken != token) {
        for (retire = 0; retire < t->ExceptionDepth; ++retire) {
            if (t->ExceptionParents[retire].Token == request.RetireThroughToken) {
                break;
            }
        }
        if (retire == t->ExceptionDepth) {
            return WIT_STATUS_BAD_HANDLE;
        }
    }
    const WitU64 status = wit_user_context_validate(p, t, &request.Context, 1);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    /* IF is clear: commit registers and retire exactly the selected suffix only
     * after validating the full copied request and current-thread token chain. */
    wit_arch_context_apply(t->Context, &request.Context);
    ++p->ExceptionContinuations;
    if (!retire) {
        wit_user_exception_clear(t);
    } else {
        t->Exception = t->ExceptionParents[retire - 1];
        for (WitU32 i = retire - 1; i < t->ExceptionDepth; ++i) {
            for (WitU32 j = 0; j < sizeof(t->ExceptionParents[i]); ++j) {
                ((WitU8 *)&t->ExceptionParents[i])[j] = 0;
            }
        }
        t->ExceptionDepth = retire - 1;
    }
    return WIT_STATUS_OK;
}

WitU64 wit_user_exception_begin(WitUserProcess *p, WitU64 input, WitU64 size, WitU64 code, WitU64 *result)
{
    *result = 0;
    WitUserThread *t = &p->Threads[p->CurrentThread];
    if (size != sizeof(WitThreadContext) || code > 0xFFFFFFFFULL) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (t->State != WitThreadRunning ||
        t->SuspendCount ||
        t->WaitKind != WitWaitNone ||
        wit_user_stack_leases_owned(p, t->Handle)) {
        return WIT_STATUS_BUSY;
    }
    if (!next_exception_token || t->ExceptionDepth + (t->Exception.Token ? 1U : 0U) >= WIT_EXCEPTION_MAX_DEPTH) {
        return WIT_STATUS_NO_MEMORY;
    }
    if (!wit_arch_context_supported()) {
        return WIT_STATUS_UNSUPPORTED;
    }
    WitThreadContext context;
    if (!wit_user_copy_from(&p->Space, input, (WitU8 *)&context, sizeof(context))) {
        return WIT_STATUS_BAD_ADDRESS;
    }
    const WitU64 status = wit_user_context_validate(p, t, &context, 1);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    WitUserExceptionInfo info = {0};
    info.Version = WIT_EXCEPTION_VERSION;
    info.Size = sizeof(info);
    info.Token = next_exception_token++;
    info.Vector = WIT_EXCEPTION_SOFTWARE_VECTOR;
    info.Error = code;
    wit_arch_exception_record_software(&info, &context);
    info.Context = context;
    info.Context.Flags |= WIT_THREAD_CONTEXT_EXCEPTION_ACTIVE;
    if (t->Exception.Token) {
        t->ExceptionParents[t->ExceptionDepth++] = t->Exception;
    }
    t->Exception = info;
    *result = info.Token;
    return WIT_STATUS_OK;
}

/* The one entry into the fault callback for faults and activations: the callback frame goes below the interrupted
 * stack pointer with WIT_EXCEPTION_STACK_MINIMUM of room, the record is published on the thread and the frame enters
 * the callback with the token, the vector and the address. Everything is validated before any byte is written. */
int wit_user_exception_enter(WitUserProcess *p, WitUserThread *t, WitArchFrame *frame, WitUserExceptionInfo *info)
{
    if (!p->ExceptionCallback || t->Exception.Token || !next_exception_token || !wit_arch_context_supported()) {
        return 0;
    }
    const WitU64 sp = wit_arch_frame_sp(frame);
    if (!wit_arch_frame_returns_to_user(frame) || sp < t->StackBottom || sp >= t->StackTop) {
        return 0;
    }
    WitU32 callFrameBytes;
    const WitU64 callbackStack = wit_arch_callback_stack(sp, &callFrameBytes);
    static const WitU8 callFrame[64] = {0};
    if (callFrameBytes > sizeof(callFrame) || callbackStack < t->StackBottom + WIT_EXCEPTION_STACK_MINIMUM) {
        return 0;
    }
    if (!wit_user_buffer_writable(
            &p->Space, callbackStack - WIT_EXCEPTION_STACK_MINIMUM, WIT_EXCEPTION_STACK_MINIMUM + callFrameBytes) ||
        !wit_user_space_physical(&p->Space, p->ExceptionCallback, 0, 1)) {
        return 0;
    }
    info->Token = next_exception_token;
    if (!wit_user_copy_to(&p->Space, callbackStack, callFrame, callFrameBytes)) {
        return 0;
    }
    t->Exception = *info;
    ++next_exception_token;
    wit_arch_frame_enter_callback(frame, p->ExceptionCallback, callbackStack, info->Token, info->Vector, info->Address);
    return 1;
}

int wit_user_exception_deliver(WitUserProcess *p, WitArchFrame *frame, WitU64 vector, WitU64 error, WitU64 address)
{
    WitUserThread *t = &p->Threads[p->CurrentThread];
    if (t->State != WitThreadRunning ||
        t->SuspendCount ||
        t->WaitKind != WitWaitNone ||
        !wit_arch_exception_deliverable(vector) ||
        !wit_arch_context_supported()) {
        return 0;
    }
    WitUserExceptionInfo info = {0};
    info.Version = WIT_EXCEPTION_VERSION;
    info.Size = sizeof(info);
    info.Vector = vector;
    info.Error = error;
    wit_user_context_snapshot(&info.Context, t, frame);
    info.Context.Flags |= WIT_THREAD_CONTEXT_EXCEPTION_ACTIVE;
    wit_arch_exception_record(&info, frame, address);
    if (!wit_user_exception_enter(p, t, frame, &info)) {
        return 0;
    }
    switch (wit_arch_exception_kind(vector, error, address)) {
    case WitArchExceptionNullWrite:
        ++p->HardwareNullWrites;
        break;
    case WitArchExceptionNullRead:
        ++p->HardwareNullReads;
        break;
    case WitArchExceptionDivide:
        ++p->HardwareDivideFaults;
        break;
    case WitArchExceptionIllegal:
        ++p->HardwareIllegalFaults;
        break;
    default:
        break;
    }
    return 1;
}
