#include "context_conversion.witos.h"
#include "unwind_scope.witos.h"
#include "unwind_validation.witos.h"
#include "seh_scope.witos.h"
#include "security_handler.witos.h"
#include "exception_classification.h"
#if defined(WITOS_DYNAMIC_CODE)
#include "function_tables_guest.witos.h"
extern "C" EXCEPTION_DISPOSITION wit_native_handler_invoke(
    void *, PEXCEPTION_ROUTINE, EXCEPTION_RECORD *, void *, CONTEXT *, DISPATCHER_CONTEXT *);
#endif
extern "C" EXCEPTION_DISPOSITION __cdecl __GSHandlerCheck_SEH(
    EXCEPTION_RECORD *, void *, CONTEXT *, DISPATCHER_CONTEXT *);
extern "C" void wit_native_exception_entry(WitU64, WitU64, WitU64);
extern "C" WitU64 wit_native_seh_invoke(void *, WitU64, WitU64, WitU64);
extern "C" EXCEPTION_DISPOSITION __cdecl __C_specific_handler(
    EXCEPTION_RECORD *, void *, CONTEXT *, DISPATCHER_CONTEXT *);

namespace {
constexpr unsigned Capacity = 8;

struct Entry {
    WitU64 Token;
    PVECTORED_EXCEPTION_HANDLER Handler;
};

static Entry handlers[Capacity];
static WitU64 order[Capacity], nextToken = 1, gateOwner;
static unsigned count;
static volatile WitU32 gate;
static bool installed;

bool current(WitUserThreadInfo &thread)
{
    return wit_native_thread_info(&thread) && thread.CompilerTls;
}

bool enter(WitU64 owner)
{
    while (!wit_native_try_lock(&gate)) {
        if (gateOwner == owner) {
            return false;
        }
        wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr);
    }
    gateOwner = owner;
    return true;
}

void leave()
{
    gateOwner = 0;
    wit_native_unlock(&gate);
}

Entry *find(WitU64 token)
{
    for (auto &entry : handlers) {
        if (entry.Token == token) {
            return &entry;
        }
    }
    return nullptr;
}

bool code(WitU64 address)
{
    if (wit_native_image_range(wit_native_process_image(), address, 1, WIT_IMAGE_INFO_READ | WIT_IMAGE_INFO_EXECUTE,
            WIT_IMAGE_INFO_WRITE, 1)) {
        return true;
    }
#if defined(WITOS_DYNAMIC_CODE)
    return wit_coreclr_code_registered(address, 1) && wit_coreclr_unwind_read(address, 1, true) != nullptr;
#else
    return false;
#endif
}

[[noreturn]] void reject(WitU64 token)
{
    wit_native_call(WIT_CALL_EXCEPTION_REJECT, token, 0, 0, nullptr);
    wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}

LONG vectored(EXCEPTION_POINTERS &pointers, WitU64 owner)
{
    const auto record = pointers.ExceptionRecord;
    const auto context = pointers.ContextRecord;
    WitU64 selected[Capacity];
    unsigned total;
    if (!enter(owner)) {
        return EXCEPTION_CONTINUE_SEARCH;
    }
    total = count;
    for (unsigned i = 0; i < total; ++i) {
        selected[i] = order[i];
    }
    leave();
    for (unsigned i = 0; i < total; ++i) {
        if (!enter(owner)) {
            return EXCEPTION_CONTINUE_SEARCH;
        }
        const Entry *entry = find(selected[i]);
        const auto callback = entry ? entry->Handler : nullptr;
        leave(); // No gate or reusable slot is retained while calling user code.
        if (!callback) {
            continue;
        }
        if (!code((WitU64)callback)) {
            return 1; // Invalid disposition: fail closed at the dispatcher.
        }
        const LONG result = callback(&pointers);
        if (pointers.ExceptionRecord != record ||
            pointers.ContextRecord != context ||
            record->NumberParameters > EXCEPTION_MAXIMUM_PARAMETERS) {
            return 1;
        }
        if (result != EXCEPTION_CONTINUE_SEARCH) {
            return result;
        }
    }
    return EXCEPTION_CONTINUE_SEARCH;
}

PRUNTIME_FUNCTION function(const WitUserImageInfo *image, WitU64 pc, DWORD64 *base)
{
    *base = image->Base;
    if (pc < image->Base || pc - image->Base >= image->ImageSize) {
#if defined(WITOS_DYNAMIC_CODE)
        return wit_coreclr_lookup_function_entry(pc, base, nullptr);
#else
        return nullptr;
#endif
    }
    auto entries = (PRUNTIME_FUNCTION)(image->Base + image->UnwindRva);
    unsigned low = 0, high = image->UnwindSize / sizeof(*entries);
    while (low < high) {
        const unsigned middle = low + (high - low) / 2;
        const auto &entry = entries[middle];
        if (pc - image->Base < entry.BeginAddress) {
            high = middle;
        } else if (pc - image->Base >= entry.EndAddress) {
            low = middle + 1;
        } else {
            return &entries[middle];
        }
    }
    return nullptr;
}

