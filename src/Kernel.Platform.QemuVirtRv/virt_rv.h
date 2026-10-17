#ifndef WITOS_VIRT_RV_H
#define WITOS_VIRT_RV_H

#include "witos/types.h"

/* QEMU virt board for riscv64 (plan step V1.1): the device addresses its device tree names, used only by this
 * directory. */

#define WIT_VIRT_RV_UART_BASE 0x10000000ULL /* ns16550a, byte registers, no register shift */
#define WIT_VIRT_RV_TEST_BASE 0x00100000ULL /* sifive,test: the test finisher that ends the machine with a code */

#define WIT_VIRT_RV_TEST_FAIL 0x3333U /* the finisher's "fail" form carries the exit code in its upper half */

#endif
