#include "pal.witos.h"
#include "native_security.h"
#include "tls.h"
#include "../User/protocol.h"
#include "unwind_scope.witos.h"
#include <errno.h>
static volatile HANDLE targetReference;
static volatile WitU64 foreignToken, orphanToken, orphanSelfToken;
static HANDLE eventHandle;

static void yield()
{
    wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr);
}

static WitU64 acquire(HANDLE reference, WitStackLeaseInfo &output)
{
    return wit_native_call(WIT_CALL_STACK_LEASE_ACQUIRE, (WitU64)reference, (WitU64)&output, sizeof(output), nullptr);
}

static WitU64 query(WitU64 token, WitStackLeaseInfo &output)
{
    return wit_native_call(WIT_CALL_STACK_LEASE_QUERY, token, (WitU64)&output, sizeof(output), nullptr);
}

static WitU64 release(WitU64 token)
{
    return wit_native_call(WIT_CALL_STACK_LEASE_RELEASE, token, 0, 0, nullptr);
}

static WitU64 target(WitU64)
{
    HANDLE reference = nullptr;
    if (!DuplicateHandle(GetCurrentProcess(), GetCurrentThread(), GetCurrentProcess(), &reference, 0, FALSE,
            DUPLICATE_SAME_ACCESS)) {
        return 3901;
    }
    targetReference = reference;
    return WaitForMultipleObjectsEx(1, &eventHandle, FALSE, INFINITE, FALSE) == WAIT_OBJECT_0 ? WIT_TEST_EXIT_CODE
                                                                                              : 3902;
}

static bool parked(HANDLE &handle)
{
    for (unsigned i = 0; i < 10000; ++i) {
        WitThreadReferenceInfo info;
        handle = targetReference;
        if (handle &&
            wit_native_call(WIT_CALL_THREAD_REFERENCE_QUERY, (WitU64)handle, (WitU64)&info, sizeof(info), nullptr) ==
                WIT_STATUS_OK &&
            info.State == WIT_THREAD_REFERENCE_WAITING) {
            return true;
        }
        yield();
    }
    return false;
}

static WitU64 intruder(WitU64 targetHandle)
{
    WitStackLeaseInfo snapshot;
    for (unsigned i = 0; i < sizeof(snapshot); ++i) {
        ((WitU8 *)&snapshot)[i] = 0xA5;
    }
    if (release(foreignToken) != WIT_STATUS_DENIED || query(foreignToken, snapshot) != WIT_STATUS_DENIED) {
        return 3903;
    }
    for (unsigned i = 0; i < sizeof(snapshot); ++i) {
        if (((WitU8 *)&snapshot)[i] != 0xA5) {
            return 3903;
        }
    }
    if (acquire((HANDLE)targetHandle, snapshot) != WIT_STATUS_OK) {
        return 3904;
    }
    orphanToken = snapshot.Token;
    if (acquire(GetCurrentThread(), snapshot) != WIT_STATUS_OK) {
        return 3905;
    }
    orphanSelfToken = snapshot.Token;
    // Raw thread exit skips user TLS destructors; kernel still drops this owner's leases.
    wit_native_call(WIT_CALL_THREAD_EXIT, WIT_TEST_EXIT_CODE, 0, 0, nullptr);
    return 3906;
}

static volatile WitU64 scopeRootAddress;
static WitNativeUnwindScope *foreignScope;

static WitU64 scope_target(WitU64)
{
    volatile WitU64 root = 0x1122334455667788ULL;
    scopeRootAddress = (WitU64)&root;
    if (target(0) != WIT_TEST_EXIT_CODE) {
        return 3940;
    }
    return root == 0x8877665544332211ULL ? WIT_TEST_EXIT_CODE : 3941;
}