struct SehDispatch {
    SehDispatch *Previous;
    WitUserExceptionInfo *Info;
    const WitCpuContextInfo *Cpu;
    CONTEXT *Initial;
    WitNativeUnwindScope *Scope;
    WitUnwindStackRange Bounds;
    WitU64 Owner;
    bool CallSite;
    bool Unwinding;
    DISPATCHER_CONTEXT *Dispatcher;
    const CONTEXT *Before;
    SehDispatch *Bridge;
    WitU64 RetireThrough;
    CONTEXT *UnwindOutput;
};

static __declspec(thread) SehDispatch *seh;

struct SehGuard {
    SehDispatch State;

    SehGuard(WitUserExceptionInfo &info, const WitCpuContextInfo &cpu, CONTEXT &initial, WitNativeUnwindScope &scope,
        WitUnwindStackRange bounds, WitU64 owner, bool callSite)
        : State{
              seh, &info, &cpu, &initial, &scope, bounds, owner, callSite, false, nullptr, nullptr, nullptr, 0, nullptr}
    {
        seh = &State;
    }

    ~SehGuard()
    {
        seh = State.Previous;
    }
};

void before_funclet(void *value)
{
    auto state = (SehDispatch *)value;
    WitUserThreadInfo owner;
    if (!current(owner) || owner.ThreadId != state->Owner || seh != state || state->Scope->Close() != WIT_STATUS_OK) {
        reject(state->Info->Token);
    }
    // Only this executing thread's stack is exposed here. Its backing cannot
    // be reaped during the callback, and pending state blocks foreign mutation.
}

void after_funclet(void *value)
{
    auto state = (SehDispatch *)value;
    WitUserExceptionInfo pending;
    if (seh != state ||
        wit_native_call(WIT_CALL_EXCEPTION_QUERY, state->Info->Token, (WitU64)&pending, sizeof(pending), nullptr) !=
            WIT_STATUS_OK ||
        pending.Context.ThreadId != state->Owner ||
        state->Scope->ReopenCurrent() != WIT_STATUS_OK) {
        reject(state->Info->Token);
    }
}
#if defined(WITOS_DYNAMIC_CODE)
// CoreCLR can restore a redirected context using collided unwind without an
// internal handler bridge. Copy the whole context before accepting the tuple;
// revalidate its entry/frame on the next iteration, under the stack lease.
struct DispatcherRestart {
    bool Pending = false;
    DISPATCHER_CONTEXT Value = {};

    void Capture(SehDispatch &state, const DISPATCHER_CONTEXT &supplied, WitU64 minimum, CONTEXT &working)
    {
        if (!supplied.ContextRecord ||
            !wit_coreclr_unwind_read((WitU64)supplied.ContextRecord, sizeof(CONTEXT), false)) {
            reject(state.Info->Token);
        }
        const CONTEXT context = *supplied.ContextRecord;
        if (!WitContext::flags(&context) ||
            context.Rsp < minimum ||
            context.Rsp < state.Bounds.Low ||
            context.Rsp >= state.Bounds.High ||
            !code(supplied.ControlPc) ||
            !code((WitU64)supplied.LanguageHandler)) {
            reject(state.Info->Token);
        }
        WitThreadContext checked = state.Info->Context;
        if (!WitContext::decode(context, checked, *state.Cpu, true)) {
            reject(state.Info->Token);
        }
        Value = supplied;
        Value.ContextRecord = nullptr;
        Value.HistoryTable = nullptr;
        working = context;
        working.Rip = supplied.ControlPc;
        Pending = true;
    }

