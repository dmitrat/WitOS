#ifndef WITOS_RV64_CSR_H
#define WITOS_RV64_CSR_H

#include "witos/types.h"

/* The control and status registers and the wait instruction of the RISC-V kernel, as clang inline assembly (plan step
 * V1.1), as witos/x64_instructions.h gives x64's: each that changes processor state is a compiler barrier ("memory"),
 * so no load or store moves across it. */

#define WIT_RV64_SSTATUS_SIE 0x2ULL /* supervisor interrupts enabled */

static inline WitU64 wit_rv64_read_sstatus(void)
{
    WitU64 value;
    __asm__ volatile("csrr %0, sstatus" : "=r"(value));
    return value;
}

static inline void wit_rv64_set_sstatus(WitU64 bits)
{
    __asm__ volatile("csrs sstatus, %0" : : "r"(bits) : "memory");
}

static inline void wit_rv64_clear_sstatus(WitU64 bits)
{
    __asm__ volatile("csrc sstatus, %0" : : "r"(bits) : "memory");
}

/* The address translation mode and root table: zero is the bare physical space, and a change takes effect after the
 * fence. */
static inline void wit_rv64_write_satp(WitU64 value)
{
    __asm__ volatile("csrw satp, %0" : : "r"(value) : "memory");
}

static inline void wit_rv64_fence_vma(void)
{
    __asm__ volatile("sfence.vma zero, zero" : : : "memory");
}

static inline void wit_rv64_wait_for_interrupt(void)
{
    __asm__ volatile("wfi" : : : "memory");
}

#endif
