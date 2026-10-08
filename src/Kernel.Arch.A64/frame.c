#include "witos/arch.h"
#include "witos/arch_types.h"
#include "witos/platform.h"
#include "user.h"
#include "a64.h"

/* ARM64 implementation of the thread-frame part of witos/arch.h and the EL0 trap entry. A user thread runs at
 * EL0t with its frame at the top of its own kernel stack. System calls: SVC #0 with the number in x8 and the
 * arguments in x0-x2; the status returns in x0 and the value in x1. The raw TLS base is TPIDRRO_EL0, which EL0
 * cannot change, and the compiler TLS block is x18, the platform register, which the kernel sets on every
 * return to EL0. A thread context is the AArch64 block of witos/thread_context.h (plan step K1.4): X0-X30, SP,
 * PC, PSTATE as the condition flags alone, the 32 vector registers, FPCR within the probed mask and FPSR; the fault
 * callback receives it for the EL0 exception classes below and for activations. */

#define CLASS_UNKNOWN 0x00U
#define CLASS_SVC64 0x15U
#define CLASS_SYSTEM_REGISTER 0x18U
#define CLASS_USER_INSTRUCTION_ABORT 0x20U
#define CLASS_PC_ALIGNMENT 0x22U
#define CLASS_USER_DATA_ABORT 0x24U
#define CLASS_SP_ALIGNMENT 0x26U
#define CLASS_BREAKPOINT 0x3CU
/* FPSR bits user code can set: the cumulative exception flags, QC and the AArch32 condition flags. */
#define FPSR_USER 0xF80000DFULL
#define ESR_ISS_IMMEDIATE 0xFFFFULL
#define ESR_FAR_NOT_VALID (1ULL << 10) /* FnV of instruction and data aborts. */
#define ESR_WRITE (1ULL << 6) /* WnR of data aborts. */
/* A thread or callback starts below a zeroed frame record (x29, x30) that ends frame-pointer walks; the stack
 * pointer stays 16-byte aligned. */
#define CALL_FRAME_BYTES 16U

static WitU64 selected_top;
static WitU64 compiler_tls;

static WitU64 stack_low(WitU32 slot, WitU32 thread)
{
    return (WitU64)wit_a64_user_kernel_stacks[slot][thread] + 4096;
}

void wit_arch_select_thread_stack(WitU32 slot, WitU32 thread)
{
    selected_top = stack_low(slot, thread) + WIT_A64_KERNEL_STACK_SIZE;
}

void wit_arch_select_boot_stack(void)
{
    selected_top = (WitU64)wit_a64_kernel_stack + 4096 + WIT_A64_KERNEL_STACK_SIZE;
}

void wit_arch_set_user_tls(WitU64 address, WitU64 compiler_address)
{
    wit_a64_set_thread_pointer(address);
    compiler_tls = compiler_address;
}

void wit_arch_reset_user_tls(void)
{
    wit_a64_set_thread_pointer(0); /* Kernel C uses neither thread register. */
    compiler_tls = 0;
}

int wit_arch_user_tls_is_reset(void)
{
    return wit_a64_thread_pointer() == 0 && compiler_tls == 0;
}

int wit_arch_kernel_space_active(void)
{
    return wit_a64_translation_base() == wit_virtual_kernel_root();
}

void wit_arch_run_user(WitArchFrame *frame, WitU64 root)
{
    if (wit_arch_interrupts_enabled() || !wit_arch_frame_returns_to_user(frame)) {
        wit_panic("User launch needs IRQ masked and an EL0 frame");
    }
    wit_a64_run_user(wit_a64_prepare_resume(frame), root);
}

WIT_NORETURN void wit_arch_leave_user(void)
{
    wit_a64_leave_user();
}

void wit_arch_space_switch(WitU64 root)
{
    if (wit_a64_translation_base() != root) {
        wit_a64_switch_translation(root);
    }
}

/* A dispatched frame of another thread, on that thread's own kernel stack, resumes here without returning: the
 * current kernel stack is abandoned (K5.2c). */