    void Apply(SehDispatch &state, DWORD64 base, PRUNTIME_FUNCTION entry, DWORD64 frame, PEXCEPTION_ROUTINE &handler,
        void *&data, DWORD &cursor)
    {
        if (!Pending) {
            return;
        }
        if (base != Value.ImageBase || entry != Value.FunctionEntry || frame != Value.EstablisherFrame) {
            reject(state.Info->Token);
        }
        handler = Value.LanguageHandler;
        data = Value.HandlerData;
        cursor = Value.ScopeIndex;
        Pending = false;
    }
};
#endif
[[noreturn]] void unwind_target(EXCEPTION_RECORD &exception, WitU64 targetFrame, WitU64 targetIp, WitU64 returnValue)
{
    SehDispatch *state = seh;
    if (!state ||
        state->Unwinding ||
        targetFrame < state->Bounds.Low ||
        targetFrame >= state->Bounds.High ||
        !code(targetIp)) {
        reject(state ? state->Info->Token : 0);
    }
    state->Unwinding = true;
    const auto image = wit_native_process_image();
    CONTEXT working = *state->Initial;
    bool callSite = state->CallSite;
    const DWORD originalFlags = exception.ExceptionFlags;
    DWORD resumeCursor = 0;
    bool collided = false;
#if defined(WITOS_DYNAMIC_CODE)
    DispatcherRestart restart;
#endif
    for (unsigned depth = 0; depth < 128 && working.Rip; ++depth) {
        const WitU64 pc = working.Rip - (callSite ? 1U : 0U), sp = working.Rsp;
        if (sp < state->Bounds.Low || sp >= state->Bounds.High || !code(pc)) {
            reject(state->Info->Token);
        }
        DWORD64 frameBase = 0;
        const auto entry = function(image, pc, &frameBase);
        if (!entry) {
#if defined(WITOS_DYNAMIC_CODE)
            if (restart.Pending) {
                reject(seh->Info->Token);
            }
#endif
            if (!wit_unwind_read_stack(&state->Bounds, sp, &working.Rip, 8)) {
                reject(state->Info->Token);
            }
            working.Rsp += 8;
        } else {
            const CONTEXT before = working;
            void *data = nullptr;
            DWORD64 frame = 0;
            auto handler = RtlVirtualUnwind(UNW_FLAG_UHANDLER, frameBase, pc, entry, &working, &data, &frame, nullptr);
#if defined(WITOS_DYNAMIC_CODE)
            restart.Apply(*state, frameBase, entry, frame, handler, data, resumeCursor);
#endif
            if (frame > targetFrame) {
                reject(state->Info->Token);
            }
            const bool target = frame == targetFrame;
            exception.ExceptionFlags = originalFlags |
                EXCEPTION_UNWINDING |
                (target ? EXCEPTION_TARGET_UNWIND : 0) |
                (collided ? EXCEPTION_COLLIDED_UNWIND : 0);
            if (handler) {
                DISPATCHER_CONTEXT dispatcher = {};
                dispatcher.ControlPc = pc;
                dispatcher.ImageBase = frameBase;
                dispatcher.FunctionEntry = entry;
                dispatcher.EstablisherFrame = frame;
                dispatcher.ContextRecord = &working;
                dispatcher.LanguageHandler = handler;
                dispatcher.HandlerData = data;
                dispatcher.TargetIp = targetIp;
                dispatcher.ScopeIndex = resumeCursor;
                state->Dispatcher = &dispatcher;
                state->Before = &before;
                state->Bridge = nullptr;
                EXCEPTION_DISPOSITION result;
#if defined(WITOS_DYNAMIC_CODE)
                if (frameBase != image->Base) {
                    before_funclet(state);
                    result =
                        wit_native_handler_invoke(state, handler, &exception, (void *)frame, &working, &dispatcher);
                    after_funclet(state);
                } else
#endif
                    result = handler(&exception, (void *)frame, &working, &dispatcher);
                state->Dispatcher = nullptr;
                state->Before = nullptr;
                if (result == ExceptionCollidedUnwind) {
                    const auto bridge = state->Bridge;
#if defined(WITOS_DYNAMIC_CODE)
                    if (!bridge) {
                        restart.Capture(*state, dispatcher, sp, working);
                        callSite = false;
                        collided = true;
                        continue;
                    }
#endif
                    if (!bridge || !dispatcher.ContextRecord || dispatcher.ContextRecord->Rsp <= sp) {
                        reject(state->Info->Token);
                    }
                    working = *dispatcher.ContextRecord;
                    working.Rip = dispatcher.ControlPc;
                    resumeCursor = dispatcher.ScopeIndex;
                    state->RetireThrough = bridge->RetireThrough ? bridge->RetireThrough : bridge->Info->Token;
                    state->Previous = bridge->Previous;
                    callSite = false;
                    collided = true;
                    continue;
                }
                if (result != ExceptionContinueSearch) {
                    reject(state->Info->Token);
                }
            }
            if (target) {
                // Enter the compiler landing pad in the selected frame, not its
                // caller produced by VirtualUnwind. Keep restored nonvolatiles.
                CONTEXT destination = before;
                destination.Rip = targetIp;
                destination.Rsp = frame;
                destination.Rax = returnValue;
                destination.ContextFlags =
                    WitContext::complete | CONTEXT_EXCEPTION_REPORTING | CONTEXT_EXCEPTION_ACTIVE;
                if (!WitContext::decode(destination, state->Info->Context, *state->Cpu, true)) {
                    reject(state->Info->Token);
                }
#if defined(WITOS_DYNAMIC_CODE)
                if (state->UnwindOutput) {
                    WitCodeMemoryRequest request = {WIT_CODE_MEMORY_VERSION, sizeof(request), WIT_CODE_VALIDATE, 3,
                        (WitU64)state->UnwindOutput, 0, sizeof(CONTEXT), 0, 0, 0};
                    if (wit_native_call(WIT_CALL_CODE_MEMORY, (WitU64)&request, sizeof(request), 0, nullptr) !=
                        WIT_STATUS_OK) {
                        reject(state->Info->Token);
                    }
                    *state->UnwindOutput = destination;
                }
#endif
                const WitU64 token = state->Info->Token;
                if (state->Scope->Close() != WIT_STATUS_OK) {
                    reject(token);
                }
                seh = state->Previous;
                if (state->RetireThrough) {
                    WitUserExceptionTransfer transfer = {};
                    transfer.Version = WIT_EXCEPTION_TRANSFER_VERSION;
                    transfer.Size = sizeof(transfer);
                    transfer.RetireThroughToken = state->RetireThrough;
                    transfer.Context = state->Info->Context;
                    wit_native_call(WIT_CALL_EXCEPTION_UNWIND, token, (WitU64)&transfer, sizeof(transfer), nullptr);
                } else {
                    wit_native_call(WIT_CALL_EXCEPTION_CONTINUE, token, (WitU64)&state->Info->Context,
                        sizeof(state->Info->Context), nullptr);
                }
                reject(token);
            }
        }
        if (working.Rsp <= sp || working.Rsp > state->Bounds.High) {
            reject(state->Info->Token);
        }
        callSite = true;
        resumeCursor = 0;
        collided = false;
    }
    reject(state->Info->Token);
}

