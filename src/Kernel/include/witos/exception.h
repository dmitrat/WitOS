#ifndef WITOS_EXCEPTION_H
#define WITOS_EXCEPTION_H
#include "thread_context.h"
#include "limits.h"
#define WIT_EXCEPTION_VERSION 1U
#define WIT_EXCEPTION_SIZE 768U
#define WIT_EXCEPTION_STACK_MINIMUM 4096U
#define WIT_EXCEPTION_SOFTWARE_VECTOR (~0ULL)
#define WIT_EXCEPTION_SOFTWARE_FAILURE_EXIT 0xFFFF0006ULL

/* Kernel-owned pending fault, queried by its interrupted thread. RawRflags is
 * diagnostic; Context.Rflags follows the existing validated return profile. */
typedef struct WitUserExceptionInfo {
    WitU32 Version, Size;
    WitU64 Token, Vector, Error, Address, RawRflags;
    WitThreadContext Context;
} WitUserExceptionInfo;

WIT_STATIC_ASSERT(sizeof(WitUserExceptionInfo) == WIT_EXCEPTION_SIZE, "User exception snapshot ABI");
/* Atomically continue the current exception while retiring abandoned parents.
 * RetireThroughToken identifies the current record or a pending ancestor to
 * retire inclusively. Older records remain intact. Validate before mutation. */
#define WIT_EXCEPTION_TRANSFER_VERSION 1U
#define WIT_EXCEPTION_TRANSFER_SIZE 736U

typedef struct WitUserExceptionTransfer {
    WitU32 Version, Size;
    WitU64 RetireThroughToken;
    WitThreadContext Context;
} WitUserExceptionTransfer;

WIT_STATIC_ASSERT(sizeof(WitUserExceptionTransfer) == WIT_EXCEPTION_TRANSFER_SIZE, "Exception transfer ABI");
#endif
