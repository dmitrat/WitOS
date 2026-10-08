#ifndef WITOS_THREAD_REFERENCE_H
#define WITOS_THREAD_REFERENCE_H
#include "types.h"
#include "limits.h"
/* Pseudo-handle of the calling thread for every call that takes a thread handle. */
#define WIT_THREAD_SELF (~1ULL)

/* THREAD_CREATE: the one form. The fixed-stack backend accepts zero or a supported stack size; the native-id output
 * is optional and validated as a whole. The handle observes the thread's lifetime; it is not a consuming join. */
#define WIT_THREAD_CREATE_VERSION 1U /* the kernel's stack and TLS: the frozen line's form, leaves at K8 */
#define WIT_THREAD_CREATE_VERSION_2 \
    2U /* the one form (RFC 0011 section 7.3): the caller's stack pointer and TLS base */
#define WIT_THREAD_START_SUSPENDED 1U

typedef struct WitThreadCreateRequest {
    WitU32 Version, Size;
    WitU64 Entry, Argument, StackBytes, NativeIdOutput;
    WitU32 Flags, Reserved;
} WitThreadCreateRequest;

/* Version 2, the same size: the thread starts at Entry with Argument in the argument registers of both entry
 * conventions, its stack pointer exactly StackPointer (16-byte aligned, inside a committed writable reservation
 * of the caller, whose bounds become the thread's) and its raw TLS base TlsBase (FS on x64, TPIDRRO_EL0 on ARM64;
 * a user address or zero). The kernel maps no stack and no TLS page for it; Flags is SUSPENDED or zero. */
typedef struct WitThreadCreateRequest2 {
    WitU32 Version, Size;
    WitU64 Entry, Argument, StackPointer, TlsBase;
    WitU32 Flags, Reserved;
} WitThreadCreateRequest2;

WIT_STATIC_ASSERT(sizeof(WitThreadCreateRequest2) == 48, "Thread create request 2 ABI");

WIT_STATIC_ASSERT(sizeof(WitThreadCreateRequest) == 48, "Thread create request ABI");
#endif