// Search real native frames. Language-specific handlers retain their genuine
// dependencies (notably __C_specific_handler); no successful SEH substitute.
LONG frames(EXCEPTION_RECORD &record, CONTEXT &original, WitUserExceptionInfo &info, const WitCpuContextInfo &cpu,
    WitU64 owner, bool callSite)
{
    const WitU64 token = info.Token;
    const auto image = wit_native_process_image();
    if (!image || wit_unwind_validate_image(image) != WitUnwindValid) {
        reject(token);
    }
    WitNativeUnwindScope scope(WIT_THREAD_REFERENCE_CURRENT);
    if (scope.Status() != WIT_STATUS_OK) {
        reject(token);
    }
    WitStackLeaseInfo lease;
    if (WitNativeUnwindScope::Current(original.Rsp, &lease) != WIT_STATUS_OK) {
        reject(token);
    }
    const WitUnwindStackRange bounds = {lease.StackLow, lease.StackHigh};
    SehGuard guard(info, cpu, original, scope, bounds, owner, callSite);
    CONTEXT working = original;
    WitU64 nestedBoundary = 0;
#if defined(WITOS_DYNAMIC_CODE)
    DispatcherRestart restart;
#endif
    for (unsigned depth = 0; depth < 128 && working.Rip; ++depth) {
        const WitU64 pc = working.Rip - (callSite ? 1U : 0U), sp = working.Rsp;
        if (working.Rsp < bounds.Low || working.Rsp >= bounds.High || !code(pc)) {
            break;
        }
        if (nestedBoundary && sp > nestedBoundary) {
            record.ExceptionFlags &= ~EXCEPTION_NESTED_CALL;
            nestedBoundary = 0;
        }
        DWORD64 frameBase = 0;
        const auto entry = function(image, pc, &frameBase);
        if (!entry) {
#if defined(WITOS_DYNAMIC_CODE)
            if (restart.Pending) {
                reject(seh->Info->Token);
            }
#endif
            if (!wit_unwind_read_stack(&bounds, sp, &working.Rip, 8)) {
                reject(token);
            }
            working.Rsp += 8;
        } else {
            const CONTEXT before = working;
            void *data = nullptr;
            DWORD64 frame = 0;
            auto handler = RtlVirtualUnwind(UNW_FLAG_EHANDLER, frameBase, pc, entry, &working, &data, &frame, nullptr);
            DWORD cursor = 0;
#if defined(WITOS_DYNAMIC_CODE)
            restart.Apply(guard.State, frameBase, entry, frame, handler, data, cursor);
#endif
            if (handler) {
                DISPATCHER_CONTEXT dispatcher = {};
                dispatcher.ControlPc = pc;
                dispatcher.ImageBase = frameBase;
                dispatcher.FunctionEntry = entry;
                dispatcher.EstablisherFrame = frame;
                dispatcher.ContextRecord = &working;
                dispatcher.LanguageHandler = handler;
                dispatcher.HandlerData = data;
                dispatcher.ScopeIndex = cursor;
                guard.State.Dispatcher = &dispatcher;
                guard.State.Before = &before;
                guard.State.Bridge = nullptr;
                EXCEPTION_DISPOSITION result;
#if defined(WITOS_DYNAMIC_CODE)
                if (frameBase != image->Base) {
                    before_funclet(&guard.State);
                    result = wit_native_handler_invoke(
                        &guard.State, handler, &record, (void *)frame, &original, &dispatcher);
                    after_funclet(&guard.State);
                } else
#endif
                    result = handler(&record, (void *)frame, &original, &dispatcher);
                guard.State.Dispatcher = nullptr;
                guard.State.Before = nullptr;
                if (result == ExceptionContinueExecution) {
                    return EXCEPTION_CONTINUE_EXECUTION;
                }
#if defined(WITOS_DYNAMIC_CODE)
                if (result == ExceptionCollidedUnwind && !guard.State.Bridge) {
                    restart.Capture(guard.State, dispatcher, sp, working);
                    callSite = false;
                    continue;
                }
#endif
                if (result == ExceptionNestedException) {
                    if (!guard.State.Bridge || !dispatcher.ContextRecord || dispatcher.ContextRecord->Rsp <= sp) {
                        reject(token);
                    }
                    working = *dispatcher.ContextRecord;
                    working.Rip = dispatcher.ControlPc;
                    callSite = false;
                    if (dispatcher.EstablisherFrame > nestedBoundary) {
                        nestedBoundary = dispatcher.EstablisherFrame;
                    }
                    record.ExceptionFlags |= EXCEPTION_NESTED_CALL;
                    continue;
                }
                if (result != ExceptionContinueSearch) {
                    reject(token);
                }
            }
        }
        if (working.Rsp <= sp || working.Rsp > bounds.High) {
            reject(token);
        }
        callSite = true;
    }
    return EXCEPTION_CONTINUE_SEARCH;
}

