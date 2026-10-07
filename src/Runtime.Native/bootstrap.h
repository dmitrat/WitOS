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
#else
#define WIT_NATIVE_SAFEBUFFERS
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

/* Kernel record of the calling thread. Nonzero only for a complete record of
 * this ABI version with a thread identity; writable FS/GS data never defines
 * identity or stack bounds. Callers check the further fields they rely on. */
static inline int wit_native_thread_info(WitUserThreadInfo *info)
{
    WitU64 copied = 0;
    return wit_native_call(WIT_CALL_THREAD_QUERY, (WitU64)info, sizeof(*info), WIT_THREAD_INFO_VERSION, &copied) ==
        WIT_STATUS_OK &&
        copied == sizeof(*info) &&
        info->Version == WIT_THREAD_INFO_VERSION &&
        info->Size == sizeof(*info) &&
        info->ThreadId;
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
 * count, WIT_WAIT_INFINITE waits forever and zero polls. The winner's index is optional. */
WIT_NATIVE_SAFEBUFFERS static inline WitU64 wit_native_wait_any(
    const WitU64 *handles, WitU32 count, WitU64 deadline, WitU64 *index)
{
    WitUserWaitRequest request = {WIT_WAIT_OBJECTS_VERSION, sizeof(request), (WitU64)handles, count, 0, deadline};
    WitU64 winner = 0;
    const WitU64 status = wit_native_call(WIT_CALL_OBJECT_WAIT, (WitU64)&request, sizeof(request), 0, &winner);
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

static inline WitU64 wit_native_sleep_ticks(WitU64 ticks)
{
    WitU64 now = 0;
    const WitU64 status = wit_native_clock_read(&now);
    return status != WIT_STATUS_OK
        ? status
        : wit_native_call(WIT_CALL_SLEEP_UNTIL, now + wit_native_tick_counts(ticks), 0, 0, 0);
}
#endif
