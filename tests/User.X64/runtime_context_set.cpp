#include "pal.witos.h"
#include "NativeContext.h"
#include "native_security.h"
#include "tls.h"
#include "../User/protocol.h"
#include <errno.h>
// Same private helper declarations as pinned EHHelpers.cpp.
uintptr_t GetSSP(CONTEXT *);
void SetSSP(CONTEXT *, uintptr_t);
extern "C" {
volatile WitU64 wit_context_spin_ready, wit_context_spin_result, wit_context_spin_xmm;
volatile WitU32 wit_context_spin_mxcsr;
void wit_context_spin();
void wit_context_spin_landing();
WitU64 wit_test_pal_restore_roundtrip(WitThreadContext *, WitU64, WitU64, WitU64);
WitU64 wit_test_restore_roundtrip(WitThreadContext *, WitU64, WitU64, WitU64);
}
static volatile HANDLE reference;

static bool get(HANDLE handle, WitThreadContext &c)
{
    return wit_native_call(WIT_CALL_THREAD_CONTEXT_GET, (WitU64)handle, (WitU64)&c, sizeof(c), nullptr) ==
        WIT_STATUS_OK;
}

static WitU64 set(HANDLE handle, const WitThreadContext &c)
{
    return wit_native_call(WIT_CALL_THREAD_CONTEXT_SET, (WitU64)handle, (WitU64)&c, sizeof(c), nullptr);
}

static bool equal(const WitThreadContext &a, const WitThreadContext &b)
{
    for (size_t i = 0; i < sizeof(a); ++i) {
        if (((const WitU8 *)&a)[i] != ((const WitU8 *)&b)[i]) {
            return false;
        }
    }
    return true;
}

static WitU64 worker(WitU64)
{
    HANDLE handle = nullptr;
    if (!DuplicateHandle(
            GetCurrentProcess(), GetCurrentThread(), GetCurrentProcess(), &handle, 0, FALSE, DUPLICATE_SAME_ACCESS)) {
        return 3801;
    }
    reference = handle;
    wit_context_spin();
    return WIT_TEST_EXIT_CODE;
}

extern "C" void wit_test_prepare_restore(WitThreadContext *c, WitU64 ip, WitU64 sp)
{
    c->Rip = ip;
    c->Rsp = sp;
    c->Rflags = 0x247;
    c->Rax = 0x12345678ABCDEF01ULL;
    c->R12 = 0x55AA773311225588ULL;
    *(WitU64 *)&c->FxState[256] = c->R12;
    *(WitU64 *)&c->FxState[264] = c->R12;
    *(WitU32 *)&c->FxState[24] = 0x7F80;
}

extern "C" void wit_test_restore_via_pal(WitThreadContext *w)
{
    NATIVE_CONTEXT native = {};
    CONTEXT &c = native.ctx;
    c.ContextFlags = CONTEXT_FULL | CONTEXT_DEBUG_REGISTERS;
    c.Rip = w->Rip;
    c.Rsp = w->Rsp;
    c.EFlags = (DWORD)w->Rflags;
    c.SegCs = (WORD)w->Cs;
    c.SegSs = (WORD)w->Ss;
    c.Rax = w->Rax;
    c.Rbx = w->Rbx;
    c.Rcx = w->Rcx;
    c.Rdx = w->Rdx;
    c.Rbp = w->Rbp;
    c.Rsi = w->Rsi;
    c.Rdi = w->Rdi;
    c.R8 = w->R8;
    c.R9 = w->R9;
    c.R10 = w->R10;
    c.R11 = w->R11;
    c.R12 = w->R12;
    c.R13 = w->R13;
    c.R14 = w->R14;
    c.R15 = w->R15;
    for (unsigned i = 0; i < 512; ++i) {
        ((WitU8 *)&c.FltSave)[i] = w->FxState[i];
    }
    c.MxCsr = c.FltSave.MxCsr;
    PalRestoreContext(&native);
    wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}

