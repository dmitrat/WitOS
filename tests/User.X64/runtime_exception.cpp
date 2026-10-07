#include "pal.witos.h"
#include "NativeContext.h"
#include "native_security.h"
#include "tls.h"
#include "../User/protocol.h"
#include <string.h>
#include <errno.h>
extern "C" {
WitU64 wit_test_exception_trigger(WitU64);
void wit_exception_landing();
void wit_exception_invalid();
void wit_exception_divide();
void wit_exception_page();
void wit_exception_breakpoint();
void wit_exception_general();
void wit_exception_low_stack();
}
static WitU64 mode, kind, arena, imageInfo, lastToken;
static volatile WitU64 calls, workerDone;
static WitU64 mainIdentity;
static HANDLE mainReference;
static volatile WitU64 activations, activationSequence, activationThread;
static WitU64 sleeperIdentity;

static WitU64 query(WitU64 token, WitUserExceptionInfo &info)
{
    return wit_native_call(WIT_CALL_EXCEPTION_QUERY, token, (WitU64)&info, sizeof(info), nullptr);
}

static void fail(WitU64 code)
{
    wit_native_fail_fast(code);
}

/* CONTINUE through the transfer request; the current token retires the current record alone. */
static WitU64 resume(WitU64 token, const WitThreadContext *context)
{
    WitUserExceptionTransfer transfer = {};
    transfer.Version = WIT_EXCEPTION_TRANSFER_VERSION;
    transfer.Size = sizeof(transfer);
    transfer.RetireThroughToken = token;
    transfer.Context = *context;
    return wit_native_call(WIT_CALL_EXCEPTION_CONTINUE, token, (WitU64)&transfer, sizeof(transfer), nullptr);
}

static WitU64 intruder(WitU64 token)
{
    WitUserExceptionInfo info;
    memset(&info, 0xA5, sizeof(info));
    if (query(token, info) != WIT_STATUS_BAD_HANDLE ||
        wit_native_call(WIT_CALL_EXCEPTION_CONTINUE, token, 0, sizeof(WitUserExceptionTransfer), nullptr) !=
            WIT_STATUS_BAD_HANDLE) {
        return 4101;
    }
    for (unsigned i = 0; i < sizeof(info); ++i) {
        if (((WitU8 *)&info)[i] != 0xA5) {
            return 4102;
        }
    }
    if (SuspendThread(mainReference) != 0) {
        return 4130;
    }
    WitThreadContext before, changed, after;
    if (wit_native_call(WIT_CALL_THREAD_CONTEXT_GET, (WitU64)mainReference, (WitU64)&before, sizeof(before), nullptr) !=
            WIT_STATUS_OK ||
        !(before.Flags & WIT_THREAD_CONTEXT_EXCEPTION_ACTIVE) ||
        before.State != WIT_THREAD_CONTEXT_READY) {
        return 4131;
    }
    changed = before;
    changed.R12 ^= 0x1234;
    if (wit_native_call(WIT_CALL_THREAD_CONTEXT_SET, (WitU64)mainReference, (WitU64)&changed, sizeof(changed),
            nullptr) != WIT_STATUS_BUSY ||
        wit_native_call(WIT_CALL_THREAD_CONTEXT_GET, (WitU64)mainReference, (WitU64)&after, sizeof(after), nullptr) !=
            WIT_STATUS_OK ||
        memcmp(&before, &after, sizeof(before))) {
        return 4132;
    }
    NATIVE_CONTEXT native = {};
    native.ctx.ContextFlags = CONTEXT_FULL | CONTEXT_DEBUG_REGISTERS;
    if (!PalGetCompleteThreadContext(mainReference, &native) ||
        !(native.ctx.ContextFlags & CONTEXT_EXCEPTION_ACTIVE) ||
        ResumeThread(mainReference) != 1) {
        return 4133;
    }
    if (!wit_test_exception_trigger(0)) {
        return 4134;
    }
    workerDone = 1;
    return WIT_TEST_EXIT_CODE;
}

// The requester's callback of an activation: runs on the activated thread inside the fault callback.
static void activation_target(WitU64 argument)
{
    ++activations;
    activationSequence = activationSequence * 16 + (argument & 0xF);
    WitUserThreadInfo self;
    if (wit_native_thread_query(WIT_THREAD_SELF, &self) != WIT_STATUS_OK) {
        fail(4141);
    }
    activationThread = self.ThreadId;
}

