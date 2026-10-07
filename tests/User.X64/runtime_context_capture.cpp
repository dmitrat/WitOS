#include "pal.witos.h"
#include "native_security.h"
#include "tls.h"
#include "../User/protocol.h"
#include <errno.h>
extern "C" WitU64 wit_test_context_registers(WitU64, WitU64, WitU64, WitU64);
static volatile HANDLE targetReference;
static HANDLE eventHandle;

static bool get(WitU64 handle, WitThreadContext &context)
{
    return wit_native_call(WIT_CALL_THREAD_CONTEXT_GET, handle, (WitU64)&context, sizeof(context), nullptr) ==
        WIT_STATUS_OK;
}

static bool valid(const WitThreadContext &c, WitU64 id)
{
    if (c.Version != WIT_THREAD_CONTEXT_VERSION ||
        c.Size != sizeof(c) ||
        c.ThreadId != id ||
        (c.Flags & ~WIT_THREAD_CONTEXT_SERVICE_ACTIVE) != WIT_THREAD_CONTEXT_FXSAVE64 ||
        c.Reserved ||
        c.SuspendCount ||
        c.Rsp < c.StackLow ||
        c.Rsp >= c.StackHigh ||
        (c.Cs & 3) != 3 ||
        (c.Ss & 3) != 3 ||
        c.FxState[5] ||
        (c.FxState[7] & 0xF8)) {
        return false;
    }
    for (unsigned i = 32; i < 160; ++i) {
        if ((i - 32) % 16 >= 10 && c.FxState[i]) {
            return false;
        }
    }
    for (unsigned i = 416; i < 512; ++i) {
        if (c.FxState[i]) {
            return false;
        }
    }
    return true;
}

static bool sentinels(const WitThreadContext &c)
{
    return c.R12 == 0x55AA001122334455ULL &&
        c.R13 == 0xAA55112233445566ULL &&
        *(const WitU64 *)&c.FxState[256] == 0x55AA001122334455ULL &&
        *(const WitU64 *)&c.FxState[264] == 0x55AA001122334455ULL &&
        *(const WitU32 *)&c.FxState[24] == 0x7F80 &&
        c.FxState[0] == 0x7F &&
        c.FxState[1] == 3 &&
        c.FxState[4] == 0x80 &&
        *(const WitU64 *)&c.FxState[32] == 0x8000000000000000ULL &&
        c.FxState[40] == 0xFF &&
        c.FxState[41] == 0x3F;
}

static WitU64 worker(WitU64)
{
    HANDLE reference = nullptr;
    if (!DuplicateHandle(GetCurrentProcess(), GetCurrentThread(), GetCurrentProcess(), &reference, 0, FALSE,
            DUPLICATE_SAME_ACCESS)) {
        return 3601;
    }
    targetReference = reference;
    static WitU64 waitHandle;
    static WitUserWaitRequest waitRequest;
    waitHandle = (WitU64)eventHandle;
    waitRequest = {WIT_WAIT_OBJECTS_VERSION, sizeof(waitRequest), (WitU64)&waitHandle, 1, 0, WIT_WAIT_INFINITE};
    if (wit_test_context_registers(WIT_CALL_OBJECT_WAIT, (WitU64)&waitRequest, sizeof(waitRequest), 0) !=
        WIT_STATUS_OK) {
        return 3602;
    }
    return WIT_TEST_EXIT_CODE;
}

