#ifndef WITOS_RV64_CSR_H
#define WITOS_RV64_CSR_H

#include "witos/types.h"

/* The control and status registers, fences and the wait instruction of the RISC-V kernel, as clang inline assembly
 * (plan steps V1.1, V1.2), as witos/x64_instructions.h gives x64's: each that changes processor state or reaches a
 * device is a compiler barrier ("memory"), so no load or store moves across it. The sstc timer compare register is
 * named by its number, since the assembler knows its name under the extension alone. */

/* sie and sip bits: the supervisor software, timer and external interrupts. */
#define WIT_RV64_SIE_SOFTWARE (1ULL << 1)
#define WIT_RV64_SIE_TIMER (1ULL << 5)
#define WIT_RV64_SIE_EXTERNAL (1ULL << 9)
#define WIT_RV64_SATP_SV48 (9ULL << 60)
#define WIT_RV64_CSR_STIMECMP 0x14D

static inline WitU64 wit_rv64_read_sstatus(void)
{
    WitU64 value;
    __asm__ volatile("csrr %0, sstatus" : "=r"(value));
    return value;
}

static inline void wit_rv64_write_sstatus(WitU64 value)
{
    __asm__ volatile("csrw sstatus, %0" : : "r"(value) : "memory");
}

static inline void wit_rv64_set_sstatus(WitU64 bits)
{
    __asm__ volatile("csrs sstatus, %0" : : "r"(bits) : "memory");
}

static inline void wit_rv64_clear_sstatus(WitU64 bits)
{
    __asm__ volatile("csrc sstatus, %0" : : "r"(bits) : "memory");
}

static inline WitU64 wit_rv64_read_sie(void)
{
    WitU64 value;
    __asm__ volatile("csrr %0, sie" : "=r"(value));
    return value;
}

static inline void wit_rv64_set_sie(WitU64 bits)
{
    __asm__ volatile("csrs sie, %0" : : "r"(bits) : "memory");
}

static inline void wit_rv64_clear_sie(WitU64 bits)
{
    __asm__ volatile("csrc sie, %0" : : "r"(bits) : "memory");
}

static inline WitU64 wit_rv64_read_sip(void)
{
    WitU64 value;
    __asm__ volatile("csrr %0, sip" : "=r"(value));
    return value;
}

static inline void wit_rv64_write_stvec(WitU64 value)
{
    __asm__ volatile("csrw stvec, %0" : : "r"(value) : "memory");
}

static inline WitU64 wit_rv64_read_stvec(void)
{
    WitU64 value;
    __asm__ volatile("csrr %0, stvec" : "=r"(value));
    return value;
}

static inline void wit_rv64_write_sscratch(WitU64 value)
{
    __asm__ volatile("csrw sscratch, %0" : : "r"(value) : "memory");
}

static inline WitU64 wit_rv64_read_sscratch(void)
{
    WitU64 value;
    __asm__ volatile("csrr %0, sscratch" : "=r"(value));
    return value;
}

static inline void wit_rv64_write_scounteren(WitU64 value)
{
    __asm__ volatile("csrw scounteren, %0" : : "r"(value) : "memory");
}

/* The address translation mode and root table: zero is the bare physical space, and a change takes effect after the
 * fence. */
static inline void wit_rv64_write_satp(WitU64 value)
{
    __asm__ volatile("csrw satp, %0" : : "r"(value) : "memory");
}

static inline WitU64 wit_rv64_read_satp(void)
{
    WitU64 value;
    __asm__ volatile("csrr %0, satp" : "=r"(value));
    return value;
}

static inline void wit_rv64_fence_vma(void)
{
    __asm__ volatile("sfence.vma zero, zero" : : : "memory");
}

static inline void wit_rv64_fence_vma_address(WitU64 address)
{
    __asm__ volatile("sfence.vma %0, zero" : : "r"(address) : "memory");
}

/* The instruction fetch sees the stores before it (code publication). */
static inline void wit_rv64_fence_instructions(void)
{
    __asm__ volatile("fence rw, rw\n\tfence.i" : : : "memory");
}

/* A full memory fence. */
static inline void wit_rv64_fence(void)
{
    __asm__ volatile("fence rw, rw" : : : "memory");
}

/* The time counter the platform's clock reads (the timebase the device tree names). */
static inline WitU64 wit_rv64_read_time(void)
{
    WitU64 value;
    __asm__ volatile("rdtime %0" : "=r"(value));
    return value;
}

/* The sstc timer compare register: a supervisor timer interrupt is pending while time >= stimecmp. */
static inline void wit_rv64_write_stimecmp(WitU64 value)
{
    __asm__ volatile("csrw 0x14D, %0" : : "r"(value) : "memory");
}

static inline WitU64 wit_rv64_read_fcsr(void)
{
    WitU64 value;
    __asm__ volatile("frcsr %0" : "=r"(value));
    return value;
}

static inline void wit_rv64_wait_for_interrupt(void)
{
    __asm__ volatile("wfi" : : : "memory");
}

#endif
