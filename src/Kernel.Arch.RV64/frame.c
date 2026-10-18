#include "witos/arch.h"
#include "witos/arch_types.h"
#include "witos/platform.h"
#include "witos/virtual.h"
#include "user.h"
#include "rv64.h"

/* RISC-V implementation of the thread-frame part of witos/arch.h and the U-mode trap entry (plan step V1.3). A user
 * thread runs in U-mode with its frame at the top of its own kernel stack. System calls: ECALL with the number in a7
 * and the arguments in a0–a2; the status returns in a0 and the value in a1, and sepc moves past the ECALL, which the
 * hardware leaves pointing at it. The thread pointer tp (x4) is the thread's, as TPIDR_EL0 is on ARM64: the kernel
 * writes it once, from the request's TLS base, when a frame that has never run is first resumed, and THREAD_SET_TLS is
 * unsupported. A thread context is the RISC-V block of witos/thread_context.h: X1–X31, PC, the 32 floating-point
 * registers and FCSR; the fault callback receives it for the exception causes exceptions.c names and for activations. */

#define CAUSE_ECALL_USER WIT_RV64_EXCEPTION_ECALL_USER
#define INSTRUCTION_BYTES 4U /* ECALL is never compressed */
/* A thread or callback starts with a 16-byte aligned stack pointer and 16 bytes of zeros above it, as a frame record a
 * walk ends at; the psABI has no red zone. */
#define CALL_FRAME_BYTES 16U
/* fcsr: the accrued exception flags (4:0) and the rounding mode (7:5), whose values 5 and 6 are reserved and 7 is
 * only an instruction's "dynamic". */
#define FCSR_USER 0xFFULL
#define FCSR_ROUNDING_SHIFT 5
#define FCSR_ROUNDING_MAX 4ULL
#define SATP_PPN 0x00000FFFFFFFFFFFULL

/* The kernel stack top of the thread each processor runs and the TLS base its next fresh frame takes (P1.3a). */
static WitU64 selected_top[WIT_PROCESSOR_CAPACITY];
static WitU64 user_tls[WIT_PROCESSOR_CAPACITY];

static WitU64 stack_low(WitU32 slot, WitU32 thread)
{
    return (WitU64)wit_rv64_user_kernel_stacks[slot][thread] + 4096;
}

/* The sstatus of a frame that returns to U-mode: SPP clear, interrupts enabled on return, the floating-point unit on
 * and the width the hart reports. */
static WitU64 user_status(void)
{
    return (wit_rv64_read_sstatus() & WIT_RV64_SSTATUS_UXL) | WIT_RV64_SSTATUS_SPIE | WIT_RV64_SSTATUS_FS_INITIAL;
}

static WitU64 satp_of(WitU64 root)
{
    return WIT_RV64_SATP_SV48 | (root >> 12);
}

void wit_arch_select_thread_stack(WitU32 slot, WitU32 thread)
{
    selected_top[wit_arch_processor_index()] = stack_low(slot, thread) + WIT_RV64_KERNEL_STACK_SIZE;
}

void wit_arch_select_boot_stack(void)
{
    selected_top[wit_arch_processor_index()] = (WitU64)wit_rv64_kernel_stack + 4096 + WIT_RV64_KERNEL_STACK_SIZE;
}

void wit_arch_set_user_tls(WitU64 address)
{
    user_tls[wit_arch_processor_index()] = address;
}

void wit_arch_reset_user_tls(void)
{
    user_tls[wit_arch_processor_index()] = 0;
}

int wit_arch_user_tls_is_reset(void)
{
    return user_tls[wit_arch_processor_index()] == 0;
}

int wit_arch_kernel_space_active(void)
{
    return (wit_rv64_read_satp() & SATP_PPN) == (wit_virtual_kernel_root() >> 12);
}

void wit_arch_run_user(WitArchFrame *frame, WitU64 root)
{
    if (wit_arch_interrupts_enabled() || !wit_arch_frame_returns_to_user(frame)) {
        wit_panic("User launch needs interrupts disabled and a U-mode frame");
    }
    wit_rv64_run_user(wit_rv64_prepare_resume(frame), satp_of(root));
}

WIT_NORETURN void wit_arch_leave_user(void)
{
    wit_rv64_leave_user();
}

void wit_arch_space_switch(WitU64 root)
{
    if ((wit_rv64_read_satp() & SATP_PPN) != (root >> 12)) {
        wit_rv64_fence();
        wit_rv64_write_satp(satp_of(root));
        wit_rv64_fence_vma();
    }
}

/* A dispatched frame of another thread, on that thread's own kernel stack, resumes here without returning: the
 * current kernel stack is abandoned (K5.2c). */
WIT_NORETURN void wit_arch_resume_frame(WitArchFrame *frame)
{
    if (wit_arch_interrupts_enabled() || !wit_arch_frame_returns_to_user(frame)) {
        wit_panic("Frame resume needs interrupts disabled and a U-mode frame");
    }
    wit_rv64_resume_frame(wit_rv64_prepare_resume(frame));
}

