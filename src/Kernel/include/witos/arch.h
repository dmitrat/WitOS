#ifndef WITOS_ARCH_H
#define WITOS_ARCH_H

#include "cpu_context_info.h"
#include "exception.h"
#include "thread_context.h"
#include "types.h"

/*
 * Contract between the common kernel and one processor architecture.
 *
 * The common kernel decides which thread runs, what a system call means and how a fault is handled.
 * The architecture switches to and from user mode, owns the saved register frame of every thread,
 * keeps the processor state profile, and reports traps. Each src/Kernel.Arch.<isa> directory implements
 * every function declared here; the common kernel never names registers, selectors or ISA instructions.
 *
 * A thread is identified by its component slot and thread index. Its frame lives on that thread's
 * kernel stack while the thread is outside user mode; frame pointers stay valid until the thread runs.
 */

/* Saved user register frame of one thread; its layout belongs to the architecture. */
typedef struct WitArchFrame WitArchFrame;

/* Architecture details of one user fault (src/Kernel.Arch.<isa>/include/witos/arch_types.h). */
typedef struct WitArchFaultState WitArchFaultState;

/* Hardware fault classes that the common kernel counts. */
typedef enum WitArchExceptionKind {
    WitArchExceptionOther,
    WitArchExceptionNullRead,
    WitArchExceptionNullWrite,
    WitArchExceptionDivide,
    WitArchExceptionIllegal
} WitArchExceptionKind;

/* Switching between kernel and user mode. */
void wit_arch_run_user(WitArchFrame *frame, WitU64 root);
WIT_NORETURN void wit_arch_leave_user(void);
void wit_arch_select_thread_stack(WitU32 slot, WitU32 thread);
void wit_arch_select_boot_stack(void);
void wit_arch_set_user_tls(WitU64 address, WitU64 compiler_address);
void wit_arch_reset_user_tls(void);
int wit_arch_user_tls_is_reset(void);
int wit_arch_kernel_space_active(void);
int wit_arch_interrupts_enabled(void);
void wit_arch_idle_once(void);

/* Thread frames. */
WitArchFrame *wit_arch_frame_create(WitU32 slot, WitU32 thread, WitU64 entry, WitU64 argument, WitU64 stack_top);
int wit_arch_kernel_stack_contains(WitU32 slot, WitU32 thread, const void *object, WitU64 size);
int wit_arch_frame_owned(const WitArchFrame *frame, WitU32 slot, WitU32 thread);
int wit_arch_frame_from_user(const WitArchFrame *frame);
int wit_arch_frame_returns_to_user(const WitArchFrame *frame);
int wit_arch_frame_is_idle(const WitArchFrame *frame, WitU32 slot, WitU32 thread);
WitU64 wit_arch_frame_pc(const WitArchFrame *frame);
WitU64 wit_arch_frame_sp(const WitArchFrame *frame);
void wit_arch_frame_prepare_return(WitArchFrame *frame, int syscall);
WitU64 *wit_arch_frame_status(WitArchFrame *frame);
WitU64 *wit_arch_frame_value(WitArchFrame *frame);
void wit_arch_frame_set_result(WitArchFrame *frame, WitU64 status, WitU64 value);
WitU64 wit_arch_callback_stack(WitU64 sp, WitU32 *call_frame_bytes);
void wit_arch_frame_enter_callback(
    WitArchFrame *frame, WitU64 entry, WitU64 stack, WitU64 argument0, WitU64 argument1, WitU64 argument2);
void wit_arch_frame_describe(const WitArchFrame *frame);

/* Thread contexts; WitThreadContext is the per-ISA ABI layout. */
int wit_arch_context_supported(void);
WitU32 wit_arch_context_profile(void);
void wit_arch_context_describe(WitThreadContext *context);
void wit_arch_context_capture(WitThreadContext *context, const WitArchFrame *frame);
int wit_arch_context_registers_valid(const WitThreadContext *context);
int wit_arch_context_state_valid(const WitThreadContext *context);
WitU64 wit_arch_context_pc(const WitThreadContext *context);
WitU64 wit_arch_context_sp(const WitThreadContext *context);
void wit_arch_context_apply(WitArchFrame *frame, const WitThreadContext *context);
void wit_arch_cpu_context_describe(WitCpuContextInfo *info, const WitArchFrame *frame);

/* Exceptions and faults. */
int wit_arch_exception_deliverable(WitU64 vector);
WitArchExceptionKind wit_arch_exception_kind(WitU64 vector, WitU64 error, WitU64 address);
void wit_arch_exception_record(WitUserExceptionInfo *info, const WitArchFrame *frame, WitU64 address);
void wit_arch_exception_record_software(WitUserExceptionInfo *info, const WitThreadContext *context);
void wit_arch_fault_from_frame(WitArchFaultState *state, const WitArchFrame *frame);
void wit_arch_fault_from_context(WitArchFaultState *state, const WitThreadContext *context);
int wit_arch_fault_from_user(const WitArchFaultState *state);
void wit_arch_fault_describe(const WitArchFaultState *state);

/* Time: scheduler ticks and the monotonic counter. */
void wit_arch_timer_start(void);
void wit_arch_timer_stop(void);
WitU64 wit_arch_clock_ticks(void);
WitU64 wit_arch_monotonic_read(void);
WitU64 wit_arch_monotonic_frequency(void);

/* Processor services. */
WitU64 wit_arch_cache_size(void);
void wit_arch_process_write_barrier(void);

#endif
