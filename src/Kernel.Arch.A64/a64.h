#ifndef WITOS_A64_H
#define WITOS_A64_H

#include "witos/types.h"

/* ARM64 instructions the C code cannot express; entry.asm, AAPCS64. */

/* Masks interrupts and calls function(argument) on a fresh stack; function never returns. */
WIT_NORETURN void wit_a64_call_on_stack(const void *argument, void *stack_top, void (*function)(const void *));

/* Masks debug, SError, IRQ and FIQ exceptions (DAIF). */
void wit_a64_mask_interrupts(void);

/* Waits for the next interrupt or event. */
void wit_a64_wait(void);

/* Current exception level, 0 to 3. */
WitU64 wit_a64_exception_level(void);

#endif