[[noreturn]] void dispatch(EXCEPTION_RECORD &exception, CONTEXT &context, WitUserExceptionInfo &info,
    const WitCpuContextInfo &cpu, WitU64 owner, bool secondary = false)
{
    const bool noncontinuable = (exception.ExceptionFlags & EXCEPTION_NONCONTINUABLE) != 0;
    EXCEPTION_POINTERS pointers = {&exception, &context};
    LONG result = vectored(pointers, owner);
    const bool frameSearch = result == EXCEPTION_CONTINUE_SEARCH;
    if (frameSearch) {
        result = frames(exception, context, info, cpu, owner, info.Vector == WIT_EXCEPTION_SOFTWARE_VECTOR);
    }
    if (frameSearch && result == EXCEPTION_CONTINUE_EXECUTION && noncontinuable) {
        if (secondary) {
            reject(info.Token);
        }
        EXCEPTION_RECORD failure = {};
        failure.ExceptionCode = EXCEPTION_NONCONTINUABLE_EXCEPTION;
        failure.ExceptionFlags = EXCEPTION_NONCONTINUABLE | 0x80U;
        failure.ExceptionRecord = &exception;
        failure.ExceptionAddress = exception.ExceptionAddress;
        dispatch(failure, context, info, cpu, owner, true);
    }
    if (result != EXCEPTION_CONTINUE_EXECUTION ||
        !WitContext::flags(&context) ||
        !WitContext::decode(context, info.Context, cpu, true)) {
        reject(info.Token);
    }
    wit_native_call(WIT_CALL_EXCEPTION_CONTINUE, info.Token, (WitU64)&info.Context, sizeof(info.Context), nullptr);
    reject(info.Token);
}

bool record(const WitUserExceptionInfo &info, EXCEPTION_RECORD &output)
{
    output = {};
    output.ExceptionAddress = (void *)info.Context.Rip;
    switch (info.Vector) {
    case 0:
        output.ExceptionCode = EXCEPTION_INT_DIVIDE_BY_ZERO;
        break;
    case 3:
        output.ExceptionCode = EXCEPTION_BREAKPOINT;
        if (info.Context.Rip) {
            output.ExceptionAddress = (void *)(info.Context.Rip - 1);
        }
        break;
    case 6:
        output.ExceptionCode = EXCEPTION_ILLEGAL_INSTRUCTION;
        break;
    case 14:
        output.ExceptionCode = EXCEPTION_ACCESS_VIOLATION;
        output.NumberParameters = 2;
        output.ExceptionInformation[0] = (info.Error & 16) ? 8 : (info.Error & 2) ? 1 : 0;
        output.ExceptionInformation[1] = info.Address;
        break;
    case 13: {
        WitU8 bytes[15];
        WitU32 size = 0;
        for (; size < sizeof(bytes); ++size) {
            const WitU64 address = info.Context.Rip + size;
            if (address < info.Context.Rip || !code(address)) {
                break;
            }
            bytes[size] = *(const WitU8 *)address;
        }
        const auto kind = wit_x64_classify_gp(bytes, size, info.Error);
        if (kind == WIT_GP_PRIVILEGED) {
            output.ExceptionCode = EXCEPTION_PRIV_INSTRUCTION;
            break;
        }
        if (kind != WIT_GP_ACCESS_UNKNOWN) {
            return false;
        }
        output.ExceptionCode = EXCEPTION_ACCESS_VIOLATION;
        output.NumberParameters = 2;
        // Windows reports an unknown address for these GP forms, including a
        // noncanonical write. Zero would falsely request managed null translation.
        output.ExceptionInformation[0] = 0;
        output.ExceptionInformation[1] = ~(ULONG_PTR)0;
        break;
    }
    default:
        return false;
    }
    return true;
}
}

