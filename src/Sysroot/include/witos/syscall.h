#ifndef WITOS_SYSCALL_H
#define WITOS_SYSCALL_H
#include "witos/types.h"

/* The transport of ABI-1 for layer 2 (RFC 0011 section 6.1, plan step T1): one system call takes the call number
 * and at most three register arguments and returns a status and a result. On x64 the instruction is SYSCALL with
 * the number in RAX, the arguments in RDI, RSI and RDX, the status in RAX and the result in RDX; the instruction
 * clobbers RCX and R11 and the kernel preserves the other registers. On ARM64 the instruction is SVC #0 with the
 * number in x8, the arguments in x0, x1 and x2, the status in x0 and the result in x1; the kernel clears the
 * condition flags. The register roles are those of Linux so that a libc's stubs (step S1) differ from musl's by the
 * status and result registers alone. A status is a small nonnegative integer, never a negative errno. */

static inline WitU64 wit_syscall(WitU64 number, WitU64 a0, WitU64 a1, WitU64 a2, WitU64 *result)
{
#if defined(__x86_64__)
    WitU64 status = number, value = a2;
    __asm__ volatile("syscall" : "+a"(status), "+d"(value) : "D"(a0), "S"(a1) : "rcx", "r11", "memory");
    *result = value;
    return status;
#elif defined(__aarch64__)
    register WitU64 x0 __asm__("x0") = a0;
    register WitU64 x1 __asm__("x1") = a1;
    register WitU64 x2 __asm__("x2") = a2;
    register WitU64 x8 __asm__("x8") = number;
    __asm__ volatile("svc #0" : "+r"(x0), "+r"(x1) : "r"(x2), "r"(x8) : "memory", "cc");
    *result = x1;
    return x0;
#else
#error "WitOS ABI-1 transport: unsupported architecture"
#endif
}

#endif
