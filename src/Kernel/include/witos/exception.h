#ifndef WITOS_EXCEPTION_H
#define WITOS_EXCEPTION_H
#include "thread_context.h"
#include "limits.h"
#define WIT_EXCEPTION_VERSION 1U
#define WIT_EXCEPTION_SIZE (WIT_THREAD_CONTEXT_SIZE + 48U)
#define WIT_EXCEPTION_STACK_MINIMUM 4096U
#define WIT_EXCEPTION_SOFTWARE_VECTOR (~0ULL)
/* An activation (THREAD_ACTIVATE): Address is the requester's callback, Error its argument and Context the context
 * the delivery interrupted; EXCEPTION_CONTINUE with that context resumes the thread where it was. */
#define WIT_EXCEPTION_ACTIVATION_VECTOR (~1ULL)
#define WIT_EXCEPTION_SOFTWARE_FAILURE_EXIT 0xFFFF0006ULL

/* Kernel-owned pending fault or activation, queried by its interrupted thread. RawState is diagnostic: the RFLAGS
 * or SPSR of the interrupted frame as the hardware saved it; the context carries the validated user profile. */
typedef struct WitUserExceptionInfo {
    WitU32 Version, Size;
    WitU64 Token, Vector, Error, Address, RawState;
    WitThreadContext Context;
} WitUserExceptionInfo;

WIT_STATIC_ASSERT(sizeof(WitUserExceptionInfo) == WIT_EXCEPTION_SIZE, "User exception snapshot ABI");
/* Atomically continue the current exception while retiring abandoned parents.
 * RetireThroughToken identifies the current record or a pending ancestor to
 * retire inclusively. Older records remain intact. Validate before mutation. */
#define WIT_EXCEPTION_TRANSFER_VERSION 1U
#define WIT_EXCEPTION_TRANSFER_SIZE (WIT_THREAD_CONTEXT_SIZE + 16U)

typedef struct WitUserExceptionTransfer {
    WitU32 Version, Size;
    WitU64 RetireThroughToken;
    WitThreadContext Context;
} WitUserExceptionTransfer;

WIT_STATIC_ASSERT(sizeof(WitUserExceptionTransfer) == WIT_EXCEPTION_TRANSFER_SIZE, "Exception transfer ABI");
#endif
