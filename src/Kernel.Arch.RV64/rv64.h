#ifndef WITOS_RV64_H
#define WITOS_RV64_H

#include "witos/rv64_csr.h"
#include "witos/rv64_sbi.h"
#include "witos/types.h"
#include "witos/user_layout.h"

/* RISC-V state and instructions private to this directory (plan step V1.2); the assembly is the psABI (lp64d). */

/* The frame the trap entry saves on the interrupted kernel stack and restores from the frame it resumes; entry.S
 * owns the layout. It is the WitArchFrame of witos/arch.h. X[0] stays zero, X[2] is the interrupted stack pointer
 * (the user stack of a frame from U-mode, the frame's address plus its size of a frame from S-mode), X[4] the thread
 * pointer tp, which a thread writes itself. F holds the 32 floating-point registers; the kernel keeps sstatus.FS off
 * Off, so that they are always accessible. */
typedef struct WitArchFrame {
    WitU64 X[32];
    WitU64 Sepc;
    WitU64 Sstatus;
    WitU64 Scause;
    WitU64 Stval;
    WitU64 F[32];
    WitU64 Fcsr;
    WitU64 Reserved;
} WitRv64Frame;

#define WIT_RV64_FRAME_SIZE 560U

/* sstatus fields. */
#define WIT_RV64_SSTATUS_SIE (1ULL << 1)
#define WIT_RV64_SSTATUS_SPIE (1ULL << 5)
#define WIT_RV64_SSTATUS_SPP (1ULL << 8) /* the mode the trap came from: 1 S-mode, 0 U-mode */
#define WIT_RV64_SSTATUS_FS (3ULL << 13)
#define WIT_RV64_SSTATUS_FS_INITIAL (1ULL << 13)
#define WIT_RV64_SSTATUS_SUM (1ULL << 18)
#define WIT_RV64_SSTATUS_MXR (1ULL << 19)
#define WIT_RV64_SSTATUS_UXL (3ULL << 32) /* the width of U-mode, which the kernel keeps as the hart reports it */

/* A frame's Reserved word (V1.3): FRESH marks a frame no return has resumed yet, whose thread pointer the kernel
 * sets from the thread's TLS base at its first resume; afterwards tp is the thread's own, as TPIDR_EL0 is a thread's
 * on ARM64. */
#define WIT_RV64_FRAME_FRESH 1ULL

/* scause: the interrupt bit and the codes the kernel names. */
#define WIT_RV64_CAUSE_INTERRUPT (1ULL << 63)
#define WIT_RV64_CAUSE_CODE 0xFFULL
#define WIT_RV64_INTERRUPT_SOFTWARE 1U
#define WIT_RV64_INTERRUPT_TIMER 5U
#define WIT_RV64_INTERRUPT_EXTERNAL 9U
#define WIT_RV64_EXCEPTION_INSTRUCTION_MISALIGNED 0U
#define WIT_RV64_EXCEPTION_INSTRUCTION_ACCESS 1U
#define WIT_RV64_EXCEPTION_ILLEGAL 2U
#define WIT_RV64_EXCEPTION_BREAKPOINT 3U
#define WIT_RV64_EXCEPTION_LOAD_MISALIGNED 4U
#define WIT_RV64_EXCEPTION_LOAD_ACCESS 5U
#define WIT_RV64_EXCEPTION_STORE_MISALIGNED 6U
#define WIT_RV64_EXCEPTION_STORE_ACCESS 7U
#define WIT_RV64_EXCEPTION_ECALL_USER 8U
#define WIT_RV64_EXCEPTION_INSTRUCTION_PAGE 12U
#define WIT_RV64_EXCEPTION_LOAD_PAGE 13U
#define WIT_RV64_EXCEPTION_STORE_PAGE 15U

/* Kernel stacks: [guard 4 KiB | stack | guard 4 KiB]. The guards are unmapped once the kernel installs its own
 * translation tables. */
#define WIT_RV64_KERNEL_STACK_SIZE (64U * 1024U)
#define WIT_RV64_STACK_REGION_SIZE (WIT_RV64_KERNEL_STACK_SIZE + 8192U)
#define WIT_RV64_STACK_GUARD_COUNT (6U + 2U * WIT_PROCESS_CAPACITY * WIT_PROCESS_THREAD_CAPACITY)
extern WitU8 wit_rv64_kernel_stack[WIT_RV64_STACK_REGION_SIZE];

/* Stacks of the two kernel workers of the preemption self-test. */
extern WitU8 wit_rv64_worker_stacks[2][WIT_RV64_STACK_REGION_SIZE];

/* Kernel stack of each user thread of every registry slot (V1.3, as A64's); a thread's frame sits at the top of its
 * stack whenever it runs in U-mode, so that the next trap finds it there through sscratch. */
extern WitU8 wit_rv64_user_kernel_stacks[WIT_PROCESS_CAPACITY][WIT_PROCESS_THREAD_CAPACITY][WIT_RV64_STACK_REGION_SIZE];
void wit_rv64_stack_guards(WitU64 guards[WIT_RV64_STACK_GUARD_COUNT]);

/* Boot storage window in the high half of Sv48, at the same address as the other ISAs' storage slot. */
#define WIT_RV64_STORAGE_BASE 0xFFFFA00000000000ULL
#define WIT_RV64_ROOT_BASE (WIT_RV64_STORAGE_BASE + (128ULL << 20)) /* the root task image after the package window */

/* The trap vector of entry.S (stvec, direct mode, 4-byte aligned). */
extern const WitU8 wit_rv64_trap_vector[];

/* Disables interrupts and calls function(argument) on a fresh stack; function never returns. */
WIT_NORETURN void wit_rv64_call_on_stack(const void *argument, void *stack_top, void (*function)(const void *));

/* Current stack pointer. */
WitU64 wit_rv64_stack_pointer(void);

/* Resume address of wit_arch_idle_once: an idle interrupt's frame returns here and disables interrupts again. */
void wit_rv64_idle_resume(void);

/* Restores a frame on its own kernel stack and returns to its mode (entry.S); never returns. */
WIT_NORETURN void wit_rv64_resume_frame(WitRv64Frame *frame);

/* Saves the host's callee-saved state, installs the address space root in satp and resumes the U-mode frame (entry.S,
 * V1.3). It returns when wit_rv64_leave_user restores the host state and the kernel root. */
void wit_rv64_run_user(WitRv64Frame *frame, WitU64 satp);
WIT_NORETURN void wit_rv64_leave_user(void);

/* Final check before a frame is resumed (frame.c): a U-mode frame must sit at the top of the selected kernel stack, and
 * a fresh one takes the thread's TLS base as its thread pointer. */
WitRv64Frame *wit_rv64_prepare_resume(WitRv64Frame *frame);

/* A synchronous trap from U-mode (frame.c): an environment call is a system call, anything else a user fault for the
 * common kernel. */
WitRv64Frame *wit_rv64_user_trap(WitRv64Frame *frame);

/* Handles a trap and returns the frame to resume, which may belong to another thread (exceptions.c). Kernel
 * synchronous exceptions are fatal. */
WitRv64Frame *wit_rv64_trap(WitRv64Frame *frame);

/* An interrupt taken in S-mode or U-mode (scheduler.c): counts timer ticks, runs the user scheduler or switches the
 * self-test kernel threads, or delivers a device line. */
WitRv64Frame *wit_rv64_interrupt(WitRv64Frame *frame);

#endif