/* An activation record (RFC 0011 section 7.5): the vector names it, Address is the callback, Error the argument and
 * the context the interrupted one; the handler runs the callback and continues the context unchanged. */
static void activation(WitU64 token, WitU64 vector, WitU64 address)
{
    WitUserExceptionInfo info;
    WitUserThreadInfo self;
    if (query(token, info) != WIT_STATUS_OK ||
        info.Version != WIT_EXCEPTION_VERSION ||
        info.Size != sizeof(info) ||
        info.Token != token ||
        token <= lastToken ||
        info.Vector != vector ||
        info.Address != address ||
        address != (WitU64)&activation_target ||
        (info.Error >> 4) != 0x515 ||
        wit_native_thread_query(WIT_THREAD_SELF, &self) != WIT_STATUS_OK ||
        info.Context.ThreadId != self.ThreadId ||
        info.Context.State != WIT_THREAD_CONTEXT_RUNNING ||
        info.Context.Flags != (WIT_THREAD_CONTEXT_FXSAVE64 | WIT_THREAD_CONTEXT_EXCEPTION_ACTIVE) ||
        info.Context.SuspendCount ||
        info.Context.StackLow != self.StackLow ||
        info.Context.StackHigh != self.StackHigh ||
        info.Context.Rsp < self.StackLow ||
        info.Context.Rsp >= self.StackHigh) {
        fail(4142);
    }
    lastToken = token;
    // A self-activation interrupts the return of the activating call itself (RAX is its status); an activation of a
    // parked thread interrupts its wait or sleep.
    if (info.Context.Rax != (info.Error == 0x5150 ? WIT_STATUS_OK : WIT_STATUS_INTERRUPTED)) {
        fail(4143);
    }
    ((void (*)(WitU64))info.Address)(info.Error);
    if (wit_native_call(WIT_CALL_EXCEPTION_REGISTER, 0, WIT_EXCEPTION_VERSION, 0, nullptr) != WIT_STATUS_BUSY) {
        fail(4144);
    }
    resume(token, &info.Context);
    fail(4145);
}

// Activates the main thread once it is parked (argument 0: in a wait, 1: in a sleep).
static WitU64 activator(WitU64 argument)
{
    for (;;) {
        WitUserThreadInfo info;
        if (wit_native_thread_query((WitU64)mainReference, &info) != WIT_STATUS_OK) {
            return 4150;
        }
        if (info.State == WIT_THREAD_STATE_WAITING) {
            break;
        }
        wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr);
    }
    return wit_native_call(WIT_CALL_THREAD_ACTIVATE, (WitU64)mainReference, (WitU64)&activation_target,
               0x5151 + argument, nullptr) == WIT_STATUS_OK
        ? WIT_TEST_EXIT_CODE
        : 4151;
}

// Parks in a wait; suspended and activated four times, it sees the four callbacks in order, then INTERRUPTED.
static WitU64 sleeper(WitU64 event)
{
    WitUserThreadInfo self;
    if (wit_native_thread_query(WIT_THREAD_SELF, &self) != WIT_STATUS_OK) {
        return 4152;
    }
    sleeperIdentity = self.ThreadId;
    const WitU64 before = activations;
    WitUserWaitRequest request = {WIT_WAIT_OBJECTS_VERSION, sizeof(request), (WitU64)&event, 1, 0, WIT_WAIT_INFINITE};
    if (wit_native_call(WIT_CALL_OBJECT_WAIT, (WitU64)&request, sizeof(request), 0, nullptr) !=
        WIT_STATUS_INTERRUPTED) {
        return 4153;
    }
    return activations == before + 4 && (activationSequence & 0xFFFF) == 0x3456 && activationThread == sleeperIdentity
        ? WIT_TEST_EXIT_CODE
        : 4154;
}

static WitU64 exiter(WitU64)
{
    return WIT_TEST_EXIT_CODE;
}

