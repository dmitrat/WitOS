#ifndef WITOS_A64_H
#define WITOS_A64_H

#include "witos/types.h"
#include "witos/user_layout.h"

/* ARM64 state and instructions private to this directory; the assembly is AAPCS64. */

/* State that an exception vector saves on the interrupted stack and restores from the frame it resumes;
 * vectors.asm owns the layout. It is the WitArchFrame of witos/arch.h. Sp is SP_EL0 in a frame from EL0 and the
 * interrupted kernel stack pointer, the frame address plus its size, in a frame from EL1. TPIDR_EL0 is writable
 * at EL0 and therefore part of each thread's state. Q holds the 32 SIMD registers as low and high halves. */
typedef struct WitArchFrame {
    WitU64 X[31];
    WitU64 Sp;
    WitU64 Elr;
    WitU64 Spsr;
    WitU64 Esr;
    WitU64 Far;
    WitU64 Fpcr;
    WitU64 Fpsr;
    WitU64 Tpidr;
    WitU64 Reserved;
    WitU64 Q[64];
} WitA64Frame;

#define WIT_A64_FRAME_SIZE 832U

/* SPSR fields: M[4:0] is zero for EL0t in AArch64 and 5 for EL1h; NZCV are the condition flags. */
#define WIT_A64_SPSR_MODE 0x1FULL
#define WIT_A64_SPSR_EL1H 0x5ULL
#define WIT_A64_SPSR_FLAGS 0xF0000000ULL
#define WIT_A64_SPSR_ILLEGAL (1ULL << 20)

/* Kernel stacks: [guard 4 KiB | stack | guard 4 KiB]. The guards are unmapped once the kernel installs its own
 * translation tables. */
#define WIT_A64_KERNEL_STACK_SIZE (64U * 1024U)
#define WIT_A64_STACK_REGION_SIZE (WIT_A64_KERNEL_STACK_SIZE + 8192U)
#define WIT_A64_STACK_GUARD_COUNT (6U + 2U * WIT_PROCESS_CAPACITY * WIT_PROCESS_THREAD_CAPACITY)
extern WitU8 wit_a64_kernel_stack[WIT_A64_STACK_REGION_SIZE];

/* Stacks of the two kernel workers of the preemption self-test. */
extern WitU8 wit_a64_worker_stacks[2][WIT_A64_STACK_REGION_SIZE];

/* Kernel stack of each user thread of every registry slot (K5.2c); a thread's frame sits at the top of its stack
 * whenever it runs at EL0, so that SP_EL1 receives its next exception there. */
extern WitU8 wit_a64_user_kernel_stacks[WIT_PROCESS_CAPACITY][WIT_PROCESS_THREAD_CAPACITY][WIT_A64_STACK_REGION_SIZE];
void wit_a64_stack_guards(WitU64 guards[WIT_A64_STACK_GUARD_COUNT]);

/* Boot storage window in the TTBR1 half, at the same address as the x64 storage slot. */
#define WIT_A64_STORAGE_BASE 0xFFFFA00000000000ULL
#define WIT_A64_ROOT_BASE (WIT_A64_STORAGE_BASE + (128ULL << 20)) /* the root task image after the package window */

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

/* Resume address of wit_arch_idle_once: an idle interrupt's frame returns here and masks IRQ again. */
void wit_a64_idle_resume(void);

/* Current exception level, 0 to 3. */
WitU64 wit_a64_exception_level(void);

/* Current stack pointer. */
WitU64 wit_a64_stack_pointer(void);

/* Installs and reads back the exception vector base (VBAR_EL1). */
void wit_a64_set_vectors(const void *table);
WitU64 wit_a64_vectors_base(void);

/* Memory model feature register ID_AA64MMFR0_EL1. */
WitU64 wit_a64_memory_features(void);
/* MPIDR_EL1, ID_AA64ISAR0_EL1 and ID_AA64PFR0_EL1 of the running processor (processor_registers.asm, K7.1). */
WitU64 wit_a64_processor_id(void);
WitU64 wit_a64_isa_features(void);
WitU64 wit_a64_processor_features(void);
/* Secondary processors (secondary.asm, K7.2): PSCI CPU_ON through the virt profile's HVC conduit, the entry a started
 * processor begins at with the MMU off, the boot processor's translation registers it copies, and its C main. */
