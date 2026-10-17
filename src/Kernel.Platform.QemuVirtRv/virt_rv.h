#ifndef WITOS_VIRT_RV_H
#define WITOS_VIRT_RV_H

#include "witos/limits.h"
#include "witos/types.h"

/* QEMU virt board for riscv64 (plan steps V1.1, V1.2): the device addresses its device tree names, used only by this
 * directory. */

#define WIT_VIRT_RV_UART_BASE 0x10000000ULL /* ns16550a, byte registers, no register shift */
#define WIT_VIRT_RV_TEST_BASE 0x00100000ULL /* sifive,test: the test finisher that ends the machine with a code */
#define WIT_VIRT_RV_RTC_BASE 0x00101000ULL /* google,goldfish-rtc: nanoseconds since 1970 in two words (K6's clock) */
#define WIT_VIRT_RV_PLIC_BASE 0x0C000000ULL /* riscv,plic0: the interrupt controller, two contexts a hart */

#define WIT_VIRT_RV_TEST_FAIL 0x3333U /* the finisher's "fail" form carries the exit code in its upper half */

struct WitBootInfo;

/* What the device tree says of the board (fdt.c), read once after paging is active: the harts of /cpus with their
 * identities, their status and whether their ISA names sstc, and the timebase frequency the time counter runs at. */
typedef struct WitVirtRvHart {
    WitU64 Id;
    WitU32 Enabled, Sstc;
} WitVirtRvHart;

typedef struct WitVirtRvTree {
    WitVirtRvHart Harts[WIT_PROCESSOR_CAPACITY];
    WitU32 HartCount, TimebaseFrequency;
    int Read;
} WitVirtRvTree;

const WitVirtRvTree *wit_virt_rv_tree(const struct WitBootInfo *boot);

/* Maps the PLIC pages of the boot hart's supervisor context; the clock does it once paging is active. */
void wit_virt_rv_interrupts_map(const struct WitBootInfo *boot);

#endif
