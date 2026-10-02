#ifndef WITOS_ARCH_H
#define WITOS_ARCH_H

#include "types.h"

/*
 * Contract between the common kernel and one processor architecture.
 *
 * The common kernel decides which thread runs, what a system call means and how a fault is handled.
 * The architecture switches to and from user mode, owns the saved register frame of every thread,
 * keeps the processor state profile, and reports traps. Each src/Kernel.Arch.<isa> directory implements
 * every function declared here; the common kernel never names registers, selectors or ISA instructions.
 */

/* Saved user register frame of one thread; its layout belongs to the architecture. */
typedef struct WitArchFrame WitArchFrame;

/* Switching between kernel and user mode. */
void wit_arch_run_user(WitArchFrame *frame, WitU64 root);
WIT_NORETURN void wit_arch_leave_user(void);
void wit_arch_set_kernel_stack(WitU64 top);
void wit_arch_set_user_tls(WitU64 address, WitU64 compiler_address);
void wit_arch_idle_once(void);

/* Time: scheduler ticks and the monotonic counter. */
void wit_arch_timer_start(void);
void wit_arch_timer_stop(void);
WitU64 wit_arch_clock_ticks(void);
WitU64 wit_arch_monotonic_read(void);
WitU64 wit_arch_monotonic_frequency(void);

/* Processor services. */
int wit_arch_context_supported(void);
WitU64 wit_arch_cache_size(void);
void wit_arch_process_write_barrier(void);

#endif