extern "C" WitU64 wit_test_context_capture(const WitUserStartup *startup, WitU64 mode)
{
    auto report = (WitU64 *)WIT_GC_INFO_REPORT;
    report[0] = mode;
    report[1] = 1099511627776ULL;
    wit_native_security_initialize_system();
    wit_native_process_image_initialize(startup);
    const bool tls = mode == 89;
    if (tls) {
        wit_native_tls_initialize(startup);
    }
    SetLastError(0x81234567);
    if (tls) {
        errno = 237;
    }
    WitUserThreadInfo thread;
    if (wit_native_thread_query(WIT_THREAD_SELF, &thread) != WIT_STATUS_OK) {
        return 3603;
    }
    WitThreadContext c;
    if (wit_test_context_registers(WIT_CALL_THREAD_CONTEXT_GET, WIT_THREAD_REFERENCE_CURRENT, (WitU64)&c, sizeof(c)) !=
            WIT_STATUS_OK ||
        !valid(c, thread.ThreadId) ||
        !sentinels(c) ||
        c.State != WIT_THREAD_CONTEXT_RUNNING ||
        c.StackLow != thread.StackLow ||
        c.StackHigh != thread.StackHigh) {
        return 3604;
    }
    WitU64 getOnly = 0, queryOnly = 0, setOnly = 0;
    if (wit_native_call(WIT_CALL_THREAD_REFERENCE_DUPLICATE, WIT_THREAD_REFERENCE_CURRENT, (WitU64)&getOnly,
            WIT_THREAD_REFERENCE_GET_CONTEXT, nullptr) != WIT_STATUS_OK ||
        wit_native_call(WIT_CALL_THREAD_REFERENCE_DUPLICATE, WIT_THREAD_REFERENCE_CURRENT, (WitU64)&queryOnly,
            WIT_THREAD_REFERENCE_QUERY, nullptr) != WIT_STATUS_OK ||
        wit_native_call(WIT_CALL_THREAD_REFERENCE_DUPLICATE, WIT_THREAD_REFERENCE_CURRENT, (WitU64)&setOnly,
            WIT_THREAD_REFERENCE_SET_CONTEXT, nullptr) != WIT_STATUS_OK ||
        !get(getOnly, c) ||
        !valid(c, thread.ThreadId)) {
        return 3605;
    }
    for (size_t i = 0; i < sizeof(c); ++i) {
        ((WitU8 *)&c)[i] = 0x5A;
    }
    if (wit_native_call(WIT_CALL_THREAD_CONTEXT_GET, queryOnly, (WitU64)&c, sizeof(c), nullptr) != WIT_STATUS_DENIED ||
        wit_native_call(WIT_CALL_THREAD_CONTEXT_GET, setOnly, (WitU64)&c, sizeof(c), nullptr) != WIT_STATUS_DENIED ||
        wit_native_call(WIT_CALL_THREAD_CONTEXT_GET, thread.ThreadId, (WitU64)&c, sizeof(c), nullptr) !=
            WIT_STATUS_WRONG_TYPE ||
        wit_native_call(WIT_CALL_THREAD_CONTEXT_GET, getOnly, (WitU64)&c, sizeof(c) - 1, nullptr) !=
            WIT_STATUS_INVALID_ARGUMENT) {
        return 3606;
    }
    for (size_t i = 0; i < sizeof(c); ++i) {
        if (((WitU8 *)&c)[i] != 0x5A) {
            return 3607;
        }
    }
    auto raw = (WitU64 *)thread.RawTls;
    const WitU64 saved = raw[1];
    raw[1] = ~saved;
    const bool identity = get(WIT_THREAD_REFERENCE_CURRENT, c) && valid(c, thread.ThreadId);
    raw[1] = saved;
    if (!identity) {
        return 3608;
    }
    WitU64 arena = 0;
    if (wit_native_call(WIT_CALL_MEMORY_RESERVE, 8192, 4096, 0, &arena) != WIT_STATUS_OK ||
        wit_native_call(WIT_CALL_MEMORY_COMMIT, arena, 4096, 3, nullptr) != WIT_STATUS_OK) {
        return 3609;
    }
    auto tail = (WitU8 *)(arena + 4092);
    for (unsigned i = 0; i < 4; ++i) {
        tail[i] = 0x5A;
    }
    if (wit_native_call(WIT_CALL_THREAD_CONTEXT_GET, getOnly, (WitU64)tail, sizeof(c), nullptr) !=
            WIT_STATUS_BAD_ADDRESS ||
        wit_native_call(WIT_CALL_THREAD_CONTEXT_GET, getOnly, startup->ImageInfo, sizeof(c), nullptr) !=
            WIT_STATUS_BAD_ADDRESS) {
        return 3610;
    }
    for (unsigned i = 0; i < 4; ++i) {
        if (tail[i] != 0x5A) {
            return 3611;
        }
    }
    auto exact = (WitThreadContext *)(arena + 4096 - sizeof(c));
    if (!get(getOnly, *exact) ||
        !valid(*exact, thread.ThreadId) ||
        wit_native_call(WIT_CALL_MEMORY_RELEASE, arena, 0, 0, nullptr) != WIT_STATUS_OK ||
        !CloseHandle((HANDLE)getOnly) ||
        !CloseHandle((HANDLE)queryOnly) ||
        !CloseHandle((HANDLE)setOnly)) {
        return 3612;
    }
    if (tls) {
        eventHandle = CreateEventExW(nullptr, nullptr, 0, SYNCHRONIZE | EVENT_MODIFY_STATE);
        if (!eventHandle) {
            return 3613;
        }
        WitU64 priorId = 0;
        HANDLE oldReference = nullptr;
        for (unsigned pass = 0; pass < 3; ++pass) {
            targetReference = nullptr;
            WitU64 join = 0, result = 0;
            if (wit_native_thread_create(worker, 0, &join) != WIT_STATUS_OK) {
                return 3614;
            }
            WitUserThreadInfo target = {};
            bool parked = false;
            for (unsigned attempt = 0; attempt < 10000; ++attempt) {
                const HANDLE reference = targetReference;
                if (reference &&
                    wit_native_thread_query((WitU64)reference, &target) == WIT_STATUS_OK &&
                    target.State == WIT_THREAD_STATE_WAITING) {
                    parked = true;
                    break;
                }
                wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, nullptr);
            }
            if (!parked ||
                target.ThreadId == priorId ||
                !get((WitU64)targetReference, c) ||
                !valid(c, target.ThreadId) ||
                !sentinels(c) ||
                c.State != WIT_THREAD_CONTEXT_WAITING ||
                c.StackLow != target.StackLow ||
                c.StackHigh != target.StackHigh) {
                return 3615;
            }
            priorId = target.ThreadId;
            if (oldReference &&
                wit_native_call(WIT_CALL_THREAD_CONTEXT_GET, (WitU64)oldReference, (WitU64)&c, sizeof(c), nullptr) !=
                    WIT_STATUS_BAD_HANDLE) {
                return 3616;
            }
            if (!SetEvent(eventHandle) ||
                wit_native_thread_join(join, &result) != WIT_STATUS_OK ||
                result != WIT_TEST_EXIT_CODE) {
                return 3617;
            }
            for (size_t i = 0; i < sizeof(c); ++i) {
                ((WitU8 *)&c)[i] = 0x5A;
            }
            if (wit_native_call(WIT_CALL_THREAD_CONTEXT_GET, (WitU64)targetReference, (WitU64)&c, sizeof(c), nullptr) !=
                WIT_STATUS_CLOSED) {
                return 3618;
            }
            for (size_t i = 0; i < sizeof(c); ++i) {
                if (((WitU8 *)&c)[i] != 0x5A) {
                    return 3619;
                }
            }
            oldReference = targetReference;
            if (!CloseHandle(oldReference)) {
                return 3620;
            }
        }
        if (!CloseHandle(eventHandle)) {
            return 3621;
        }
        wit_native_tls_leave();
    }
    if (GetLastError() != 0x81234567 || (tls && errno != 237)) {
        return 3622;
    }
    return WIT_TEST_EXIT_CODE;
}
