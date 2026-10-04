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

/* Generation-bearing identity of the calling thread; a failed query is fatal. */
static inline WitU64 wit_native_thread_identity(void)
{
    WitU64 identity = 0;
    if (wit_native_call(WIT_CALL_THREAD_CURRENT, 0, 0, 0, &identity) != WIT_STATUS_OK || !identity) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    return identity;
}
#endif
