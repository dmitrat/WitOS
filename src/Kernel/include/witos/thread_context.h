#ifndef WITOS_THREAD_CONTEXT_H
#define WITOS_THREAD_CONTEXT_H
#include "types.h"
#define WIT_THREAD_CONTEXT_VERSION 2U
/* Flags: the register block present (FXSAVE64 on x64, FPSIMD on ARM64; CONTEXT_PROFILE reports the same) and the
 * thread's suspension, wait and delivery state. */
#define WIT_THREAD_CONTEXT_FXSAVE64 1U
#define WIT_THREAD_CONTEXT_SUSPENDED 2U
#define WIT_THREAD_CONTEXT_SERVICE_ACTIVE 4U
#define WIT_THREAD_CONTEXT_EXCEPTION_ACTIVE 8U
#define WIT_THREAD_CONTEXT_FPSIMD 16U
#define WIT_THREAD_CONTEXT_RUNNING 1U
#define WIT_THREAD_CONTEXT_READY 2U
#define WIT_THREAD_CONTEXT_WAITING 3U
/* The size of a context with each register block (RFC 0011 section 7.3: only the block differs between ISAs);
 * WIT_THREAD_CONTEXT_SIZE is the compiling ISA's. */
#define WIT_THREAD_CONTEXT_SIZE_X64 720U
#define WIT_THREAD_CONTEXT_SIZE_ARM64 848U

#if defined(_M_ARM64) || defined(__aarch64__)
#define WIT_THREAD_CONTEXT_SIZE (WIT_THREAD_CONTEXT_SIZE_ARM64)
/* The PSTATE bits a user context carries: the condition flags NZCV. The mode is EL0t with every exception unmasked,
 * and the kernel refuses a context with any other bit set. */
#define WIT_CONTEXT_USER_PSTATE 0xF0000000ULL

/* Atomic point-in-time snapshot, the AArch64 block: X0-X30, SP, PC, PSTATE, the 32 vector registers as 16-byte
 * lanes of V, FPCR and FPSR. TPIDR_EL0 is per-thread frame state the kernel keeps outside the context, and x18 is
 * the compiler TLS register the kernel sets on every return to EL0, so a context reports it but cannot change it.
 * The snapshot neither suspends nor modifies the target. */
typedef struct WitThreadContext {
    WitU32 Version, Size;
    WitU64 ThreadId, StackLow, StackHigh;
    WitU32 State, Flags;
    WitU64 X[31];
    WitU64 Sp, Pc, Pstate;
    WitU32 SuspendCount, Reserved;
    WitU64 Fpcr, Fpsr;
    WitU8 V[512];
} WitThreadContext;
#else
#define WIT_THREAD_CONTEXT_SIZE (WIT_THREAD_CONTEXT_SIZE_X64)
#define WIT_CONTEXT_USER_FLAGS (0x200CD5ULL | 0x202ULL)

/* Atomic point-in-time snapshot, the x64 block: the general registers, RIP, RSP, RFLAGS, the segment selectors and
 * an FXSAVE64 image whose reserved bytes are zero. It neither suspends nor modifies the target. */
typedef struct WitThreadContext {
    WitU32 Version, Size;
    WitU64 ThreadId, StackLow, StackHigh;
    WitU32 State, Flags;
    WitU64 Rip, Rsp, Rflags, Cs, Ss;
    WitU64 Rax, Rbx, Rcx, Rdx, Rbp, Rsi, Rdi, R8, R9, R10, R11, R12, R13, R14, R15;
    WitU32 SuspendCount, Reserved;
    WitU8 FxState[512];
} WitThreadContext;
#endif

WIT_STATIC_ASSERT(sizeof(WitThreadContext) == WIT_THREAD_CONTEXT_SIZE, "Thread context snapshot ABI");
#endif
