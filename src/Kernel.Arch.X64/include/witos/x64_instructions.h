#ifndef WITOS_X64_INSTRUCTIONS_H
#define WITOS_X64_INSTRUCTIONS_H

#include "witos/types.h"

/* The privileged, port and identification instructions of the x64 kernel and the q35 board, as clang inline assembly
 * (plan step T3.1): what MSVC's intrinsics gave before. Each instruction that changes processor state or reaches a
 * device is a compiler barrier ("memory"), so no load or store moves across it. */

static inline void wit_x64_disable_interrupts(void)
{
    __asm__ volatile("cli" ::: "memory");
}

static inline void wit_x64_enable_interrupts(void)
{
    __asm__ volatile("sti" ::: "memory");
}

static inline void wit_x64_halt(void)
{
    __asm__ volatile("hlt" ::: "memory");
}

static inline WitU64 wit_x64_read_cr0(void)
{
    WitU64 value;
    __asm__ volatile("mov %%cr0, %0" : "=r"(value));
    return value;
}

static inline WitU64 wit_x64_read_cr3(void)
{
    WitU64 value;
    __asm__ volatile("mov %%cr3, %0" : "=r"(value));
    return value;
}

static inline WitU64 wit_x64_read_cr4(void)
{
    WitU64 value;
    __asm__ volatile("mov %%cr4, %0" : "=r"(value));
    return value;
}

static inline void wit_x64_write_cr0(WitU64 value)
{
    __asm__ volatile("mov %0, %%cr0" : : "r"(value) : "memory");
}

static inline void wit_x64_write_cr3(WitU64 value)
{
    __asm__ volatile("mov %0, %%cr3" : : "r"(value) : "memory");
}

static inline void wit_x64_write_cr4(WitU64 value)
{
    __asm__ volatile("mov %0, %%cr4" : : "r"(value) : "memory");
}

/* Debug register 0-3, 6 or 7; any other number reads zero and writes nothing. */
static inline WitU64 wit_x64_read_dr(WitU32 number)
{
    WitU64 value = 0;
    switch (number) {
    case 0:
        __asm__ volatile("mov %%dr0, %0" : "=r"(value));
        break;
    case 1:
        __asm__ volatile("mov %%dr1, %0" : "=r"(value));
        break;
    case 2:
        __asm__ volatile("mov %%dr2, %0" : "=r"(value));
        break;
    case 3:
        __asm__ volatile("mov %%dr3, %0" : "=r"(value));
        break;
    case 6:
        __asm__ volatile("mov %%dr6, %0" : "=r"(value));
        break;
    case 7:
        __asm__ volatile("mov %%dr7, %0" : "=r"(value));
        break;
    default:
        break;
    }
    return value;
}

static inline void wit_x64_write_dr(WitU32 number, WitU64 value)
{
    switch (number) {
    case 0:
        __asm__ volatile("mov %0, %%dr0" : : "r"(value) : "memory");
        break;
    case 1:
        __asm__ volatile("mov %0, %%dr1" : : "r"(value) : "memory");
        break;
    case 2:
        __asm__ volatile("mov %0, %%dr2" : : "r"(value) : "memory");
        break;
    case 3:
        __asm__ volatile("mov %0, %%dr3" : : "r"(value) : "memory");
        break;
    case 6:
        __asm__ volatile("mov %0, %%dr6" : : "r"(value) : "memory");
        break;
    case 7:
        __asm__ volatile("mov %0, %%dr7" : : "r"(value) : "memory");
        break;
    default:
        break;
    }
}

static inline WitU64 wit_x64_read_msr(WitU32 msr)
{
    WitU32 low, high;
    __asm__ volatile("rdmsr" : "=a"(low), "=d"(high) : "c"(msr));
    return ((WitU64)high << 32) | low;
}

static inline void wit_x64_write_msr(WitU32 msr, WitU64 value)
{
    __asm__ volatile("wrmsr" : : "c"(msr), "a"((WitU32)value), "d"((WitU32)(value >> 32)) : "memory");
}

static inline void wit_x64_invlpg(const void *address)
{
    __asm__ volatile("invlpg (%0)" : : "r"(address) : "memory");
}

/* CPUID of a leaf and a subleaf: EAX, EBX, ECX and EDX in that order, as MSVC's __cpuidex gave them. */
static inline void wit_x64_cpuidex(int registers[4], int leaf, int subleaf)
{
    __asm__ volatile("cpuid"
        : "=a"(registers[0]), "=b"(registers[1]), "=c"(registers[2]), "=d"(registers[3])
        : "a"(leaf), "c"(subleaf));
}

static inline void wit_x64_cpuid(int registers[4], int leaf)
{
    wit_x64_cpuidex(registers, leaf, 0);
}

static inline WitU8 wit_x64_in8(WitU16 port)
{
    WitU8 value;
    __asm__ volatile("inb %1, %0" : "=a"(value) : "Nd"(port) : "memory");
    return value;
}

static inline WitU32 wit_x64_in32(WitU16 port)
{
    WitU32 value;
    __asm__ volatile("inl %1, %0" : "=a"(value) : "Nd"(port) : "memory");
    return value;
}

static inline void wit_x64_out8(WitU16 port, WitU8 value)
{
    __asm__ volatile("outb %0, %1" : : "a"(value), "Nd"(port) : "memory");
}

static inline void wit_x64_out32(WitU16 port, WitU32 value)
{
    __asm__ volatile("outl %0, %1" : : "a"(value), "Nd"(port) : "memory");
}

#endif