extern "C" WitU64 wit_test_context_mutation(const WitUserStartup *startup, WitU64 mode)
{
    auto report = (WitU64 *)WIT_GC_INFO_REPORT;
    report[0] = mode;
    report[1] = 4398046511104ULL;
    wit_native_security_initialize_system();
    wit_native_process_image_initialize(startup);
    if (mode == 98 || mode == 99) {
        NATIVE_CONTEXT invalid = {};
        invalid.ctx.ContextFlags = CONTEXT_FULL | CONTEXT_DEBUG_REGISTERS;
        report[2] = 0x1234;
        if (mode == 98) {
            SetSSP(&invalid.ctx, 1);
        } else {
            invalid.ctx.ContextFlags |= CONTEXT_XSTATE;
            PalRestoreContext(&invalid);
        }
        return 3850;
    }
    const bool pal = mode >= 96;
    const bool tls = mode == 94 || mode == 96;
    if (tls) {
        wit_native_tls_initialize(startup);
    }
    SetLastError(0xA2345678);
    if (tls) {
        errno = 257;
    }
    WitThreadContext original, changed, check;
    if (!get(GetCurrentThread(), original) || set(GetCurrentThread(), original) != WIT_STATUS_BUSY) {
        return 3802;
    }
    changed = original;
    changed.Rip = 0;
    if (wit_native_call(WIT_CALL_THREAD_CONTEXT_RESTORE, (WitU64)&changed, sizeof(changed), WIT_THREAD_CONTEXT_VERSION,
            nullptr) != WIT_STATUS_BAD_ADDRESS) {
        return 3803;
    }
    changed = original;
    changed.Rflags |= 0x3000;
    if (wit_native_call(WIT_CALL_THREAD_CONTEXT_RESTORE, (WitU64)&changed, sizeof(changed), WIT_THREAD_CONTEXT_VERSION,
            nullptr) != WIT_STATUS_INVALID_ARGUMENT) {
        return 3804;
    }
    if (!(pal ? wit_test_pal_restore_roundtrip(
                    &original, WIT_CALL_THREAD_CONTEXT_GET, WIT_CALL_THREAD_CONTEXT_RESTORE, sizeof(original))
              : wit_test_restore_roundtrip(
                    &original, WIT_CALL_THREAD_CONTEXT_GET, WIT_CALL_THREAD_CONTEXT_RESTORE, sizeof(original)))) {
        return 3805;
    }
    if (tls) {
        reference = nullptr;
        wit_context_spin_ready = 0;
        WitU64 join = 0, result = 0;
        if (wit_native_thread_create(worker, 0, &join) != WIT_STATUS_OK) {
            return 3806;
        }
        for (unsigned i = 0; i < 10000 && !wit_context_spin_ready; ++i) {
            wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr);
        }
        HANDLE target = reference;
        if (!wit_context_spin_ready ||
            !target ||
            SuspendThread(target) != 0 ||
            !get(target, original) ||
            original.State != WIT_THREAD_CONTEXT_READY ||
            original.R12 != 0x1122334455667788ULL) {
            return 3807;
        }
        HANDLE getOnly = nullptr;
        if (!DuplicateHandle(
                GetCurrentProcess(), target, GetCurrentProcess(), &getOnly, THREAD_GET_CONTEXT, FALSE, 0) ||
            set(getOnly, original) != WIT_STATUS_DENIED ||
            !CloseHandle(getOnly)) {
            return 3808;
        }
        for (unsigned which = 0; which < 18; ++which) {
            changed = original;
            WitU64 expected = WIT_STATUS_INVALID_ARGUMENT;
            switch (which) {
            case 0:
                changed.Version = 0;
                expected = WIT_STATUS_UNSUPPORTED;
                break;
            case 1:
                changed.Size = 0;
                break;
            case 2:
                ++changed.ThreadId;
                break;
            case 3:
                ++changed.StackLow;
                break;
            case 4:
                changed.State = WIT_THREAD_CONTEXT_WAITING;
                break;
            case 5:
                changed.Flags |= WIT_THREAD_CONTEXT_SERVICE_ACTIVE;
                break;
            case 6:
                changed.SuspendCount = 0;
                break;
            case 7:
                changed.Reserved = 1;
                break;
            case 8:
                changed.Cs = 8;
                break;
            case 9:
                changed.Ss = 16;
                break;
            case 10:
                changed.Rflags &= ~0x200ULL;
                break;
            case 11:
                changed.Rflags |= 0x3000;
                break;
            case 12:
                changed.Rip = startup->ImageInfo;
                expected = WIT_STATUS_BAD_ADDRESS;
                break;
            case 13:
                changed.Rsp = changed.StackHigh;
                expected = WIT_STATUS_BAD_ADDRESS;
                break;
            case 14:
                *(WitU32 *)&changed.FxState[24] |= 0x80000000;
                break;
            case 15:
                *(WitU32 *)&changed.FxState[28] = 0xFFFFFFFF;
                break;
            case 16:
                changed.FxState[42] = 1;
                break;
            case 17:
                changed.FxState[511] = 1;
                break;
            }
            if (set(target, changed) != expected || !get(target, check) || !equal(original, check)) {
                return 3820 + which;
            }
        }
        WitU64 arena = 0;
        if (wit_native_call(WIT_CALL_MEMORY_RESERVE, 8192, 4096, 0, &arena) != WIT_STATUS_OK ||
            wit_native_call(WIT_CALL_MEMORY_COMMIT, arena, 4096, 3, nullptr) != WIT_STATUS_OK) {
            return 3840;
        }
        if (wit_native_call(WIT_CALL_THREAD_CONTEXT_SET, (WitU64)target, arena + 4092, sizeof(original), nullptr) !=
                WIT_STATUS_BAD_ADDRESS ||
            !get(target, check) ||
            !equal(original, check)) {
            return 3841;
        }
        if (pal) {
            SetLastError(0xA2345678);
            uint8_t *allocation = nullptr;
            NATIVE_CONTEXT *native = PalAllocateCompleteOSContext(&allocation);
            if (!native ||
                !PalGetCompleteThreadContext(target, native) ||
                native->ctx.Rip != original.Rip ||
                native->ctx.Rsp != original.Rsp ||
                native->ctx.R12 != original.R12 ||
                !(native->ctx.ContextFlags & CONTEXT_EXCEPTION_REPORTING) ||
                GetSSP(&native->ctx) ||
                GetLastError() != 0xA2345678) {
                return 3851;
            }
            const CONTEXT saved = native->ctx;
            SetSSP(&native->ctx, 0);
            for (size_t i = 0; i < sizeof(saved); ++i) {
                if (((const WitU8 *)&saved)[i] != ((const WitU8 *)&native->ctx)[i]) {
                    return 3852;
                }
            }
            native->ctx.Dr7 = 1;
            if (PalSetThreadContext(target, native) ||
                GetLastError() != ERROR_NOT_SUPPORTED ||
                !get(target, check) ||
                !equal(original, check)) {
                return 3853;
            }
            native->ctx = saved;
            native->ctx.MxCsr ^= 0x2000;
            if (PalSetThreadContext(target, native) ||
                GetLastError() != ERROR_INVALID_PARAMETER ||
                !get(target, check) ||
                !equal(original, check)) {
                return 3854;
            }
            native->ctx = saved;
            native->ctx.ContextFlags |= CONTEXT_XSTATE;
            if (PalGetCompleteThreadContext(target, native) || GetLastError() != ERROR_NOT_SUPPORTED) {
                return 3855;
            }
            native->ctx = saved;
            native->ctx.Rip = (DWORD64)&wit_context_spin_landing;
            native->ctx.R12 = 0x778899AABBCCDD11ULL;
            native->ctx.FltSave.XmmRegisters[6].Low = 0x66778899AABBCCDDULL;
            native->ctx.MxCsr = native->ctx.FltSave.MxCsr = 0x5F80;
            HANDLE setOnly = nullptr;
            if (!DuplicateHandle(
                    GetCurrentProcess(), target, GetCurrentProcess(), &setOnly, THREAD_SET_CONTEXT, FALSE, 0)) {
                return 3856;
            }
            NATIVE_CONTEXT denied = {};
            denied.ctx.ContextFlags = CONTEXT_FULL | CONTEXT_DEBUG_REGISTERS;
            if (PalGetCompleteThreadContext(setOnly, &denied) ||
                GetLastError() != ERROR_ACCESS_DENIED ||
                !PalSetThreadContext(setOnly, native) ||
                GetLastError() != ERROR_ACCESS_DENIED ||
                !CloseHandle(setOnly)) {
                return 3857;
            }
            delete[] allocation;
            if (wit_native_call(WIT_CALL_MEMORY_RELEASE, arena, 0, 0, nullptr) != WIT_STATUS_OK) {
                return 3858;
            }
        } else {
            changed = original;
            changed.Rip = (WitU64)&wit_context_spin_landing;
            changed.R12 = 0x778899AABBCCDD11ULL;
            *(WitU64 *)&changed.FxState[256] = 0x66778899AABBCCDDULL;
            *(WitU32 *)&changed.FxState[24] = 0x5F80;
            *(WitThreadContext *)arena = changed;
            if (wit_native_call(WIT_CALL_MEMORY_PROTECT, arena, 4096, WIT_MEMORY_READ, nullptr) != WIT_STATUS_OK ||
                wit_native_call(WIT_CALL_THREAD_CONTEXT_SET, (WitU64)target, arena, sizeof(changed), nullptr) !=
                    WIT_STATUS_OK ||
                !get(target, check) ||
                !equal(changed, check) ||
                wit_native_call(WIT_CALL_MEMORY_RELEASE, arena, 0, 0, nullptr) != WIT_STATUS_OK) {
                return 3842;
            }
        }
        if (ResumeThread(target) != 1 ||
            wit_native_call(WIT_CALL_THREAD_JOIN, join, 0, 0, &result) != WIT_STATUS_OK ||
            result != WIT_TEST_EXIT_CODE ||
            wit_context_spin_result != 0x778899AABBCCDD11ULL ||
            wit_context_spin_xmm != 0x66778899AABBCCDDULL ||
            wit_context_spin_mxcsr != 0x5F80) {
            return 3843;
        }
        if (set(target, original) != WIT_STATUS_CLOSED ||
            !CloseHandle(target) ||
            set(target, original) != WIT_STATUS_BAD_HANDLE) {
            return 3844;
        }
        wit_native_tls_leave();
    }
    if (pal) {
        SetLastError(0xA2345678);
    }
    if (GetLastError() != 0xA2345678 || (tls && errno != 257)) {
        return 3845;
    }
    return WIT_TEST_EXIT_CODE;
}
