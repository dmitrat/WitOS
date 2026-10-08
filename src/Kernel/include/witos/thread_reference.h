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

/* Version 3 (56 bytes, K5.2c): version 2 naming the process the thread starts in. Process is WIT_PROCESS_SELF or a
 * process handle of the caller with MANAGE; the stack pointer lies in a committed writable reservation of that
 * process (a mapping the creator made into it), the entry is executable there, and the thread handle goes to the
 * caller's table while the thread runs in the named process. */
#define WIT_THREAD_CREATE_VERSION_3 3U

typedef struct WitThreadCreateRequest3 {
    WitU32 Version, Size;
    WitU64 Entry, Argument, StackPointer, TlsBase;
    WitU64 Process;
    WitU32 Flags, Reserved;
} WitThreadCreateRequest3;

WIT_STATIC_ASSERT(sizeof(WitThreadCreateRequest3) == 56, "Thread create request 3 ABI");

WIT_STATIC_ASSERT(sizeof(WitThreadCreateRequest) == 48, "Thread create request ABI");

/* The exit request (S2.1): THREAD_EXIT's third argument, zero or this structure. After the thread no longer runs,
 * the kernel writes zero to the 4-byte word at ClearAddress (a writable user word, aligned) when it is nonzero and
 * sets Event (an event handle of the caller's with SIGNAL) when it is nonzero: what a libc's thread list needs to
 * learn that a thread is gone, as Linux's CLONE_CHILD_CLEARTID does. The request is validated whole before the
 * exit, and a refused request returns. */
#define WIT_THREAD_EXIT_VERSION 1U

typedef struct WitThreadExitRequest {
    WitU32 Version, Size;
    WitU64 ClearAddress, Event;
} WitThreadExitRequest;

WIT_STATIC_ASSERT(sizeof(WitThreadExitRequest) == 24, "Thread exit request ABI");
#endif
