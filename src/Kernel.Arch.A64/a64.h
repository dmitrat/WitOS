#ifndef WITOS_A64_H
#define WITOS_A64_H

#include "witos/types.h"

/* ARM64 state and instructions private to this directory; the assembly is AAPCS64. */

/* Registers that an exception vector saves on the interrupted stack; vectors.asm owns the layout. */
typedef struct WitA64Frame {
    WitU64 X[31];
    WitU64 Sp;
    WitU64 Elr;
    WitU64 Spsr;
    WitU64 Esr;
    WitU64 Far;
} WitA64Frame;

/* Kernel stack the boot processor runs on after leaving the firmware stack. */
#define WIT_A64_BOOT_STACK_SIZE (64U * 1024U)
extern WitU8 wit_a64_boot_stack[WIT_A64_BOOT_STACK_SIZE];

/* Exception vector table of vectors.asm, 2 KiB aligned. */
extern const WitU8 wit_a64_vectors[];

/* Masks interrupts and calls function(argument) on a fresh stack; function never returns. */
WIT_NORETURN void wit_a64_call_on_stack(const void *argument, void *stack_top, void (*function)(const void *));

/* Masks debug, SError, IRQ and FIQ exceptions (DAIF). */
void wit_a64_mask_interrupts(void);

/* Waits for the next interrupt or event. */
void wit_a64_wait(void);

/* Current exception level, 0 to 3. */
WitU64 wit_a64_exception_level(void);

/* Current stack pointer. */
WitU64 wit_a64_stack_pointer(void);

/* Installs and reads back the exception vector base (VBAR_EL1). */
void wit_a64_set_vectors(const void *table);
WitU64 wit_a64_vectors_base(void);

/* Reports a fatal exception taken from vector entry kind (0 to 15). */
WIT_NORETURN void wit_a64_exception(const WitA64Frame *frame, WitU64 kind);

#endif