extern "C" EXCEPTION_DISPOSITION wit_native_seh_bridge_handler(
    EXCEPTION_RECORD *exception, void *frame, CONTEXT *, DISPATCHER_CONTEXT *dispatcher)
{
    if (!seh || !exception || !dispatcher) {
        reject(seh ? seh->Info->Token : 0);
    }
    SehDispatch *paused = nullptr;
    if (!wit_unwind_read_stack(&seh->Bounds, (WitU64)frame + 32, &paused, sizeof(paused))) {
        reject(seh->Info->Token);
    }
    SehDispatch *found = seh->Previous;
    for (unsigned depth = 0; found && found != paused && depth < WIT_EXCEPTION_MAX_DEPTH; ++depth) {
        found = found->Previous;
    }
    if (!paused ||
        found != paused ||
        paused->Owner != seh->Owner ||
        paused->Scope->Status() != WIT_STATUS_CLOSED ||
        !paused->Dispatcher ||
        !paused->Before ||
        paused->Before->Rsp <= (WitU64)frame) {
        reject(seh->Info->Token);
    }
    *dispatcher = *paused->Dispatcher;
    dispatcher->ContextRecord = const_cast<CONTEXT *>(paused->Before);
    seh->Bridge = paused;
    return (exception->ExceptionFlags & (EXCEPTION_UNWINDING | EXCEPTION_EXIT_UNWIND)) ? ExceptionCollidedUnwind
                                                                                       : ExceptionNestedException;
}
#if defined(WITOS_DYNAMIC_CODE)
extern "C" EXCEPTION_DISPOSITION wit_native_handler_bridge(
    EXCEPTION_RECORD *exception, void *frame, CONTEXT *context, DISPATCHER_CONTEXT *dispatcher)
{
    const auto result = wit_native_seh_bridge_handler(exception, frame, context, dispatcher);
    const auto paused = seh->Bridge;
    if ((exception->ExceptionFlags & EXCEPTION_UNWINDING) && paused && !paused->Unwinding) {
        // A fresh target unwind initiated by a search handler starts from the
        // original exception frame, not the later frame that found the handler.
        // Otherwise intervening cleanup handlers would be skipped.
        dispatcher->ContextRecord = paused->Initial;
        dispatcher->ControlPc = paused->Initial->Rip - (paused->CallSite ? 1U : 0U);
        dispatcher->ScopeIndex = 0;
    }
    return result;
}
#endif
extern "C" EXCEPTION_DISPOSITION wit_native_c_specific_dispatch(
    EXCEPTION_RECORD *exception, void *frame, CONTEXT *context, DISPATCHER_CONTEXT *dispatcher, WitU64 expectedHandler)
{
    WitUserThreadInfo owner;
    if (!current(owner)) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    if (!seh ||
        seh->Owner != owner.ThreadId ||
        !exception ||
        !context ||
        !dispatcher ||
        (expectedHandler != (WitU64)&__C_specific_handler && expectedHandler != (WitU64)&__GSHandlerCheck_SEH)) {
        reject(seh ? seh->Info->Token : 0);
    }
    const auto image = wit_native_process_image();
    if (!image ||
        dispatcher->ImageBase != image->Base ||
        dispatcher->EstablisherFrame != (WitU64)frame ||
        (WitU64)dispatcher->FunctionEntry < image->Base ||
        (WitU64)dispatcher->FunctionEntry - image->Base >= image->ImageSize) {
        reject(seh->Info->Token);
    }
    const WitSehCallbacks callbacks = {seh, before_funclet, after_funclet, wit_native_seh_invoke};
    const WitU32 entry = (WitU32)((WitU64)dispatcher->FunctionEntry - image->Base);
    WitUnwindRecord binding;
    if (wit_unwind_validate_function(image, entry, &binding) != WitUnwindValid ||
        image->Base + binding.HandlerRva != expectedHandler ||
        (WitU64)dispatcher->LanguageHandler != expectedHandler) {
        reject(seh->Info->Token);
    }
    if (exception->ExceptionFlags & (EXCEPTION_UNWINDING | EXCEPTION_EXIT_UNWIND)) {
        if (!seh->Unwinding ||
            !wit_seh_terminate(image, entry, (WitU64)dispatcher->HandlerData, dispatcher->ControlPc, (WitU64)frame,
                &seh->Bounds, (exception->ExceptionFlags & EXCEPTION_TARGET_UNWIND) != 0, dispatcher->TargetIp,
                &dispatcher->ScopeIndex, &callbacks)) {
            reject(seh->Info->Token);
        }
        return ExceptionContinueSearch;
    }
    WitSehDecision decision;
    switch (wit_seh_search(image, entry, (WitU64)dispatcher->HandlerData, dispatcher->ControlPc, (WitU64)frame,
        &seh->Bounds, exception, context, &decision, &callbacks)) {
    case WitSehSearch:
        return ExceptionContinueSearch;
    case WitSehContinue:
        return ExceptionContinueExecution;
    case WitSehTarget:
        unwind_target(*exception, (WitU64)frame, image->Base + decision.Target, exception->ExceptionCode);
    default:
        reject(seh->Info->Token);
    }
}

extern "C" EXCEPTION_DISPOSITION __cdecl __C_specific_handler(
    EXCEPTION_RECORD *exception, void *frame, CONTEXT *context, DISPATCHER_CONTEXT *dispatcher)
{
    return wit_native_c_specific_dispatch(exception, frame, context, dispatcher, (WitU64)&__C_specific_handler);
}

