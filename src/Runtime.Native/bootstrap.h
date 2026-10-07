#ifndef WITOS_NATIVE_BOOTSTRAP_H
#define WITOS_NATIVE_BOOTSTRAP_H
#include "image.h"
#include "native_limits.h"

/* Private native startup helper, not NativeAOT's managed-module ABI. */
#define WIT_NATIVE_OK 0U
#define WIT_NATIVE_ALREADY_STARTED 1U
#define WIT_NATIVE_INVALID_BOOTSTRAP 2U
#define WIT_NATIVE_INITIALIZER_FAILED 3U
#define WIT_NATIVE_NEW 0U
#define WIT_NATIVE_INITIALIZING 1U
#define WIT_NATIVE_READY 2U
#define WIT_NATIVE_FINALIZING 3U
#define WIT_NATIVE_STOPPED 4U
#define WIT_NATIVE_FAILED 5U

typedef struct WitNativeModule {
    volatile WitU32 State;
    WitU32 Initialized;
    WitU64 FailureCode;
    const WitUserStartup *Startup;
    const WitUserImageInfo *Image;
} WitNativeModule;

typedef WitU64 (*WitNativeInitialize)(WitNativeModule *);
typedef void (*WitNativeCleanup)(WitNativeModule *);
typedef WitU64 (*WitNativeMain)(WitNativeModule *);

typedef struct WitNativeInitializer {
    WitNativeInitialize Initialize;
    WitNativeCleanup Cleanup;
} WitNativeInitializer;

WitU64 wit_native_module_from_address(const WitNativeModule *module, WitU64 address);
WitU64 wit_native_bootstrap(WitNativeModule *module, const WitUserStartup *startup,
    const WitNativeInitializer *initializers, WitU32 count, WitNativeMain main, WitU64 *exit_code);
int wit_native_claim_startup(volatile WitU32 *state);
/* MSVC/x64 acquire/release primitives. A caller must not park while holding
 * a lock; contenders yield so a preempted owner can resume on one CPU. */
int wit_native_try_lock(volatile WitU32 *state);
void wit_native_unlock(volatile WitU32 *state);
#define WIT_NATIVE_FAIL_FAST_EXIT 0xFFFF0001ULL
WIT_NORETURN void wit_native_fail_fast(WitU64 code);
WitU64 wit_native_call(WitU64 call, WitU64 argument0, WitU64 argument1, WitU64 argument2, WitU64 *result);

/* The helpers below keep a kernel request record on the stack. Under MSVC's /GS a record without pointers is a GS
 * buffer, and the frozen record probe links no security cookie, so the helpers and their callers in that probe opt
 * out: the kernel validates every record it reads, and the buffers never receive user input. */
#if defined(_MSC_VER) && !defined(__clang__)
#define WIT_NATIVE_SAFEBUFFERS __declspec(safebuffers)
#define WIT_NATIVE_FORCEINLINE static __forceinline
#else
#define WIT_NATIVE_SAFEBUFFERS
#define WIT_NATIVE_FORCEINLINE static inline __attribute__((always_inline))
#endif

/* The one blocking form of the lock above: contenders yield until the owner
 * releases it, and a failed yield is fatal. */
static inline void wit_native_lock(volatile WitU32 *state)
{
    while (!wit_native_try_lock(state)) {
        if (wit_native_call(WIT_CALL_THREAD_YIELD, 0, 0, 0, 0) != WIT_STATUS_OK) {
            wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
        }
    }
}

/* THREAD_QUERY of a thread handle or WIT_THREAD_SELF: the caller's Version and Size select the record. Always
 * inlined: it holds no buffer of its own, and a frame of its own in every translation unit would cost the
 * default-profile probes their 128-unwind-record quota. */
WIT_NATIVE_FORCEINLINE WitU64 wit_native_thread_query(WitU64 handle, WitUserThreadInfo *info)
{
    WitU64 copied = 0;
    info->Version = WIT_THREAD_INFO_VERSION;
    info->Size = sizeof(*info);
    const WitU64 status = wit_native_call(WIT_CALL_THREAD_QUERY, handle, (WitU64)info, sizeof(*info), &copied);
    if (status == WIT_STATUS_OK &&
        (copied != sizeof(*info) ||
            info->Version != WIT_THREAD_INFO_VERSION ||
            info->Size != sizeof(*info) ||
            !info->ThreadId)) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    return status;
}

/* Kernel record of the calling thread. Nonzero only for a complete record of this ABI version with a thread
 * identity; writable FS/GS data never defines identity or stack bounds. Callers check the further fields they rely
 * on. A macro, not a function: the default-profile probes sit at the 128-unwind-record limit, and at /Od every
 * static function with a call adds a record per translation unit. */
#define wit_native_thread_info(info) (wit_native_thread_query(WIT_THREAD_SELF, (info)) == WIT_STATUS_OK)

/* THREAD_CREATE, the one form, with the fixed stack and no native-id output; flags are the request's 32 bits, and
 * a wider value is as invalid as an unknown bit. */