static WitU64 scope_intruder(WitU64)
{
    WitStackLeaseInfo result;
    if (foreignScope->Close() != WIT_STATUS_DENIED ||
        WitNativeUnwindScope::Current((WitU64)&result, &result) != WIT_STATUS_CLOSED) {
        return 3942;
    }
    WitNativeUnwindScope own(WIT_THREAD_REFERENCE_CURRENT);
    return own.Status() == WIT_STATUS_OK && WitNativeUnwindScope::Current((WitU64)&result, &result) == WIT_STATUS_OK
        ? WIT_TEST_EXIT_CODE
        : 3943;
}

static __declspec(noinline) volatile WitU64 *scoped_location(WitU64 address)
{
    WitStackLeaseInfo result;
    return WitNativeUnwindScope::Current(address, &result) == WIT_STATUS_OK ? (volatile WitU64 *)address : nullptr;
}

static WitU64 scope_test(WitU64 mode, WitU64 *report)
{
    WitStackLeaseInfo output;
    for (unsigned i = 0; i < sizeof(output); ++i) {
        ((WitU8 *)&output)[i] = 0xA5;
    }
    const WitU64 noScope = mode == 104 ? WIT_STATUS_DENIED : WIT_STATUS_CLOSED;
    if (WitNativeUnwindScope::Current((WitU64)&output, &output) != noScope) {
        return 3944;
    }
    for (unsigned i = 0; i < sizeof(output); ++i) {
        if (((WitU8 *)&output)[i] != 0xA5) {
            return 3945;
        }
    }
    if (mode == 104) {
        WitNativeUnwindScope denied(WIT_THREAD_REFERENCE_CURRENT);
        report[2] = 1;
        return denied.Status() == WIT_STATUS_DENIED ? WIT_TEST_EXIT_CODE : 3946;
    }
    if (mode == 105) {
        WitNativeUnwindScope broken(WIT_THREAD_REFERENCE_CURRENT);
        if (broken.Status() != WIT_STATUS_OK ||
            WitNativeUnwindScope::Current((WitU64)&output, &output) != WIT_STATUS_OK ||
            release(output.Token) != WIT_STATUS_OK) {
            return 3947;
        }
        report[2] = 0x1234;
        return 3948; // Destructor must fail fast on lost kernel authority.
    }
    SetLastError(0xB2345678);
    errno = 271;
    {
        WitNativeUnwindScope outer(WIT_THREAD_REFERENCE_CURRENT);
        if (outer.Status() != WIT_STATUS_OK ||
            WitNativeUnwindScope::Current((WitU64)&output, &output) != WIT_STATUS_OK) {
            return 3949;
        }
        const WitStackLeaseInfo parent = output;
        {
            WitNativeUnwindScope two(WIT_THREAD_REFERENCE_CURRENT), three(WIT_THREAD_REFERENCE_CURRENT),
                four(WIT_THREAD_REFERENCE_CURRENT), overflow(WIT_THREAD_REFERENCE_CURRENT);
            if (two.Status() != WIT_STATUS_OK ||
                three.Status() != WIT_STATUS_OK ||
                four.Status() != WIT_STATUS_OK ||
                overflow.Status() != WIT_STATUS_NO_MEMORY ||
                outer.Close() != WIT_STATUS_BUSY ||
                WitNativeUnwindScope::Current((WitU64)&output, &output) != WIT_STATUS_OK ||
                output.Token == parent.Token) {
                return 3950;
            }
        }
        if (WitNativeUnwindScope::Current((WitU64)&output, &output) != WIT_STATUS_OK || output.Token != parent.Token) {
            return 3951;
        }
        for (unsigned i = 0; i < sizeof(output); ++i) {
            ((WitU8 *)&output)[i] = 0xA5;
        }
        if (WitNativeUnwindScope::Current(parent.StackHigh, &output) != WIT_STATUS_BAD_ADDRESS ||
            WitNativeUnwindScope::Current(parent.StackLow - 1, &output) != WIT_STATUS_BAD_ADDRESS ||
            WitNativeUnwindScope::Current(parent.StackLow, nullptr) != WIT_STATUS_INVALID_ARGUMENT) {
            return 3952;
        }
        for (unsigned i = 0; i < sizeof(output); ++i) {
            if (((WitU8 *)&output)[i] != 0xA5) {
                return 3953;
            }
        }
        eventHandle = CreateEventExW(nullptr, nullptr, 0, SYNCHRONIZE | EVENT_MODIFY_STATE);
        if (!eventHandle) {
            return 3954;
        }
        targetReference = nullptr;
        scopeRootAddress = 0;
        WitU64 join = 0, result = 0;
        HANDLE handle = nullptr, controller = nullptr;
        if (wit_native_thread_create(scope_target, 0, &join) != WIT_STATUS_OK ||
            !parked(handle) ||
            !scopeRootAddress ||
            !DuplicateHandle(
                GetCurrentProcess(), handle, GetCurrentProcess(), &controller, 0, FALSE, DUPLICATE_SAME_ACCESS)) {
            return 3955;
        }
        {
            WitNativeUnwindScope running((WitU64)handle);
            if (running.Status() != WIT_STATUS_BUSY) {
                return 3956;
            }
        }
        if (SuspendThread(controller) != 0) {
            return 3957;
        }
        {
            WitNativeUnwindScope targetScope((WitU64)handle);
            if (targetScope.Status() != WIT_STATUS_OK ||
                !CloseHandle(handle) ||
                WitNativeUnwindScope::Current(scopeRootAddress, &output) != WIT_STATUS_OK ||
                output.ThreadId == parent.ThreadId ||
                output.OwnerId != parent.OwnerId ||
                WitNativeUnwindScope::Current(parent.StackLow, &output) != WIT_STATUS_BAD_ADDRESS) {
                return 3958;
            }
            foreignScope = &targetScope;
            WitU64 other = 0;
            if (wit_native_thread_create(scope_intruder, 0, &other) != WIT_STATUS_OK ||
                wit_native_call(WIT_CALL_THREAD_JOIN, other, 0, 0, &result) != WIT_STATUS_OK ||
                result != WIT_TEST_EXIT_CODE) {
                return 3959;
            }
            volatile WitU64 *location = scoped_location(scopeRootAddress);
            // Retain and modify the original address after the helper returns and after scheduling another thread.
            yield();
            if (!location ||
                *location != 0x1122334455667788ULL ||
                wit_native_call(WIT_CALL_THREAD_RESUME, (WitU64)controller, 0, 0, nullptr) != WIT_STATUS_BUSY) {
                return 3960;
            }
            *location = 0x8877665544332211ULL;
            if (!SetEvent(eventHandle)) {
                return 3961;
            }
        }
        if (WitNativeUnwindScope::Current((WitU64)&output, &output) != WIT_STATUS_OK ||
            output.Token != parent.Token ||
            ResumeThread(controller) != 1 ||
            wit_native_call(WIT_CALL_THREAD_JOIN, join, 0, 0, &result) != WIT_STATUS_OK ||
            result != WIT_TEST_EXIT_CODE ||
            !CloseHandle(controller) ||
            !CloseHandle(eventHandle)) {
            return 3962;
        }
        if (GetLastError() != 0xB2345678 ||
            errno != 271 ||
            outer.Close() != WIT_STATUS_OK ||
            outer.Close() != WIT_STATUS_CLOSED ||
            WitNativeUnwindScope::Current((WitU64)&output, &output) != WIT_STATUS_CLOSED) {
            return 3963;
        }
    }
    report[2] = 1;
    return WIT_TEST_EXIT_CODE;
}

