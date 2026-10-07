#ifndef WITOS_THREAD_REFERENCE_H
#define WITOS_THREAD_REFERENCE_H
#include "types.h"
#include "limits.h"
/* Pseudo-handle of the calling thread for every call that takes a thread handle. */
#define WIT_THREAD_SELF (~1ULL)

/* THREAD_CREATE: the one form. The fixed-stack backend accepts zero or a supported stack size; the native-id output
 * is optional and validated as a whole. The handle observes the thread's lifetime; it is not a consuming join. */
#define WIT_THREAD_CREATE_VERSION 1U
#define WIT_THREAD_START_SUSPENDED 1U

typedef struct WitThreadCreateRequest {
    WitU32 Version, Size;
    WitU64 Entry, Argument, StackBytes, NativeIdOutput;
    WitU32 Flags, Reserved;
} WitThreadCreateRequest;

WIT_STATIC_ASSERT(sizeof(WitThreadCreateRequest) == 48, "Thread create request ABI");
#endif