WitRv64Frame *wit_rv64_prepare_resume(WitRv64Frame *frame)
{
    if ((frame->Sstatus & WIT_RV64_SSTATUS_SPP) == 0) {
        if ((WitU64)frame + WIT_RV64_FRAME_SIZE != selected_top[wit_arch_processor_index()]) {
            wit_panic("U-mode frame is not at the top of the selected kernel stack");
        }
        if (frame->Reserved == WIT_RV64_FRAME_FRESH) {
            frame->X[4] = user_tls[wit_arch_processor_index()];
            frame->Reserved = 0;
        }
    }
    return frame;
}

static WitArchFrame *build_frame(WitU32 slot, WitU32 thread, WitU64 entry, WitU64 argument, WitU64 stack_pointer)
{
    WitArchFrame *frame = (WitArchFrame *)(stack_low(slot, thread) + WIT_RV64_KERNEL_STACK_SIZE - WIT_RV64_FRAME_SIZE);
    for (WitU32 i = 0; i < sizeof(*frame); ++i) {
        ((WitU8 *)frame)[i] = 0;
    }
    frame->X[10] = argument;
    frame->Sepc = entry;
    frame->Sstatus = user_status();
    frame->X[2] = stack_pointer; /* a zero return address traps an accidental RET */
    frame->Reserved = WIT_RV64_FRAME_FRESH;
    return frame;
}

WitArchFrame *wit_arch_frame_create(WitU32 slot, WitU32 thread, WitU64 entry, WitU64 argument, WitU64 stack_top)
{
    return build_frame(slot, thread, entry, argument, (stack_top & ~15ULL) - CALL_FRAME_BYTES);
}

WitArchFrame *wit_arch_frame_create_at(WitU32 slot, WitU32 thread, WitU64 entry, WitU64 argument, WitU64 stack_pointer)
{
    return build_frame(slot, thread, entry, argument, stack_pointer);
}

/* tp is written by the thread and preserved in the frame; the kernel sets it once, at a fresh frame's first resume. */
int wit_arch_user_tls_settable(void)
{
    return 0;
}

int wit_arch_kernel_stack_contains(WitU32 slot, WitU32 thread, const void *object, WitU64 size)
{
    const WitU64 low = stack_low(slot, thread);
    return (WitU64)object >= low && (WitU64)object <= low + WIT_RV64_KERNEL_STACK_SIZE - size;
}

int wit_arch_frame_owned(const WitArchFrame *frame, WitU32 slot, WitU32 thread)
{
    return wit_arch_kernel_stack_contains(slot, thread, frame, sizeof(*frame)) && ((WitU64)frame & 15) == 0;
}

int wit_arch_frame_from_user(const WitArchFrame *frame)
{
    return (frame->Sstatus & WIT_RV64_SSTATUS_SPP) == 0;
}

int wit_arch_frame_returns_to_user(const WitArchFrame *frame)
{
    return (frame->Sstatus & WIT_RV64_SSTATUS_SPP) == 0;
}

int wit_arch_frame_is_idle(const WitArchFrame *frame, WitU64 low, WitU64 high)
{
    return (WitU64)frame >= low &&
        (WitU64)frame <= high - sizeof(*frame) &&
        ((WitU64)frame & 15) == 0 &&
        (frame->Sstatus & WIT_RV64_SSTATUS_SPP) != 0 &&
        frame->X[2] >= low &&
        frame->X[2] < high &&
        frame->Sepc == (WitU64)wit_rv64_idle_resume &&
        wit_arch_user_tls_is_reset();
}

WitU64 wit_arch_frame_pc(const WitArchFrame *frame)
{
    return frame->Sepc;
}

WitU64 wit_arch_frame_sp(const WitArchFrame *frame)
{
    return frame->X[2];
}

/* U-mode with interrupts enabled; RISC-V has no condition flags for a system call to clear. */
void wit_arch_frame_prepare_return(WitArchFrame *frame, int syscall)
{
    (void)syscall;
    frame->Sstatus = user_status();
}

WitU64 *wit_arch_frame_status(WitArchFrame *frame)
{
    return &frame->X[10];
}

WitU64 *wit_arch_frame_value(WitArchFrame *frame)
{
    return &frame->X[11];
}

void wit_arch_frame_set_result(WitArchFrame *frame, WitU64 status, WitU64 value)
{
    frame->X[10] = status;
    frame->X[11] = value;
}

WitU64 wit_arch_callback_stack(WitU64 sp, WitU32 *call_frame_bytes)
{
    *call_frame_bytes = CALL_FRAME_BYTES; /* The return address travels in ra; no shadow space. */
    return (sp & ~15ULL) - CALL_FRAME_BYTES;
}

void wit_arch_frame_enter_callback(
    WitArchFrame *frame, WitU64 entry, WitU64 stack, WitU64 argument0, WitU64 argument1, WitU64 argument2)
{
    frame->Sepc = entry;
    frame->X[2] = stack;
    frame->Sstatus = user_status();
    frame->X[10] = argument0;
    frame->X[11] = argument1;
    frame->X[12] = argument2;
    frame->X[1] = 0;
}

