#ifndef WITOS_THREAD_CONTEXT_H
#define WITOS_THREAD_CONTEXT_H
#include "types.h"
#define WIT_THREAD_CONTEXT_VERSION 2U
#define WIT_THREAD_CONTEXT_SIZE 720U
#define WIT_THREAD_CONTEXT_FXSAVE64 1U
#define WIT_THREAD_CONTEXT_SUSPENDED 2U
#define WIT_THREAD_CONTEXT_SERVICE_ACTIVE 4U
#define WIT_THREAD_CONTEXT_EXCEPTION_ACTIVE 8U
#define WIT_CONTEXT_USER_FLAGS (0x200CD5ULL|0x202ULL)
#define WIT_THREAD_CONTEXT_RUNNING 1U
#define WIT_THREAD_CONTEXT_READY 2U
#define WIT_THREAD_CONTEXT_WAITING 3U
/* Atomic point-in-time snapshot. It neither suspends nor modifies the target.
 * FxState contains defined FXSAVE64 fields only; all reserved bytes are zero. */
typedef struct WitThreadContext {
    WitU32 Version,Size;
    WitU64 ThreadId,StackLow,StackHigh;
    WitU32 State,Flags;
    WitU64 Rip,Rsp,Rflags,Cs,Ss;
    WitU64 Rax,Rbx,Rcx,Rdx,Rbp,Rsi,Rdi,R8,R9,R10,R11,R12,R13,R14,R15;
    WitU32 SuspendCount,Reserved;
    WitU8 FxState[512];
} WitThreadContext;
WIT_STATIC_ASSERT(sizeof(WitThreadContext)==WIT_THREAD_CONTEXT_SIZE,"Thread context snapshot ABI");
#endif