WitU64 wit_a64_psci_cpu_on(WitU64 target, WitU64 entry, WitU64 context);
extern const WitU8 wit_a64_secondary_entry[];
WitU64 wit_a64_translation_control(void);
WitU64 wit_a64_memory_attributes(void);
WitU64 wit_a64_translation_base_high(void);
WIT_NORETURN void wit_a64_secondary_main(WitU32 index);

/* System control (SCTLR_EL1) and FP/SIMD access control (CPACR_EL1). */
WitU64 wit_a64_system_control(void);
void wit_a64_set_system_control(WitU64 value);
WitU64 wit_a64_fp_access(void);
void wit_a64_set_fp_access(WitU64 value);

/* Installs MAIR_EL1, TCR_EL1 and both translation table bases, then invalidates the TLB. The caller must run
 * identity-mapped in the old and the new tables. */
void wit_a64_install_tables(WitU64 ttbr0, WitU64 ttbr1, WitU64 tcr, WitU64 mair);

/* Current TTBR0_EL1. */
WitU64 wit_a64_translation_base(void);

/* Another TTBR0_EL1 within a run, with every translation of the previous one invalidated; the kernel is identity
 * mapped in both (K5.2c). */
void wit_a64_switch_translation(WitU64 ttbr0);

/* Invalidates the translation of one page for every address space after a descriptor changed. */
void wit_a64_invalidate_page(WitU64 virtual_address);

/* Floating-point control (FPCR) of the running code and the monitor debug control MDSCR_EL1. */
WitU64 wit_a64_float_control(void);
void wit_a64_set_float_control(WitU64 value);
WitU64 wit_a64_debug_control(void);

/* Probes the FPCR bits the hardware implements and checks that hardware debug is off (context.c); the mask is zero
 * until then, and thread contexts are unsupported without it. */
void wit_a64_context_initialize(void);
WitU64 wit_a64_float_control_mask(void);

/* Thread pointer that EL0 reads but cannot write (TPIDRRO_EL0): the raw TLS base of the running user thread. */
void wit_a64_set_thread_pointer(WitU64 value);
WitU64 wit_a64_thread_pointer(void);

/* Saves the host state, installs the address space root in TTBR0 and resumes the EL0 frame. It returns when
 * wit_a64_leave_user restores the host state and the kernel root. */
void wit_a64_run_user(WitA64Frame *frame, WitU64 root);
WIT_NORETURN void wit_a64_leave_user(void);

/* Cleans the data cache lines of [address, address + size) to the point of unification. */
void wit_a64_clean_data(WitU64 address, WitU64 size);

/* Invalidates the instruction cache after cleaned code and synchronizes instruction fetch. */
void wit_a64_invalidate_instructions(void);

/* Full data synchronization barrier for the inner shareable domain. */
void wit_a64_data_barrier(void);

/* Handles the exception of vector entry kind (0 to 15) and returns the frame to resume, which may belong to
 * another thread. Kernel synchronous exceptions are fatal. */
WitA64Frame *wit_a64_exception(WitA64Frame *frame, WitU64 kind);

/* Interrupt taken at EL1 or EL0: counts timer ticks, runs the user scheduler or switches the self-test kernel
 * threads. */
WitA64Frame *wit_a64_interrupt(WitA64Frame *frame);

/* Synchronous exception from EL0: a system call or a user fault for the common kernel. */
WitA64Frame *wit_a64_user_trap(WitA64Frame *frame);

/* Final check before a frame is resumed: an EL0 frame must sit at the top of the selected kernel stack. */
WitA64Frame *wit_a64_prepare_resume(WitA64Frame *frame);
/* Restores a prepared frame on its own kernel stack and returns to its mode (vectors.asm); never returns (K5.2c). */
WIT_NORETURN void wit_a64_resume_frame(WitA64Frame *frame);

#endif