extern "C" void wit_test_exception_callback(WitU64 token, WitU64 vector, WitU64 address)
{
    if (vector == WIT_EXCEPTION_ACTIVATION_VECTOR) {
        activation(token, vector, address);
    }
    ++calls;
    auto report = (WitU64 *)WIT_GC_INFO_REPORT;
    report[2] = calls;
    if (mode == 113) {
        *(volatile WitU64 *)0x400000000000ULL = 1;
        return;
    }
    if (mode == 114) {
        return;
    }
    if (mode == 112) {
        wit_native_call(WIT_CALL_EXCEPTION_REJECT, token, 0, 0, nullptr);
        fail(4103);
    }
    WitUserExceptionInfo info, after;
    if (query(token, info) != WIT_STATUS_OK ||
        info.Version != WIT_EXCEPTION_VERSION ||
        info.Size != sizeof(info) ||
        info.Token != token ||
        token <= lastToken ||
        info.Vector != vector ||
        info.Address != address) {
        return fail(4104);
    }
    lastToken = token;
    const WitU64 vectors[] = {6, 0, 14, 3, 13, 14};
    const WitU64 ips[] = {(WitU64)&wit_exception_invalid, (WitU64)&wit_exception_divide, (WitU64)&wit_exception_page,
        (WitU64)&wit_exception_breakpoint, (WitU64)&wit_exception_general, 0};
    if (kind >= 6 ||
        vector != vectors[kind] ||
        info.Context.Rip != ips[kind] ||
        info.Context.R12 != 0x1122334455667788ULL ||
        *(WitU64 *)&info.Context.FxState[256] != 0x1122334455667788ULL ||
        info.Context.Flags != (WIT_THREAD_CONTEXT_FXSAVE64 | WIT_THREAD_CONTEXT_EXCEPTION_ACTIVE) ||
        info.Context.SuspendCount ||
        info.Context.State != WIT_THREAD_CONTEXT_RUNNING ||
        (kind == 2 && (address != 0x400000000000ULL || info.Error != 4)) ||
        (kind == 5 && (address || info.Error != 20)) ||
        (kind == 4 && info.Error != 0xFFF8) ||
        (vector != 14 && address)) {
        return fail(4105);
    }
    memset(&after, 0xA5, sizeof(after));
    if (query(token + 1, after) != WIT_STATUS_BAD_HANDLE) {
        return fail(4106);
    }
    for (unsigned i = 0; i < sizeof(after); ++i) {
        if (((WitU8 *)&after)[i] != 0xA5) {
            return fail(4107);
        }
    }
    if (wit_native_call(WIT_CALL_EXCEPTION_QUERY, token, arena + 4080, sizeof(info), nullptr) !=
            WIT_STATUS_BAD_ADDRESS ||
        wit_native_call(WIT_CALL_EXCEPTION_QUERY, token, (WitU64)&after, sizeof(info) - 1, nullptr) !=
            WIT_STATUS_INVALID_ARGUMENT) {
        return fail(4108);
    }
    for (unsigned i = 0; i < 16; ++i) {
        if (((WitU8 *)arena)[4080 + i] != 0xA5) {
            return fail(4109);
        }
    }
    if (wit_native_call(WIT_CALL_EXCEPTION_REGISTER, 0, WIT_EXCEPTION_VERSION, 0, nullptr) != WIT_STATUS_BUSY ||
        wit_native_call(WIT_CALL_THREAD_CONTEXT_RESTORE, (WitU64)&info.Context, sizeof(info.Context),
            WIT_THREAD_CONTEXT_VERSION, nullptr) != WIT_STATUS_BUSY ||
        resume(token + 1, &info.Context) != WIT_STATUS_BAD_HANDLE ||
        wit_native_call(WIT_CALL_EXCEPTION_CONTINUE, token, arena + 4092, sizeof(WitUserExceptionTransfer), nullptr) !=
            WIT_STATUS_BAD_ADDRESS) {
        return fail(4110);
    }
    WitThreadContext input;
    if (wit_native_call(WIT_CALL_THREAD_CONTEXT_GET, WIT_THREAD_REFERENCE_CURRENT, (WitU64)&input, sizeof(input),
            nullptr) != WIT_STATUS_OK ||
        !(input.Flags & WIT_THREAD_CONTEXT_EXCEPTION_ACTIVE)) {
        return fail(4111);
    }
    for (unsigned i = 0; i < 6; ++i) {
        input = info.Context;
        input.Rip = (WitU64)&wit_exception_landing;
        WitU64 expected = WIT_STATUS_INVALID_ARGUMENT;
        switch (i) {
        case 0:
            input.Cs = 8;
            break;
        case 1:
            input.Rflags |= 0x3000;
            break;
        case 2:
            ++input.ThreadId;
            break;
        case 3:
            input.Rip = imageInfo;
            expected = WIT_STATUS_BAD_ADDRESS;
            break;
        case 4:
            input.FxState[511] = 1;
            break;
        case 5:
            input.Flags &= ~WIT_THREAD_CONTEXT_EXCEPTION_ACTIVE;
            break;
        }
        if (resume(token, &input) != expected ||
            query(token, after) != WIT_STATUS_OK ||
            memcmp(&info, &after, sizeof(info))) {
            return fail(4112 + i);
        }
    }
    WitStackLeaseInfo lease;
    if (wit_native_call(WIT_CALL_STACK_LEASE_ACQUIRE, WIT_THREAD_REFERENCE_CURRENT, (WitU64)&lease, sizeof(lease),
            nullptr) != WIT_STATUS_OK ||
        resume(token, &info.Context) != WIT_STATUS_BUSY ||
        wit_native_call(WIT_CALL_STACK_LEASE_RELEASE, lease.Token, 0, 0, nullptr) != WIT_STATUS_OK) {
        return fail(4118);
    }
    if (mode == 110 && !kind && info.Context.ThreadId == mainIdentity) {
        WitU64 join = 0, result = 0;
        if (wit_native_thread_create(intruder, token, &join) != WIT_STATUS_OK) {
            return fail(4119);
        }
        for (unsigned wait = 0; wait < 10000 && !workerDone; ++wait) {
            wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr);
        }
        if (!workerDone ||
            wit_native_thread_join(join, &result) != WIT_STATUS_OK ||
            result != WIT_TEST_EXIT_CODE ||
            query(token, after) != WIT_STATUS_OK ||
            memcmp(&info, &after, sizeof(info))) {
            return fail(4119);
        }
    }
    input = info.Context;
    input.Rip = (WitU64)&wit_exception_landing;
    input.Rax = 0xFEDCBA9876543210ULL;
    input.R12 = 0x778899AABBCCDD11ULL;
    input.Rflags = 0x247;
    *(WitU64 *)&input.FxState[256] = input.R12;
    resume(token, &input);
    fail(4120);
}

