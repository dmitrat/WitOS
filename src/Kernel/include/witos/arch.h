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
 * the functions that the kernel layers it links call: a new architecture starts with identity and bring-up
 * under the boot-only kernel profile. The common kernel never names registers, selectors or ISA instructions.
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

struct WitBootInfo;

/* What the boot contract, the image check and the banner say about this architecture. */
typedef struct WitArchIdentity {
    WitU32 BootArchitecture; /* WIT_ARCH_* in WitBootInfo. */
    WitU16 PeMachine; /* Machine field of the kernel's PE image and of the user images it loads. */
    WitU16 Capabilities; /* WIT_ARCH_* capability flags below. */
    const char *Name; /* Short name, such as x64. */
    const char *Processor; /* Processor family, such as x86_64. */
} WitArchIdentity;

/* The kernel validates the unwind metadata (exception directory) of this machine's images. */
#define WIT_ARCH_PE_UNWIND 1U

/* Identity, bring-up, interrupt flag and halting. */
const WitArchIdentity *wit_arch_identity(void);
WIT_NORETURN void wit_arch_enter(const struct WitBootInfo *boot);
void wit_arch_initialize(void);
void wit_arch_disable_interrupts(void);
WIT_NORETURN void wit_arch_halt(void);
void wit_arch_map_device_page(const struct WitBootInfo *boot, WitU64 physical);

/* Switching between kernel and user mode. */
void wit_arch_run_user(WitArchFrame *frame, WitU64 root);
WIT_NORETURN void wit_arch_leave_user(void);
/* Within a run: the address space of another process (or the kernel's root) becomes current, with the translations
 * of the previous one gone; and a dispatched frame on its own kernel stack resumes without returning (K5.2c). */
void wit_arch_space_switch(WitU64 root);
WIT_NORETURN void wit_arch_resume_frame(WitArchFrame *frame);
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
/* The same frame with the stack pointer exactly as given (the one thread form, K5.2a). */
WitArchFrame *wit_arch_frame_create_at(WitU32 slot, WitU32 thread, WitU64 entry, WitU64 argument, WitU64 stack_pointer);
/* Whether THREAD_SET_TLS can set the raw TLS base of a thread from the kernel (x64 FS); ARM64's TPIDR_EL0 is EL0's. */
int wit_arch_user_tls_settable(void);
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
/* The fault state of a delivered record the handler rejected: its context and, where the ISA reports one, its
 * syndrome. */
void wit_arch_fault_from_record(WitArchFaultState *state, const WitUserExceptionInfo *record);
int wit_arch_fault_from_user(const WitArchFaultState *state);
void wit_arch_fault_describe(const WitArchFaultState *state);

/*
 * User address spaces. The common kernel owns and accounts every page, including table pages, which the
 * architecture takes and returns through wit_user_space_take_table and wit_user_space_release_table.
 * A leaf entry is zero when nothing is mapped; otherwise the architecture encodes these flags.
 */
#define WIT_PAGE_READ 1U /* Accessible from user mode. */
#define WIT_PAGE_WRITE 2U
#define WIT_PAGE_EXECUTE 4U
#define WIT_PAGE_OWNED 8U /* Committed backing owned by the space, even with no access. */
#define WIT_PAGE_ALIAS 16U /* Code view of backing owned elsewhere in the same space. */
#define WIT_PAGE_DEVICE 32U /* Uncached: an alias of a device region (K3.1). */

struct WitUserSpace;
WitU64 wit_arch_page_entry_make(WitU64 physical, WitU32 flags);
WitU32 wit_arch_page_entry_flags(WitU64 entry);
WitU64 wit_arch_page_entry_physical(WitU64 entry);
WitU64 *wit_arch_page_entry(struct WitUserSpace *space, WitU64 address, int create);
void wit_arch_page_prune(struct WitUserSpace *space, WitU64 address);
void wit_arch_page_invalidate(const struct WitUserSpace *space, WitU64 address);
WitU64 wit_arch_page_translate(WitU64 root, WitU64 address, int write, int execute);
int wit_arch_space_kernel_ready(void);
void wit_arch_space_install_kernel(WitU64 root);
int wit_arch_space_active(WitU64 root);

/* Code publication: each page that received new instructions is cleaned toward instruction fetch, then
 * wit_arch_publish_code completes the batch before any of it may execute. */
void wit_arch_publish_code_page(WitU64 physical);
void wit_arch_publish_code(void);

/* Scheduler ticks counted by the timer interrupt entry; the board supplies the timer (witos/platform.h). */
WitU64 wit_arch_clock_ticks(void);

/* Processor services. */
WitU64 wit_arch_cache_size(void);
/* The running processor's hardware identity (the APIC id; the MPIDR affinity) and its ISA feature words (K7.1). */
WitU64 wit_arch_processor_id(void);
WitU64 wit_arch_processor_features(void);
/* Secondary processors (K7.2): the boot processor prepares one (its stack, its interrupt frames, the registers it
 * copies), starts it toward the architecture's entry with the kernel's number, and addresses it with an
 * inter-processor interrupt of a kind of witos/cpu.h; a processor invalidates one translation of its own. */
void wit_arch_secondary_prepare(const struct WitBootInfo *boot, WitU32 index, WitU64 hardware_id);
int wit_arch_secondary_start(WitU32 index, WitU64 hardware_id);
void wit_arch_ipi(WitU64 hardware_id, WitU32 kind);
void wit_arch_invalidate_local(WitU64 address);
void wit_arch_process_write_barrier(void);

#endif