extern "C" PVOID __cdecl wit_native_add_vectored_exception_handler(ULONG first, PVECTORED_EXCEPTION_HANDLER callback)
{
    WitUserThreadInfo thread;
    if (!current(thread) || !wit_native_process_image()) {
        SetLastError(ERROR_INVALID_STATE);
        return nullptr;
    }
    if (!callback || !code((WitU64)callback)) {
        SetLastError(ERROR_INVALID_PARAMETER);
        return nullptr;
    }
    if (!enter(thread.ThreadId)) {
        SetLastError(ERROR_BUSY);
        return nullptr;
    }
    if (count == Capacity || !nextToken) {
        leave();
        SetLastError(ERROR_NOT_ENOUGH_MEMORY);
        return nullptr;
    }
    if (!installed) {
        const auto status = wit_native_call(
            WIT_CALL_EXCEPTION_REGISTER, (WitU64)&wit_native_exception_entry, WIT_EXCEPTION_VERSION, 0, nullptr);
        if (status != WIT_STATUS_OK) {
            leave();
            wit_pal_set_status(status);
            return nullptr;
        }
        installed = true;
    }
    Entry *slot = nullptr;
    for (auto &item : handlers) {
        if (!item.Token) {
            slot = &item;
            break;
        }
    }
    if (!slot) {
        leave();
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    const WitU64 token = nextToken++;
    slot->Token = token;
    slot->Handler = callback;
    if (first) {
        for (unsigned i = count; i; --i) {
            order[i] = order[i - 1];
        }
        order[0] = token;
    } else {
        order[count] = token;
    }
    ++count;
    leave();
    return (PVOID)token;
}

extern "C" ULONG __cdecl wit_native_remove_vectored_exception_handler(PVOID handle)
{
    WitUserThreadInfo thread;
    if (!current(thread)) {
        SetLastError(ERROR_INVALID_STATE);
        return 0;
    }
    if (!handle) {
        return 0;
    }
    if (!enter(thread.ThreadId)) {
        SetLastError(ERROR_BUSY);
        return 0;
    }
    Entry *slot = find((WitU64)handle);
    if (!slot) {
        leave();
        return 0;
    }
    unsigned index = 0;
    while (index < count && order[index] != slot->Token) {
        ++index;
    }
    if (index == count) {
        leave();
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    for (unsigned i = index + 1; i < count; ++i) {
        order[i - 1] = order[i];
    }
    order[--count] = 0;
    *slot = {};
    leave();
    return 1;
}

extern "C" void wit_native_exception_entry(WitU64 token, WitU64 vector, WitU64 address)
{
    WitUserExceptionInfo info;
    WitUserThreadInfo owner;
    WitCpuContextInfo cpu;
    if (!current(owner) ||
        !WitContext::profile(cpu) ||
        wit_native_call(WIT_CALL_EXCEPTION_QUERY, token, (WitU64)&info, sizeof(info), nullptr) != WIT_STATUS_OK ||
        info.Version != WIT_EXCEPTION_VERSION ||
        info.Size != sizeof(info) ||
        info.Token != token ||
        info.Vector != vector ||
        info.Address != address ||
        info.Context.ThreadId != owner.ThreadId) {
        reject(token);
    }
    EXCEPTION_RECORD exception;
    CONTEXT context;
    if (!record(info, exception)) {
        reject(token);
    }
    WitContext::encode(info.Context, context);
    dispatch(exception, context, info, cpu, owner.ThreadId);
}

extern "C" void __cdecl wit_native_raise_exception(
    CONTEXT *captured, DWORD codeValue, DWORD flags, DWORD number, const ULONG_PTR *arguments)
{
    WitUserThreadInfo owner;
    WitCpuContextInfo cpu;
    WitUserExceptionInfo info = {};
    if (!captured ||
        !current(owner) ||
        !wit_native_process_image() ||
        !WitContext::profile(cpu) ||
        (flags & ~EXCEPTION_NONCONTINUABLE) ||
        (arguments && number > EXCEPTION_MAXIMUM_PARAMETERS)) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    EXCEPTION_RECORD exception = {};
    exception.ExceptionCode = codeValue;
    exception.ExceptionFlags = flags | 0x80U;
    exception.ExceptionAddress = (void *)captured->Rip;
    exception.NumberParameters = arguments ? number : 0;
    for (DWORD i = 0; i < exception.NumberParameters; ++i) {
        exception.ExceptionInformation[i] = arguments[i];
    }
    // Kernel metadata supplies identity, bounds and the current nesting state.
    if (wit_native_call(WIT_CALL_THREAD_CONTEXT_METADATA, WIT_THREAD_REFERENCE_CURRENT, (WitU64)&info.Context,
            sizeof(info.Context), nullptr) != WIT_STATUS_OK) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    captured->FltSave.MxCsr_Mask = cpu.MxcsrMask;
    captured->EFlags = (captured->EFlags & 0x200CD5U) | 0x202;
    if (!WitContext::flags(captured) || !WitContext::decode(*captured, info.Context, cpu, true)) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    WitU64 token = 0;
    if (wit_native_call(WIT_CALL_EXCEPTION_BEGIN, (WitU64)&info.Context, sizeof(info.Context), codeValue, &token) !=
            WIT_STATUS_OK ||
        wit_native_call(WIT_CALL_EXCEPTION_QUERY, token, (WitU64)&info, sizeof(info), nullptr) != WIT_STATUS_OK) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    captured->ContextFlags |= CONTEXT_EXCEPTION_REPORTING | CONTEXT_EXCEPTION_ACTIVE;
    dispatch(exception, *captured, info, cpu, owner.ThreadId);
}

extern "C" void __cdecl wit_native_local_unwind(CONTEXT *captured, WitU64 targetFrame, WitU64 targetIp)
{
    WitUserThreadInfo owner;
    WitCpuContextInfo cpu;
    WitUserExceptionInfo info = {};
    if (!captured ||
        !current(owner) ||
        !WitContext::profile(cpu) ||
        !code(targetIp) ||
        targetFrame < owner.StackLow ||
        targetFrame >= owner.StackHigh) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    if (wit_native_call(WIT_CALL_THREAD_CONTEXT_METADATA, WIT_THREAD_REFERENCE_CURRENT, (WitU64)&info.Context,
            sizeof(info.Context), nullptr) != WIT_STATUS_OK) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    captured->FltSave.MxCsr_Mask = cpu.MxcsrMask;
    captured->EFlags = (captured->EFlags & 0x200CD5U) | 0x202;
    if (!WitContext::flags(captured) || !WitContext::decode(*captured, info.Context, cpu, true)) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    WitU64 token = 0;
    if (wit_native_call(WIT_CALL_EXCEPTION_BEGIN, (WitU64)&info.Context, sizeof(info.Context), 0xC0000027U, &token) !=
            WIT_STATUS_OK ||
        wit_native_call(WIT_CALL_EXCEPTION_QUERY, token, (WitU64)&info, sizeof(info), nullptr) != WIT_STATUS_OK) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    captured->ContextFlags |= CONTEXT_EXCEPTION_REPORTING | CONTEXT_EXCEPTION_ACTIVE;
    WitNativeUnwindScope scope(WIT_THREAD_REFERENCE_CURRENT);
    if (scope.Status() != WIT_STATUS_OK) {
        reject(token);
    }
    WitStackLeaseInfo lease;
    if (WitNativeUnwindScope::Current(captured->Rsp, &lease) != WIT_STATUS_OK) {
        reject(token);
    }
    const WitUnwindStackRange bounds = {lease.StackLow, lease.StackHigh};
    SehGuard guard(info, cpu, *captured, scope, bounds, owner.ThreadId, true);
    EXCEPTION_RECORD exception = {};
    exception.ExceptionCode = 0xC0000027U;
    exception.ExceptionFlags = EXCEPTION_UNWINDING;
    exception.ExceptionAddress = (void *)captured->Rip;
    unwind_target(exception, targetFrame, targetIp, 0);
}

#if defined(WITOS_DYNAMIC_CODE)
extern "C" void __cdecl wit_native_rtl_unwind(CONTEXT *captured, WitU64 targetFrame, WitU64 targetIp,
    const EXCEPTION_RECORD *supplied, WitU64 returnValue, CONTEXT *output)
{
    WitUserThreadInfo owner;
    WitCpuContextInfo cpu;
    WitUserExceptionInfo info = {};
    if (!captured ||
        !targetFrame ||
        !current(owner) ||
        !WitContext::profile(cpu) ||
        !code(targetIp) ||
        targetFrame < owner.StackLow ||
        targetFrame >= owner.StackHigh) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    EXCEPTION_RECORD exception = {};
    if (supplied) {
        if (!wit_coreclr_unwind_read((DWORD64)supplied, sizeof(*supplied), false)) {
            wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
        }
        exception = *supplied;
        if (exception.NumberParameters > EXCEPTION_MAXIMUM_PARAMETERS) {
            wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
        }
    } else {
        exception.ExceptionCode = 0xC0000027U;
        exception.ExceptionAddress = (void *)captured->Rip;
    }
    if (output) {
        WitCodeMemoryRequest request = {WIT_CODE_MEMORY_VERSION, sizeof(request), WIT_CODE_VALIDATE, 3, (WitU64)output,
            0, sizeof(*output), 0, 0, 0};
        if (wit_native_call(WIT_CALL_CODE_MEMORY, (WitU64)&request, sizeof(request), 0, nullptr) != WIT_STATUS_OK) {
            wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
        }
    }
    if (wit_native_call(WIT_CALL_THREAD_CONTEXT_METADATA, WIT_THREAD_REFERENCE_CURRENT, (WitU64)&info.Context,
            sizeof(info.Context), nullptr) != WIT_STATUS_OK) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    captured->FltSave.MxCsr_Mask = cpu.MxcsrMask;
    captured->EFlags = (captured->EFlags & 0x200CD5U) | 0x202;
    if (!WitContext::flags(captured) || !WitContext::decode(*captured, info.Context, cpu, true)) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    WitU64 token = 0;
    if (wit_native_call(WIT_CALL_EXCEPTION_BEGIN, (WitU64)&info.Context, sizeof(info.Context), exception.ExceptionCode,
            &token) != WIT_STATUS_OK ||
        wit_native_call(WIT_CALL_EXCEPTION_QUERY, token, (WitU64)&info, sizeof(info), nullptr) != WIT_STATUS_OK) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    captured->ContextFlags |= CONTEXT_EXCEPTION_REPORTING | CONTEXT_EXCEPTION_ACTIVE;
    WitNativeUnwindScope scope(WIT_THREAD_REFERENCE_CURRENT);
    if (scope.Status() != WIT_STATUS_OK) {
        reject(token);
    }
    WitStackLeaseInfo lease;
    if (WitNativeUnwindScope::Current(captured->Rsp, &lease) != WIT_STATUS_OK) {
        reject(token);
    }
    const WitUnwindStackRange bounds = {lease.StackLow, lease.StackHigh};
    SehGuard guard(info, cpu, *captured, scope, bounds, owner.ThreadId, true);
    guard.State.UnwindOutput = output;
    unwind_target(exception, targetFrame, targetIp, returnValue);
}
#endif
