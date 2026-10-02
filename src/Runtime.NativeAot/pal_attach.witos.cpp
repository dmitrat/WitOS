#include "common.h"
#include "gcenv.h"
#include "regdisplay.h"
#include "StackFrameIterator.h"
#include "thread.h"
#include "threadstore.h"
#include "threadstore.inl"
#include "pal.witos.h"
#include "tls.h"
#include <objbase.h>
#include <errno.h>

extern volatile uint32_t *p_tls_index;
extern volatile uint32_t SECTIONREL__tls_CurrentThread;
extern "C" uint32_t _tls_index;

namespace {
// One startup attempt on the upstream finalizer thread. No gate is held while
// entering MTA; a concurrent/repeated attempt fails instead of parking or
// replacing readiness/notification state. 0 unused, 1 initializing, 2 ready, 3 failed.
volatile WitU32 attachment_state;

Thread *current_record()
{
    static_assert(sizeof(Thread) == sizeof(RuntimeThreadLocals));
    static_assert(sizeof(RuntimeThreadLocals) <= 4096 - WIT_COMPILER_TLS_DATA_OFFSET);
    WitUserThreadInfo info;
    WitU64 copied = 0;
    if (wit_native_call(WIT_CALL_THREAD_QUERY, (WitU64)&info, sizeof(info), WIT_THREAD_INFO_VERSION, &copied) !=
            WIT_STATUS_OK ||
        copied != sizeof(info) ||
        info.Version != WIT_THREAD_INFO_VERSION ||
        info.Size != sizeof(info) ||
        !info.ThreadId ||
        !info.CompilerTls ||
        (info.CompilerTls & 4095) ||
        info.CompilerTls > UINTPTR_MAX - 4096) {
        return nullptr;
    }
    // Only after the kernel confirms compiler TLS may the compiler access its
    // raw runtime record. Identity/stack authority never comes from FS/GS words.
    if (p_tls_index != &_tls_index || _tls_index != 0) {
        return nullptr;
    }
    const uintptr_t first = info.CompilerTls + WIT_COMPILER_TLS_DATA_OFFSET;
    const uintptr_t address = (uintptr_t)ThreadStore::RawGetCurrentThread();
    if (address < first ||
        address - first > 4096 - WIT_COMPILER_TLS_DATA_OFFSET - sizeof(RuntimeThreadLocals) ||
        address - first != SECTIONREL__tls_CurrentThread) {
        return nullptr;
    }
    return (Thread *)address;
}

void runtime_thread_exit(void *thread)
{
    if (attachment_state != 2 || !thread || thread != current_record()) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    // Keep the complete upstream detach path, including process-shutdown policy,
    // managed exit callback and real GC allocation-context cleanup.
    RuntimeThreadShutdown(thread);
}
}

bool PalInitComAndFlsSlot()
{
    const DWORD saved_error = GetLastError();
    if (!current_record() || !wit_native_tls_code_pointer((WitU64)&runtime_thread_exit)) {
        SetLastError(ERROR_NOT_READY);
        return false;
    }
    const int saved_errno = errno;
    if (!wit_native_claim_startup(&attachment_state)) {
        SetLastError(attachment_state == 1 ? ERROR_BUSY : ERROR_INVALID_STATE);
        return false;
    }
    const HRESULT result = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(result)) {
        attachment_state = 3;
        errno = saved_errno;
        SetLastError(result == E_OUTOFMEMORY ? ERROR_NOT_ENOUGH_MEMORY : ERROR_GEN_FAILURE);
        return false;
    }
    attachment_state = 2;
    errno = saved_errno;
    SetLastError(saved_error);
    return true;
}

void PalAttachThread(void *thread)
{
    if (attachment_state != 2 ||
        !thread ||
        thread != current_record() ||
        wit_native_thread_on_exit(runtime_thread_exit, thread) != WIT_STATUS_OK) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
}
