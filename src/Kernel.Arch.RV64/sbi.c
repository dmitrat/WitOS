#include "witos/rv64_sbi.h"

/* The supervisor binary interface (plan step V1.2): one ecall to the machine-mode firmware. */

WitU64 wit_rv64_sbi(WitU64 extension, WitU64 function, WitU64 a0, WitU64 a1, WitU64 a2, WitU64 *value)
{
    register WitU64 r0 __asm__("a0") = a0;
    register WitU64 r1 __asm__("a1") = a1;
    register WitU64 r2 __asm__("a2") = a2;
    register WitU64 r6 __asm__("a6") = function;
    register WitU64 r7 __asm__("a7") = extension;
    __asm__ volatile("ecall" : "+r"(r0), "+r"(r1) : "r"(r2), "r"(r6), "r"(r7) : "memory");
    if (value) {
        *value = r1;
    }
    return r0;
}