WIT_NATIVE_SAFEBUFFERS static inline WitU64 wit_native_thread_start(
    WitU64 entry, WitU64 argument, WitU64 flags, WitU64 *handle)
{
    WitThreadCreateRequest request = {
        WIT_THREAD_CREATE_VERSION, sizeof(request), entry, argument, 0, 0, (WitU32)flags, 0};
    if (flags >> 32) {
        if (handle) {
            *handle = 0;
        }
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    return wit_native_call(WIT_CALL_THREAD_CREATE, (WitU64)&request, sizeof(request), 0, handle);
}

/* Join: wait for the thread the handle observes, read its exit code and close the handle. A failed wait leaves the
 * handle to the caller and a zero code; the code pointer is optional. */
WIT_NATIVE_SAFEBUFFERS static inline WitU64 wit_native_thread_join(WitU64 handle, WitU64 *code)
{
    WitUserWaitRequest wait = {WIT_WAIT_OBJECTS_VERSION, sizeof(wait), (WitU64)&handle, 1, 0, WIT_WAIT_INFINITE};
    WitUserThreadInfo info;
    if (code) {
        *code = 0;
    }
    WitU64 status = wit_native_call(WIT_CALL_OBJECT_WAIT, (WitU64)&wait, sizeof(wait), 0, 0);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    status = wit_native_thread_query(handle, &info);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    if (info.State != WIT_THREAD_STATE_EXITED) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    if (code) {
        *code = info.ExitCode;
    }
    return wit_native_call(WIT_CALL_HANDLE_CLOSE, handle, 0, 0, 0);
}

/* The prefix of a thread context (identity, stack bounds, state, flags) from the thread's record, for the frozen
 * line's context conversions; the registers stay zero. */
WIT_NATIVE_SAFEBUFFERS static inline WitU64 wit_native_context_prefix(WitU64 handle, WitThreadContext *context)
{
    WitUserThreadInfo info;
    const WitU64 status = wit_native_thread_query(handle, &info);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    for (WitU32 i = 0; i < sizeof(*context); ++i) {
        ((WitU8 *)context)[i] = 0;
    }
    context->Version = WIT_THREAD_CONTEXT_VERSION;
    context->Size = sizeof(*context);
    context->ThreadId = info.ThreadId;
    context->StackLow = info.StackLow;
    context->StackHigh = info.StackHigh;
    context->State = info.State == WIT_THREAD_STATE_RUNNING ? WIT_THREAD_CONTEXT_RUNNING
        : info.State == WIT_THREAD_STATE_WAITING            ? WIT_THREAD_CONTEXT_WAITING
                                                            : WIT_THREAD_CONTEXT_READY;
    context->Flags = info.ContextFlags;
    context->SuspendCount = info.SuspendCount;
    return WIT_STATUS_OK;
}

/* Generation-bearing identity of the calling thread from its kernel record; a failed query is fatal. */
WIT_NATIVE_SAFEBUFFERS static inline WitU64 wit_native_thread_identity(void)
{
    WitUserThreadInfo info;
    if (!wit_native_thread_info(&info)) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    return info.ThreadId;
}

/* The one wait of ABI-1 (RFC 0011 section 7.4) on several handles or one; the deadline is an absolute monotonic
 * count, WIT_WAIT_INFINITE waits forever and zero polls. The winner's index is optional. An activation delivered to
 * the thread ends the wait with INTERRUPTED after its handler ran; the frozen line restarts the wait at its absolute
 * deadline, as a libc restarts a wait a signal interrupted. */
WIT_NATIVE_SAFEBUFFERS static inline WitU64 wit_native_wait_any(
    const WitU64 *handles, WitU32 count, WitU64 deadline, WitU64 *index)
{
    WitUserWaitRequest request = {WIT_WAIT_OBJECTS_VERSION, sizeof(request), (WitU64)handles, count, 0, deadline};
    WitU64 winner = 0;
    WitU64 status;
    do {
        winner = 0;
        status = wit_native_call(WIT_CALL_OBJECT_WAIT, (WitU64)&request, sizeof(request), 0, &winner);
    } while (status == WIT_STATUS_INTERRUPTED);
    if (index) {
        *index = winner;
    }
    return status;
}

static inline WitU64 wit_native_wait_one(WitU64 handle, WitU64 deadline)
{
    return wit_native_wait_any(&handle, 1, deadline, 0);
}

/* The monotonic clock, and the counts of a number of 10 ms scheduler ticks, in which the fixtures measure delays. */
static inline WitU64 wit_native_clock_read(WitU64 *now)
{
    return wit_native_call(WIT_CALL_CLOCK_READ, WIT_CLOCK_MONOTONIC, 0, 0, now);
}

static inline WitU64 wit_native_tick_counts(WitU64 ticks)
{
    WitU64 frequency = 0;
    if (wit_native_call(WIT_CALL_CLOCK_FREQUENCY, WIT_CLOCK_MONOTONIC, 0, 0, &frequency) != WIT_STATUS_OK ||
        !frequency) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    return frequency / 100 * ticks;
}

/* Sleeps to an absolute deadline; a sleep an activation interrupted is restarted at the same deadline. */
static inline WitU64 wit_native_sleep_until(WitU64 deadline)
{
    WitU64 status;
    do {
        status = wit_native_call(WIT_CALL_SLEEP_UNTIL, deadline, 0, 0, 0);
    } while (status == WIT_STATUS_INTERRUPTED);
    return status;
}

static inline WitU64 wit_native_sleep_ticks(WitU64 ticks)
{
    WitU64 now = 0;
    const WitU64 status = wit_native_clock_read(&now);
    return status != WIT_STATUS_OK ? status : wit_native_sleep_until(now + wit_native_tick_counts(ticks));
}
#endif