WIT_NORETURN void wit_arch_resume_frame(WitArchFrame *frame)
{
    if (wit_arch_interrupts_enabled() || !wit_arch_frame_returns_to_user(frame)) {
        wit_panic("Frame resume needs IRQ masked and an EL0 frame");
    }
    wit_a64_resume_frame(wit_a64_prepare_resume(frame));
}

WitA64Frame *wit_a64_prepare_resume(WitA64Frame *frame)
{
    if ((frame->Spsr & WIT_A64_SPSR_MODE) == 0) {
        if ((WitU64)frame + WIT_A64_FRAME_SIZE != selected_top) {
            wit_panic("EL0 frame is not at the top of the selected kernel stack");
        }
        frame->X[18] = compiler_tls;
    }
    return frame;
}

static WitArchFrame *build_frame(WitU32 slot, WitU32 thread, WitU64 entry, WitU64 argument, WitU64 stack_pointer)
{
    WitArchFrame *frame = (WitArchFrame *)(stack_low(slot, thread) + WIT_A64_KERNEL_STACK_SIZE - WIT_A64_FRAME_SIZE);
    for (WitU32 i = 0; i < sizeof(*frame); ++i) {
        ((WitU8 *)frame)[i] = 0;
    }
    frame->X[0] = argument;
    frame->Elr = entry;
    frame->Spsr = 0; /* EL0t with every exception unmasked; a zero link register traps an accidental RET. */
    frame->Sp = stack_pointer;
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

/* TPIDR_EL0 is written at EL0 and preserved in the frame; the kernel sets only TPIDRRO_EL0 (RFC 0011 section 7.3). */
int wit_arch_user_tls_settable(void)
{
    return 0;
}

int wit_arch_kernel_stack_contains(WitU32 slot, WitU32 thread, const void *object, WitU64 size)
{
    const WitU64 low = stack_low(slot, thread);
    return (WitU64)object >= low && (WitU64)object <= low + WIT_A64_KERNEL_STACK_SIZE - size;
}

int wit_arch_frame_owned(const WitArchFrame *frame, WitU32 slot, WitU32 thread)
{
    return wit_arch_kernel_stack_contains(slot, thread, frame, sizeof(*frame)) && ((WitU64)frame & 15) == 0;
}

int wit_arch_frame_from_user(const WitArchFrame *frame)
{
    return (frame->Spsr & WIT_A64_SPSR_MODE) == 0;
}

int wit_arch_frame_returns_to_user(const WitArchFrame *frame)
{
    return (frame->Spsr & (WIT_A64_SPSR_MODE | WIT_A64_SPSR_ILLEGAL)) == 0;
}

int wit_arch_frame_is_idle(const WitArchFrame *frame, WitU32 slot, WitU32 thread)
{
    const WitU64 low = stack_low(slot, thread);
    return (frame->Spsr & WIT_A64_SPSR_MODE) == WIT_A64_SPSR_EL1H &&
        frame->Sp >= low &&
        frame->Sp < low + WIT_A64_KERNEL_STACK_SIZE &&
        frame->Elr == (WitU64)wit_a64_idle_resume &&
        wit_arch_user_tls_is_reset();
}

WitU64 wit_arch_frame_pc(const WitArchFrame *frame)
{
    return frame->Elr;
}

WitU64 wit_arch_frame_sp(const WitArchFrame *frame)
{
    return frame->Sp;
}

void wit_arch_frame_prepare_return(WitArchFrame *frame, int syscall)
{
    /* EL0t with exceptions unmasked; preemption keeps the condition flags, a system call clears them. */
    frame->Spsr = syscall ? 0 : (frame->Spsr & WIT_A64_SPSR_FLAGS);
}

WitU64 *wit_arch_frame_status(WitArchFrame *frame)
{
    return &frame->X[0];
}

WitU64 *wit_arch_frame_value(WitArchFrame *frame)
{
    return &frame->X[1];
}

void wit_arch_frame_set_result(WitArchFrame *frame, WitU64 status, WitU64 value)
{
    frame->X[0] = status;
    frame->X[1] = value;
}

WitU64 wit_arch_callback_stack(WitU64 sp, WitU32 *call_frame_bytes)
{
    *call_frame_bytes = CALL_FRAME_BYTES; /* The return address travels in x30; no shadow space. */
    return (sp & ~15ULL) - CALL_FRAME_BYTES;
}

void wit_arch_frame_enter_callback(
    WitArchFrame *frame, WitU64 entry, WitU64 stack, WitU64 argument0, WitU64 argument1, WitU64 argument2)
{
    frame->Elr = entry;
    frame->Sp = stack;
    frame->Spsr = 0;
    frame->X[0] = argument0;
    frame->X[1] = argument1;
    frame->X[2] = argument2;
    frame->X[30] = 0;
}

void wit_arch_frame_describe(const WitArchFrame *frame)
{
    const WitU64 values[] = {
        frame->Elr, frame->Sp, frame->X[0], frame->X[1], frame->X[2], frame->X[8], frame->X[29], frame->X[30]};
    wit_console_write("pc/sp/x0/x1/x2/x8/x29/x30: ");
    for (WitU32 j = 0; j < 8; ++j) {
        wit_console_write_hex(values[j]);
        wit_console_write(j == 7 ? "\n" : "/");
    }
}

/* Contexts exist once the FPCR mask was probed at boot (context.c). */
int wit_arch_context_supported(void)
{
    return wit_a64_float_control_mask() != 0;
}

WitU32 wit_arch_context_profile(void)
{
    return WIT_THREAD_CONTEXT_FPSIMD;
}

void wit_arch_context_describe(WitThreadContext *context)
{
    (void)context; /* The AArch64 block carries no mask of its own; CONTEXT_PROFILE reports the FPCR mask. */
}

static void copy_vectors(WitU8 *output, const WitU8 *input)
{
    for (WitU32 i = 0; i < 512; ++i) {
        output[i] = input[i];
    }
}

void wit_arch_context_capture(WitThreadContext *context, const WitArchFrame *frame)
{
    for (WitU32 i = 0; i < 31; ++i) {
        context->X[i] = frame->X[i];
    }
    context->Sp = frame->Sp;
    context->Pc = frame->Elr;
    context->Pstate = frame->Spsr & WIT_CONTEXT_USER_PSTATE;
    context->Fpcr = frame->Fpcr & wit_a64_float_control_mask();
    context->Fpsr = frame->Fpsr & FPSR_USER;
    copy_vectors(context->V, (const WitU8 *)frame->Q);
}

int wit_arch_context_registers_valid(const WitThreadContext *context)
{
    return !(context->Pstate & ~WIT_CONTEXT_USER_PSTATE);
}

int wit_arch_context_state_valid(const WitThreadContext *context)
{
    return !(context->Fpcr & ~wit_a64_float_control_mask()) && !(context->Fpsr & ~FPSR_USER);
}

WitU64 wit_arch_context_pc(const WitThreadContext *context)
{
    return context->Pc;
}

WitU64 wit_arch_context_sp(const WitThreadContext *context)
{
    return context->Sp;
}

/* TPIDR_EL0 stays the frame's; x18 is replaced by the compiler TLS on resume (wit_a64_prepare_resume). */
void wit_arch_context_apply(WitArchFrame *frame, const WitThreadContext *context)
{
    for (WitU32 i = 0; i < 31; ++i) {
        frame->X[i] = context->X[i];
    }
    frame->Sp = context->Sp;
    frame->Elr = context->Pc;
    frame->Spsr = context->Pstate; /* EL0t, every exception unmasked, the condition flags of the context. */
    frame->Fpcr = context->Fpcr;
    frame->Fpsr = context->Fpsr;
    copy_vectors((WitU8 *)frame->Q, context->V);
}

void wit_arch_cpu_context_describe(WitCpuContextInfo *info, const WitArchFrame *frame)
{
    if (!frame || !wit_arch_frame_returns_to_user(frame)) {
        wit_panic("CPU profile lost the current EL0 frame");
    }
    info->Version = WIT_CPU_CONTEXT_VERSION;
    info->Size = sizeof(*info);
    info->EnabledState = WIT_CPU_CONTEXT_FPSIMD;
    info->LegacySaveBytes = sizeof(frame->Q);
    info->CodeSelector = 0;
    info->StackSelector = 0;
    info->DebugPolicy = WIT_CPU_DEBUG_DISABLED;
    info->FloatControlMask = (WitU32)wit_a64_float_control_mask();
}

/* The EL0 exception classes the fault callback receives: undefined instructions, trapped system register accesses,
 * instruction and data aborts, PC and SP alignment faults and BRK. */
int wit_arch_exception_deliverable(WitU64 vector)
{
    return vector == CLASS_UNKNOWN ||
        vector == CLASS_SYSTEM_REGISTER ||
        vector == CLASS_USER_INSTRUCTION_ABORT ||
        vector == CLASS_PC_ALIGNMENT ||
        vector == CLASS_USER_DATA_ABORT ||
        vector == CLASS_SP_ALIGNMENT ||
        vector == CLASS_BREAKPOINT;
}

/* vector is the exception class, error the syndrome (ESR_EL1) and address the fault address. */
WitArchExceptionKind wit_arch_exception_kind(WitU64 vector, WitU64 error, WitU64 address)
{
    if (vector == CLASS_USER_DATA_ABORT && address == 0 && !(error & ESR_FAR_NOT_VALID)) {
        return (error & ESR_WRITE) ? WitArchExceptionNullWrite : WitArchExceptionNullRead;
    }
    if (vector == CLASS_UNKNOWN) {
        return WitArchExceptionIllegal;
    }
    return WitArchExceptionOther; /* Integer division by zero does not trap on ARM64. */
}

/* The trap entry passes the fault address of aborts and alignment faults and zero for the other classes. */
void wit_arch_exception_record(WitUserExceptionInfo *info, const WitArchFrame *frame, WitU64 address)
{
    info->Address = address;
    info->RawState = frame->Spsr;
}

void wit_arch_exception_record_software(WitUserExceptionInfo *info, const WitThreadContext *context)
{
    info->Address = context->Pc;
    info->RawState = context->Pstate;
}

void wit_arch_fault_from_frame(WitArchFaultState *state, const WitArchFrame *frame)
{
    state->Elr = frame->Elr;
    state->Spsr = frame->Spsr;
    state->Esr = frame->Esr;
}

void wit_arch_fault_from_record(WitArchFaultState *state, const WitUserExceptionInfo *record)
{
    state->Elr = record->Context.Pc;
    state->Spsr = record->Context.Pstate;
    state->Esr = record->Error; /* The syndrome of the delivered fault; zero for a software record. */
}

int wit_arch_fault_from_user(const WitArchFaultState *state)
{
    return (state->Spsr & WIT_A64_SPSR_MODE) == 0;
}

void wit_arch_fault_describe(const WitArchFaultState *state)
{
    wit_console_write(" elr=");
    wit_console_write_hex(state->Elr);
    wit_console_write(" spsr=");
    wit_console_write_hex(state->Spsr);
    wit_console_write(" esr=");
    wit_console_write_hex(state->Esr);
}

/* The size of the data cache is not reported before CCSIDR decoding exists; zero means unknown. */
WitU64 wit_arch_cache_size(void)
{
    return 0;
}

WitA64Frame *wit_a64_user_trap(WitA64Frame *frame)
{
    const WitU64 exception_class = (frame->Esr >> 26) & 0x3F;
    WitU64 address = 0;
    if (exception_class == CLASS_SVC64 && (frame->Esr & ESR_ISS_IMMEDIATE) == 0) {
        return wit_user_syscall(frame, frame->X[8], frame->X[0], frame->X[1], frame->X[2]);
    }
    if (exception_class == CLASS_PC_ALIGNMENT ||
        ((exception_class == CLASS_USER_INSTRUCTION_ABORT || exception_class == CLASS_USER_DATA_ABORT) &&
            !(frame->Esr & ESR_FAR_NOT_VALID))) {
        address = frame->Far;
    }
    return wit_user_exception_trap(frame, exception_class, frame->Esr, address);
}