extern "C" WitU64 wit_test_exception(const WitUserStartup *startup, WitU64 selected)
{
    mode = selected;
    calls = 0;
    workerDone = 0;
    lastToken = 0;
    imageInfo = startup->ImageInfo;
    auto report = (WitU64 *)WIT_GC_INFO_REPORT;
    report[0] = mode;
    report[1] = 35184372088832ULL;
    report[2] = 0;
    wit_native_security_initialize_system();
    wit_native_process_image_initialize(startup);
    if (mode == 110) {
        wit_native_tls_initialize(startup);
    }
    WitUserThreadInfo own;
    if (wit_native_thread_query(WIT_THREAD_SELF, &own) != WIT_STATUS_OK) {
        return 4135;
    }
    mainIdentity = own.ThreadId;
    mainReference = nullptr;
    if (mode == 110 &&
        !DuplicateHandle(GetCurrentProcess(), GetCurrentThread(), GetCurrentProcess(), &mainReference, 0, FALSE,
            DUPLICATE_SAME_ACCESS)) {
        return 4136;
    }
    SetLastError(0xD2345678);
    if (mode == 110) {
        errno = 297;
    }
    // No fault callback yet: an activation has nowhere to go.
    if (wit_native_call(WIT_CALL_THREAD_ACTIVATE, WIT_THREAD_SELF, (WitU64)&activation_target, 0x5150, nullptr) !=
        WIT_STATUS_NOT_FOUND) {
        return 4160;
    }
    if (wit_native_call(WIT_CALL_EXCEPTION_REGISTER, imageInfo, WIT_EXCEPTION_VERSION, 0, nullptr) !=
            WIT_STATUS_BAD_ADDRESS ||
        wit_native_call(WIT_CALL_EXCEPTION_REGISTER, (WitU64)&wit_test_exception_callback, 0, 0, nullptr) !=
            WIT_STATUS_UNSUPPORTED ||
        wit_native_call(WIT_CALL_EXCEPTION_REGISTER, (WitU64)&wit_test_exception_callback, WIT_EXCEPTION_VERSION, 1,
            nullptr) != WIT_STATUS_INVALID_ARGUMENT ||
        wit_native_call(WIT_CALL_EXCEPTION_REGISTER, (WitU64)&wit_test_exception_callback, WIT_EXCEPTION_VERSION, 0,
            nullptr) != WIT_STATUS_OK) {
        return 4121;
    }
    if (mode == 116 &&
        wit_native_call(WIT_CALL_EXCEPTION_REGISTER, 0, WIT_EXCEPTION_VERSION, 0, nullptr) != WIT_STATUS_OK) {
        return 4122;
    }
    if (mode >= 112) {
        report[3] = (WitU64)(mode == 115 ? &wit_exception_low_stack : &wit_exception_invalid);
        wit_test_exception_trigger(mode == 115 ? 6 : 0);
        return 4123;
    }
    if (wit_native_call(WIT_CALL_MEMORY_RESERVE, 8192, 4096, 0, &arena) != WIT_STATUS_OK ||
        wit_native_call(WIT_CALL_MEMORY_COMMIT, arena, 4096, 3, nullptr) != WIT_STATUS_OK) {
        return 4124;
    }
    for (unsigned i = 0; i < 16; ++i) {
        ((WitU8 *)arena)[4080 + i] = 0xA5;
    }
    for (kind = 0; kind < 6; ++kind) {
        report[4] = kind;
        if (!wit_test_exception_trigger(kind) || calls != kind + 1 + (mode == 110 ? 1U : 0U)) {
            return 4125;
        }
        WitUserExceptionInfo info;
        if (query(lastToken, info) != WIT_STATUS_BAD_HANDLE) {
            return 4126;
        }
        WitThreadContext context;
        if (wit_native_call(WIT_CALL_THREAD_CONTEXT_GET, WIT_THREAD_REFERENCE_CURRENT, (WitU64)&context,
                sizeof(context), nullptr) != WIT_STATUS_OK ||
            (context.Flags & WIT_THREAD_CONTEXT_EXCEPTION_ACTIVE)) {
            return 4127;
        }
    }
    activations = activationSequence = activationThread = 0;
    // A self-activation is delivered at the return of the activating call through the registered callback; the
    // callback must be executable and the handle a thread's.
    if (wit_native_call(WIT_CALL_THREAD_ACTIVATE, WIT_THREAD_SELF, imageInfo, 0x5150, nullptr) !=
            WIT_STATUS_BAD_ADDRESS ||
        wit_native_call(WIT_CALL_THREAD_ACTIVATE, 0x7777, (WitU64)&activation_target, 0x5150, nullptr) !=
            WIT_STATUS_BAD_HANDLE ||
        wit_native_call(WIT_CALL_THREAD_ACTIVATE, WIT_THREAD_SELF, (WitU64)&activation_target, 0x5150, nullptr) !=
            WIT_STATUS_OK ||
        activations != 1 ||
        activationThread != mainIdentity) {
        return 4161;
    }
    WitThreadContext settled;
    if (wit_native_call(WIT_CALL_THREAD_CONTEXT_GET, WIT_THREAD_REFERENCE_CURRENT, (WitU64)&settled, sizeof(settled),
            nullptr) != WIT_STATUS_OK ||
        (settled.Flags & WIT_THREAD_CONTEXT_EXCEPTION_ACTIVE)) {
        return 4162;
    }
    if (mode == 110) {
        WitU64 event = 0, handle = 0, result = 0;
        if (wit_native_call(WIT_CALL_EVENT_CREATE, 0, 0, 0, &event) != WIT_STATUS_OK) {
            return 4163;
        }
        // A wait and a sleep end with INTERRUPTED once the activation's callback ran on this thread.
        WitUserWaitRequest request = {
            WIT_WAIT_OBJECTS_VERSION, sizeof(request), (WitU64)&event, 1, 0, WIT_WAIT_INFINITE};
        if (wit_native_thread_create(activator, 0, &handle) != WIT_STATUS_OK ||
            wit_native_call(WIT_CALL_OBJECT_WAIT, (WitU64)&request, sizeof(request), 0, nullptr) !=
                WIT_STATUS_INTERRUPTED ||
            activations != 2 ||
            activationThread != mainIdentity ||
            wit_native_thread_join(handle, &result) != WIT_STATUS_OK ||
            result != WIT_TEST_EXIT_CODE) {
            return 4164;
        }
        WitU64 now = 0;
        if (wit_native_clock_read(&now) != WIT_STATUS_OK ||
            wit_native_thread_create(activator, 1, &handle) != WIT_STATUS_OK ||
            wit_native_call(WIT_CALL_SLEEP_UNTIL, now + wit_native_tick_counts(1000), 0, 0, nullptr) !=
                WIT_STATUS_INTERRUPTED ||
            activations != 3 ||
            wit_native_thread_join(handle, &result) != WIT_STATUS_OK ||
            result != WIT_TEST_EXIT_CODE) {
            return 4165;
        }
        // A handle without the ACTIVATE right is refused; an exited thread is closed to activation.
        WitU64 limited = 0, copied = 0;
        if (wit_native_call(WIT_CALL_HANDLE_DUPLICATE, WIT_THREAD_SELF, (WitU64)&limited,
                WIT_RIGHT_QUERY | WIT_RIGHT_WAIT, &copied) != WIT_STATUS_OK ||
            wit_native_call(WIT_CALL_THREAD_ACTIVATE, limited, (WitU64)&activation_target, 0x5150, nullptr) !=
                WIT_STATUS_DENIED ||
            wit_native_call(WIT_CALL_HANDLE_CLOSE, limited, 0, 0, nullptr) != WIT_STATUS_OK) {
            return 4166;
        }
        if (wit_native_thread_create(exiter, 0, &handle) != WIT_STATUS_OK ||
            wit_native_wait_one(handle, WIT_WAIT_INFINITE) != WIT_STATUS_OK ||
            wit_native_call(WIT_CALL_THREAD_ACTIVATE, handle, (WitU64)&activation_target, 0x5150, nullptr) !=
                WIT_STATUS_CLOSED ||
            wit_native_call(WIT_CALL_HANDLE_CLOSE, handle, 0, 0, nullptr) != WIT_STATUS_OK) {
            return 4167;
        }
        // Four activations stay pending on a suspended thread, the fifth is refused, the callback cannot be dropped
        // while they wait, and the four are delivered in order when the thread resumes.
        WitU64 previous = 0;
        if (wit_native_thread_create(sleeper, event, &handle) != WIT_STATUS_OK) {
            return 4168;
        }
        for (;;) {
            WitUserThreadInfo info;
            if (wit_native_thread_query(handle, &info) != WIT_STATUS_OK || info.State == WIT_THREAD_STATE_EXITED) {
                return 4169;
            }
            if (info.State == WIT_THREAD_STATE_WAITING) {
                break;
            }
            wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr);
        }
        if (wit_native_call(WIT_CALL_THREAD_SUSPEND, handle, 0, 0, &previous) != WIT_STATUS_OK || previous) {
            return 4170;
        }
        for (WitU64 i = 0; i < 4; ++i) {
            if (wit_native_call(WIT_CALL_THREAD_ACTIVATE, handle, (WitU64)&activation_target, 0x5153 + i, nullptr) !=
                WIT_STATUS_OK) {
                return 4171;
            }
        }
        WitUserThreadInfo parked;
        if (wit_native_call(WIT_CALL_THREAD_ACTIVATE, handle, (WitU64)&activation_target, 0x5157, nullptr) !=
                WIT_STATUS_NO_MEMORY ||
            wit_native_call(WIT_CALL_EXCEPTION_REGISTER, 0, WIT_EXCEPTION_VERSION, 0, nullptr) != WIT_STATUS_BUSY ||
            wit_native_thread_query(handle, &parked) != WIT_STATUS_OK ||
            parked.State != WIT_THREAD_STATE_SUSPENDED ||
            activations != 3) {
            return 4172;
        }
        if (wit_native_call(WIT_CALL_THREAD_RESUME, handle, 0, 0, &previous) != WIT_STATUS_OK ||
            previous != 1 ||
            wit_native_thread_join(handle, &result) != WIT_STATUS_OK ||
            result != WIT_TEST_EXIT_CODE ||
            activations != 7 ||
            wit_native_call(WIT_CALL_HANDLE_CLOSE, event, 0, 0, nullptr) != WIT_STATUS_OK) {
            return 4173;
        }
    }
    if (GetLastError() != 0xD2345678 ||
        (mode == 110 && errno != 297) ||
        wit_native_call(WIT_CALL_EXCEPTION_REGISTER, 0, WIT_EXCEPTION_VERSION, 0, nullptr) != WIT_STATUS_OK ||
        wit_native_call(WIT_CALL_MEMORY_RELEASE, arena, 0, 0, nullptr) != WIT_STATUS_OK) {
        return 4128;
    }
    if (mainReference && !CloseHandle(mainReference)) {
        return 4137;
    }
    report[3] = 1;
    return WIT_TEST_EXIT_CODE;
}
