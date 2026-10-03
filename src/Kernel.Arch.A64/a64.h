#ifndef WITOS_A64_H
#define WITOS_A64_H

#include "witos/types.h"

/* ARM64 state and instructions private to this directory; the assembly is AAPCS64. */

/* State that an exception vector saves on the interrupted stack and restores from the frame it resumes;
 * vectors.asm owns the layout. Q holds the 32 SIMD registers as low and high halves. */
typedef struct WitA64Frame {
    WitU64 X[31];
    WitU64 Sp;
    WitU64 Elr;
    WitU64 Spsr;
    WitU64 Esr;
    WitU64 Far;
    WitU64 Fpcr;
    WitU64 Fpsr;
    WitU64 Q[64];
} WitA64Frame;

#define WIT_A64_FRAME_SIZE 816U

/* Kernel stack of the boot processor: [guard 4 KiB | stack | guard 4 KiB]. The guards are unmapped once the
 * kernel installs its own translation tables. */
#define WIT_A64_KERNEL_STACK_SIZE (64U * 1024U)
#define WIT_A64_STACK_REGION_SIZE (WIT_A64_KERNEL_STACK_SIZE + 8192U)
#define WIT_A64_STACK_GUARD_COUNT 6U
extern WitU8 wit_a64_kernel_stack[WIT_A64_STACK_REGION_SIZE];

/* Stacks of the two kernel workers of the preemption self-test, laid out like the kernel stack. */
extern WitU8 wit_a64_worker_stacks[2][WIT_A64_STACK_REGION_SIZE];
void wit_a64_stack_guards(WitU64 guards[WIT_A64_STACK_GUARD_COUNT]);

/* Boot storage window in the TTBR1 half, at the same address as the x64 storage slot. */
#define WIT_A64_STORAGE_BASE 0xFFFFA00000000000ULL

/* Exception vector table of vectors.asm, 2 KiB aligned. */
extern const WitU8 wit_a64_vectors[];

/* Masks interrupts and calls function(argument) on a fresh stack; function never returns. */
WIT_NORETURN void wit_a64_call_on_stack(const void *argument, void *stack_top, void (*function)(const void *));

/* Masks debug, SError, IRQ and FIQ exceptions (DAIF). */
void wit_a64_mask_interrupts(void);

/* Unmasks IRQ only. */
void wit_a64_enable_interrupts(void);

/* DAIF exception mask bits as the register reads. */
WitU64 wit_a64_interrupt_mask(void);

/* Waits for the next interrupt or event. */
void wit_a64_wait(void);

/* Current exception level, 0 to 3. */
WitU64 wit_a64_exception_level(void);

/* Current stack pointer. */
WitU64 wit_a64_stack_pointer(void);

/* Installs and reads back the exception vector base (VBAR_EL1). */
void wit_a64_set_vectors(const void *table);
WitU64 wit_a64_vectors_base(void);

/* Memory model feature register ID_AA64MMFR0_EL1. */
WitU64 wit_a64_memory_features(void);

/* Installs MAIR_EL1, TCR_EL1 and both translation table bases, then invalidates the TLB. The caller must run
 * identity-mapped in the old and the new tables. */
void wit_a64_install_tables(WitU64 ttbr0, WitU64 ttbr1, WitU64 tcr, WitU64 mair);

/* Current TTBR0_EL1. */
WitU64 wit_a64_translation_base(void);

/* Invalidates the translation of one page for every address space after a descriptor changed. */
void wit_a64_invalidate_page(WitU64 virtual_address);

/* Handles the exception of vector entry kind (0 to 15) and returns the frame to resume, which may belong to
 * another kernel thread. Every exception other than an interrupt is fatal. */
WitA64Frame *wit_a64_exception(WitA64Frame *frame, WitU64 kind);

/* Interrupt taken at EL1: counts timer ticks and switches the self-test kernel threads. */
WitA64Frame *wit_a64_interrupt(WitA64Frame *frame);

#endif
