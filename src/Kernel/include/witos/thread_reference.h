#ifndef WITOS_THREAD_REFERENCE_H
#define WITOS_THREAD_REFERENCE_H
#include "types.h"
#include "limits.h"
#define WIT_THREAD_REFERENCE_VERSION 3U
/* Pseudo-handle of the calling thread for every call that takes a thread handle. */
#define WIT_THREAD_SELF (~1ULL)
#define WIT_THREAD_REFERENCE_QUERY 16U
#define WIT_THREAD_REFERENCE_WAIT 4U
#define WIT_THREAD_REFERENCE_GET_CONTEXT 32U
#define WIT_THREAD_REFERENCE_SET_CONTEXT 64U
#define WIT_THREAD_REFERENCE_SUSPEND_RESUME 128U
#define WIT_THREAD_REFERENCE_ALL 244U
#define WIT_THREAD_REFERENCE_LIVE 1U
#define WIT_THREAD_REFERENCE_EXITED 2U
#define WIT_THREAD_REFERENCE_WAITING 3U
#define WIT_THREAD_REFERENCE_SUSPENDED 4U

typedef struct WitThreadReferenceInfo {
    WitU32 Version, Size;
    WitU64 ThreadId, ExitCode, StackLow, StackHigh;
    WitU32 State, Rights;
    WitU32 SuspendCount, Reserved;
} WitThreadReferenceInfo;

WIT_STATIC_ASSERT(sizeof(WitThreadReferenceInfo) == 56, "Thread reference snapshot ABI");
/* Atomic reference-bearing thread creation. The fixed-stack backend accepts
 * zero or a supported stack size; ID output is optional and fully validated.
 * A reference observes lifetime; it is not a consuming join capability. */
#define WIT_THREAD_CREATE_REFERENCE_VERSION 1U
#define WIT_THREAD_START_SUSPENDED 1U

typedef struct WitThreadCreateRequest {
    WitU32 Version, Size;
    WitU64 Entry, Argument, StackBytes, NativeIdOutput;
    WitU32 Flags, Reserved;
} WitThreadCreateRequest;

WIT_STATIC_ASSERT(sizeof(WitThreadCreateRequest) == 48, "Thread create request ABI");
#endif