void wit_arch_frame_describe(const WitArchFrame *frame)
{
    const WitU64 values[] = {
        frame->Sepc, frame->X[2], frame->X[10], frame->X[11], frame->X[12], frame->X[17], frame->X[8], frame->X[1]};
    wit_console_write("pc/sp/a0/a1/a2/a7/s0/ra: ");
    for (WitU32 j = 0; j < 8; ++j) {
        wit_console_write_hex(values[j]);
        wit_console_write(j == 7 ? "\n" : "/");
    }
}

/* Contexts need no probing: the floating-point control register's bits are architectural. */
int wit_arch_context_supported(void)
{
    return 1;
}

WitU32 wit_arch_context_profile(void)
{
    return WIT_THREAD_CONTEXT_FPREGS;
}

void wit_arch_context_describe(WitThreadContext *context)
{
    (void)context; /* The RISC-V block carries no mask of its own; CONTEXT_PROFILE reports the FCSR bits. */
}

/* The context's X[i] is x(i + 1): x0 is zero and absent. */
void wit_arch_context_capture(WitThreadContext *context, const WitArchFrame *frame)
{
    for (WitU32 i = 0; i < 31; ++i) {
        context->X[i] = frame->X[i + 1];
    }
    context->Pc = frame->Sepc;
    context->Fcsr = frame->Fcsr & FCSR_USER;
    for (WitU32 i = 0; i < 32; ++i) {
        context->F[i] = frame->F[i];
    }
}

/* Every general register is a thread's own; the mode and the interrupt state are no part of a context. */
int wit_arch_context_registers_valid(const WitThreadContext *context)
{
    (void)context;
    return 1;
}

int wit_arch_context_state_valid(const WitThreadContext *context)
{
    return !(context->Fcsr & ~FCSR_USER) && ((context->Fcsr >> FCSR_ROUNDING_SHIFT) & 7) <= FCSR_ROUNDING_MAX;
}

WitU64 wit_arch_context_pc(const WitThreadContext *context)
{
    return context->Pc;
}

WitU64 wit_arch_context_sp(const WitThreadContext *context)
{
    return context->X[1];
}

/* tp is applied like every other general register: a context a thread captured carries its own. */
void wit_arch_context_apply(WitArchFrame *frame, const WitThreadContext *context)
{
    for (WitU32 i = 0; i < 31; ++i) {
        frame->X[i + 1] = context->X[i];
    }
    frame->X[0] = 0;
    frame->Sepc = context->Pc;
    frame->Sstatus = user_status();
    frame->Fcsr = context->Fcsr & FCSR_USER;
    for (WitU32 i = 0; i < 32; ++i) {
        frame->F[i] = context->F[i];
    }
    frame->Reserved = 0; /* the context's tp, not the TLS base */
}

void wit_arch_cpu_context_describe(WitCpuContextInfo *info, const WitArchFrame *frame)
{
    if (!frame || !wit_arch_frame_returns_to_user(frame)) {
        wit_panic("CPU profile lost the current U-mode frame");
    }
    info->Version = WIT_CPU_CONTEXT_VERSION;
    info->Size = sizeof(*info);
    info->EnabledState = WIT_CPU_CONTEXT_FPREGS;
    info->LegacySaveBytes = sizeof(frame->F);
    info->CodeSelector = 0;
    info->StackSelector = 0;
    info->DebugPolicy = WIT_CPU_DEBUG_DISABLED;
    info->FloatControlMask = (WitU32)FCSR_USER;
}

/* A synchronous trap from U-mode: an environment call is a system call, whose sepc moves past the instruction before
 * anything else, so that a dispatched frame returns after it; every other cause is a fault for the common kernel, with
 * the trap value as the address of the memory faults and zero otherwise. */
WitRv64Frame *wit_rv64_user_trap(WitRv64Frame *frame)
{
    const WitU64 cause = frame->Scause & WIT_RV64_CAUSE_CODE;
    if (cause == CAUSE_ECALL_USER) {
        frame->Sepc += INSTRUCTION_BYTES;
        return wit_user_syscall(frame, frame->X[17], frame->X[10], frame->X[11], frame->X[12]);
    }
    const int memory = cause == WIT_RV64_EXCEPTION_INSTRUCTION_MISALIGNED ||
        cause == WIT_RV64_EXCEPTION_INSTRUCTION_ACCESS ||
        (cause >= WIT_RV64_EXCEPTION_LOAD_MISALIGNED && cause <= WIT_RV64_EXCEPTION_STORE_ACCESS) ||
        cause == WIT_RV64_EXCEPTION_INSTRUCTION_PAGE ||
        cause == WIT_RV64_EXCEPTION_LOAD_PAGE ||
        cause == WIT_RV64_EXCEPTION_STORE_PAGE;
    return wit_user_exception_trap(frame, cause, frame->Scause, memory ? frame->Stval : 0);
}