extern "C" WitU64 wit_test_stack_lease(const WitUserStartup *startup, WitU64 mode)
{
    auto report = (WitU64 *)WIT_GC_INFO_REPORT;
    report[0] = mode;
    report[1] = 8796093022208ULL;
    wit_native_security_initialize_system();
    wit_native_process_image_initialize(startup);
    if (mode == 100 || mode == 103 || mode == 105) {
        wit_native_tls_initialize(startup);
    }
    if (mode >= 103) {
        return scope_test(mode, report);
    }
    WitStackLeaseInfo leases[WIT_STACK_LEASE_CAPACITY], snapshot;
    WitU64 arena = 0;
    if (wit_native_call(WIT_CALL_MEMORY_RESERVE, 8192, 4096, 0, &arena) != WIT_STATUS_OK ||
        wit_native_call(WIT_CALL_MEMORY_COMMIT, arena, 4096, 3, nullptr) != WIT_STATUS_OK) {
        return 3907;
    }
    for (unsigned i = 0; i < 16; ++i) {
        ((WitU8 *)arena)[4080 + i] = 0xA5;
    }
    if (wit_native_call(WIT_CALL_STACK_LEASE_ACQUIRE, WIT_THREAD_REFERENCE_CURRENT, arena + 4080, sizeof(snapshot),
            nullptr) != WIT_STATUS_BAD_ADDRESS ||
        wit_native_call(WIT_CALL_STACK_LEASE_ACQUIRE, WIT_THREAD_REFERENCE_CURRENT, (WitU64)&snapshot,
            sizeof(snapshot) - 1, nullptr) != WIT_STATUS_INVALID_ARGUMENT ||
        wit_native_call(WIT_CALL_STACK_LEASE_ACQUIRE, WIT_THREAD_REFERENCE_CURRENT, startup->ImageInfo,
            sizeof(snapshot), nullptr) != WIT_STATUS_BAD_ADDRESS) {
        return 3908;
    }
    for (unsigned i = 0; i < 16; ++i) {
        if (((WitU8 *)arena)[4080 + i] != 0xA5) {
            return 3909;
        }
    }
    WitUserThreadInfo self;
    if (wit_native_call(WIT_CALL_THREAD_QUERY, (WitU64)&self, sizeof(self), WIT_THREAD_INFO_VERSION, nullptr) !=
        WIT_STATUS_OK) {
        return 3910;
    }
    for (unsigned i = 0; i < WIT_STACK_LEASE_CAPACITY; ++i) {
        if (acquire(GetCurrentThread(), leases[i]) != WIT_STATUS_OK ||
            leases[i].Version != WIT_STACK_LEASE_VERSION ||
            leases[i].Size != sizeof(snapshot) ||
            leases[i].OwnerId != self.ThreadId ||
            leases[i].ThreadId != self.ThreadId ||
            leases[i].StackLow != self.StackLow ||
            leases[i].StackHigh != self.StackHigh ||
            (i && leases[i].Token <= leases[i - 1].Token)) {
            return 3911;
        }
    }
    for (unsigned i = 0; i < sizeof(snapshot); ++i) {
        ((WitU8 *)&snapshot)[i] = 0xA5;
    }
    if (acquire(GetCurrentThread(), snapshot) != WIT_STATUS_NO_MEMORY) {
        return 3912;
    }
    for (unsigned i = 0; i < sizeof(snapshot); ++i) {
        if (((WitU8 *)&snapshot)[i] != 0xA5) {
            return 3913;
        }
    }
    WitThreadContext context;
    if (wit_native_call(WIT_CALL_THREAD_CONTEXT_GET, WIT_THREAD_REFERENCE_CURRENT, (WitU64)&context, sizeof(context),
            nullptr) != WIT_STATUS_OK ||
        wit_native_call(WIT_CALL_THREAD_CONTEXT_RESTORE, (WitU64)&context, sizeof(context), WIT_THREAD_CONTEXT_VERSION,
            nullptr) != WIT_STATUS_BUSY) {
        return 3914;
    }
    if (wit_native_call(WIT_CALL_STACK_LEASE_QUERY, leases[0].Token, arena + 4080, sizeof(snapshot), nullptr) !=
            WIT_STATUS_BAD_ADDRESS ||
        wit_native_call(WIT_CALL_STACK_LEASE_QUERY, leases[0].Token, (WitU64)&snapshot, sizeof(snapshot) - 1,
            nullptr) != WIT_STATUS_INVALID_ARGUMENT ||
        wit_native_call(WIT_CALL_STACK_LEASE_RELEASE, leases[0].Token, 1, 0, nullptr) != WIT_STATUS_INVALID_ARGUMENT) {
        return 3915;
    }
    for (unsigned i = 0; i < 16; ++i) {
        if (((WitU8 *)arena)[4080 + i] != 0xA5) {
            return 3916;
        }
    }
    for (unsigned i = 0; i < WIT_STACK_LEASE_CAPACITY; ++i) {
        if (query(leases[i].Token, snapshot) != WIT_STATUS_OK ||
            snapshot.Token != leases[i].Token ||
            snapshot.OwnerId != self.ThreadId ||
            release(leases[i].Token) != WIT_STATUS_OK ||
            release(leases[i].Token) != WIT_STATUS_BAD_HANDLE ||
            query(leases[i].Token, snapshot) != WIT_STATUS_BAD_HANDLE) {
            return 3917;
        }
    }
    if (wit_native_call(WIT_CALL_MEMORY_RELEASE, arena, 0, 0, nullptr) != WIT_STATUS_OK) {
        return 3918;
    }
    if (mode != 100) {
        if (acquire(GetCurrentThread(), snapshot) != WIT_STATUS_OK ||
            snapshot.Token <= leases[WIT_STACK_LEASE_CAPACITY - 1].Token) {
            return 3919;
        }
        report[2] = snapshot.Token; // Deliberately leave this lease for kernel lifecycle cleanup.
        if (mode == 102) {
            wit_native_call(WIT_CALL_EXIT, WIT_TEST_EXIT_CODE, 0, 0, nullptr);
        }
        return WIT_TEST_EXIT_CODE;
    }
    eventHandle = CreateEventExW(nullptr, nullptr, 0, SYNCHRONIZE | EVENT_MODIFY_STATE);
    if (!eventHandle) {
        return 3920;
    }
    targetReference = nullptr;
    WitU64 join = 0, exitCode = 0;
    HANDLE handle = nullptr, controller = nullptr;
    if (wit_native_thread_create(target, 0, &join) != WIT_STATUS_OK ||
        !parked(handle) ||
        !DuplicateHandle(
            GetCurrentProcess(), handle, GetCurrentProcess(), &controller, 0, FALSE, DUPLICATE_SAME_ACCESS)) {
        return 3921;
    }
    if (acquire(handle, snapshot) != WIT_STATUS_BUSY || SuspendThread(handle) != 0) {
        return 3922;
    }
    HANDLE restricted = nullptr;
    if (!DuplicateHandle(GetCurrentProcess(), handle, GetCurrentProcess(), &restricted, THREAD_GET_CONTEXT, FALSE, 0) ||
        acquire(restricted, snapshot) != WIT_STATUS_DENIED ||
        !CloseHandle(restricted)) {
        return 3923;
    }
    if (!DuplicateHandle(
            GetCurrentProcess(), handle, GetCurrentProcess(), &restricted, THREAD_SUSPEND_RESUME, FALSE, 0) ||
        acquire(restricted, snapshot) != WIT_STATUS_DENIED ||
        !CloseHandle(restricted)) {
        return 3924;
    }
    if (acquire(handle, leases[0]) != WIT_STATUS_OK ||
        acquire(handle, leases[1]) != WIT_STATUS_OK ||
        leases[0].ThreadId == self.ThreadId ||
        leases[0].OwnerId != self.ThreadId ||
        leases[0].StackLow == self.StackLow ||
        !CloseHandle(handle) ||
        query(leases[0].Token, snapshot) != WIT_STATUS_OK) {
        return 3925;
    }
    if (!SetEvent(eventHandle)) {
        return 3926;
    }
    if (wit_native_call(WIT_CALL_THREAD_CONTEXT_GET, (WitU64)controller, (WitU64)&context, sizeof(context), nullptr) !=
            WIT_STATUS_OK ||
        context.State != WIT_THREAD_CONTEXT_READY ||
        context.Flags != (WIT_THREAD_CONTEXT_FXSAVE64 | WIT_THREAD_CONTEXT_SUSPENDED)) {
        return 3927;
    }
    const WitThreadContext original = context;
    context.R12 ^= 0x12345678ABCDEF01ULL;
    if (wit_native_call(WIT_CALL_THREAD_CONTEXT_SET, (WitU64)controller, (WitU64)&context, sizeof(context), nullptr) !=
            WIT_STATUS_BUSY ||
        wit_native_call(WIT_CALL_THREAD_CONTEXT_GET, (WitU64)controller, (WitU64)&context, sizeof(context), nullptr) !=
            WIT_STATUS_OK) {
        return 3927;
    }
    for (unsigned i = 0; i < sizeof(context); ++i) {
        if (((const WitU8 *)&context)[i] != ((const WitU8 *)&original)[i]) {
            return 3927;
        }
    }
    WitU64 previous = 99;
    if (wit_native_call(WIT_CALL_THREAD_RESUME, (WitU64)controller, 0, 0, &previous) != WIT_STATUS_BUSY ||
        previous ||
        SuspendThread(controller) != 1 ||
        ResumeThread(controller) != 2) {
        return 3928;
    }
    foreignToken = leases[0].Token;
    orphanToken = orphanSelfToken = 0;
    WitU64 intruderJoin = 0;
    if (wit_native_thread_create(intruder, (WitU64)controller, &intruderJoin) != WIT_STATUS_OK ||
        wit_native_call(WIT_CALL_THREAD_JOIN, intruderJoin, 0, 0, &exitCode) != WIT_STATUS_OK ||
        exitCode != WIT_TEST_EXIT_CODE ||
        !orphanToken ||
        !orphanSelfToken ||
        query(orphanToken, snapshot) != WIT_STATUS_BAD_HANDLE ||
        query(orphanSelfToken, snapshot) != WIT_STATUS_BAD_HANDLE) {
        return 3929;
    }
    if (release(leases[0].Token) != WIT_STATUS_OK ||
        wit_native_call(WIT_CALL_THREAD_RESUME, (WitU64)controller, 0, 0, nullptr) != WIT_STATUS_BUSY ||
        release(leases[1].Token) != WIT_STATUS_OK ||
        ResumeThread(controller) != 1 ||
        wit_native_call(WIT_CALL_THREAD_JOIN, join, 0, 0, &exitCode) != WIT_STATUS_OK ||
        exitCode != WIT_TEST_EXIT_CODE) {
        return 3930;
    }
    if (acquire(controller, snapshot) != WIT_STATUS_CLOSED || !CloseHandle(controller)) {
        return 3931;
    }
    // Reuse the same fixed stack slot with a new generation; old tokens cannot bind to it.
    targetReference = nullptr;
    if (wit_native_thread_create(target, 0, &join) != WIT_STATUS_OK ||
        !parked(handle) ||
        SuspendThread(handle) != 0 ||
        acquire(handle, snapshot) != WIT_STATUS_OK ||
        snapshot.ThreadId == leases[0].ThreadId ||
        snapshot.StackLow != leases[0].StackLow ||
        query(leases[0].Token, leases[2]) != WIT_STATUS_BAD_HANDLE ||
        release(snapshot.Token) != WIT_STATUS_OK ||
        !SetEvent(eventHandle) ||
        ResumeThread(handle) != 1 ||
        wit_native_call(WIT_CALL_THREAD_JOIN, join, 0, 0, &exitCode) != WIT_STATUS_OK ||
        exitCode != WIT_TEST_EXIT_CODE ||
        !CloseHandle(handle) ||
        !CloseHandle(eventHandle)) {
        return 3932;
    }
    report[2] = 1;
    return WIT_TEST_EXIT_CODE;
}
