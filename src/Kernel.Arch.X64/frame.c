#include "x64.h"
#include "user.h"
#include "witos/arch_types.h"
#include "witos/platform.h"
#include "witos/x64_instructions.h"

/* x64 implementation of the thread-frame part of witos/arch.h and the user trap entries. */

#define FS_BASE 0xC0000100UL
#define GS_BASE 0xC0000101UL
#define USER_FLAGS 0x200CD5ULL
#define USER_FLAGS_FIXED 0x202ULL
#define CALL_FRAME_BYTES 40U /* Win64 return address plus the ABI shadow space. */
/* The SysV red zone below a layer-2 stack pointer (S3.1): a leaf function keeps data there without moving RSP, so
 * a delivery that interrupts it must leave those bytes intact. */
#define RED_ZONE_BYTES 128U

static WitU64 stack_low(WitU32 slot, WitU32 thread)
{
    return (WitU64)wit_x64_user_kernel_stacks[slot][thread] + 4096;
}

void wit_arch_select_thread_stack(WitU32 slot, WitU32 thread)
{
    wit_x64_set_kernel_stack(stack_low(slot, thread) + WIT_KERNEL_STACK_SIZE);
}

void wit_arch_select_boot_stack(void)
{
    wit_x64_set_kernel_stack((WitU64)wit_x64_kernel_stack + 4096 + WIT_KERNEL_STACK_SIZE);
}

void wit_arch_reset_user_tls(void)
{
    wit_x64_write_msr(FS_BASE, 0); /* Kernel C has no segment-based TLS. */
    wit_x64_write_msr(GS_BASE, 0);
}

int wit_arch_user_tls_is_reset(void)
{
    return wit_x64_read_msr(FS_BASE) == 0 && wit_x64_read_msr(GS_BASE) == 0;
}

int wit_arch_kernel_space_active(void)
{
    return (wit_x64_read_cr3() & 0x000FFFFFFFFFF000ULL) == wit_virtual_kernel_root();
}

int wit_arch_interrupts_enabled(void)
{
    return (wit_x64_read_flags() & 0x200) != 0;
}

/* The argument travels in RDI, the first argument register of the SysV convention (RFC 0011 section 6.1). */
static WitArchFrame *build_frame(WitU32 slot, WitU32 thread, WitU64 entry, WitU64 argument, WitU64 stack_pointer)
{
    WitArchFrame *frame = (WitArchFrame *)(stack_low(slot, thread) + WIT_KERNEL_STACK_SIZE - 4096);
    for (WitU32 i = 0; i < sizeof(*frame); ++i) {
        ((WitU8 *)frame)[i] = 0;
    }
    frame->FxState[0] = 0x7F;
    frame->FxState[1] = 0x03;
    frame->FxState[24] = 0x80;
    frame->FxState[25] = 0x1F;
    frame->Rdi = argument;
    frame->Rip = entry;
    frame->Cs = WIT_USER_CS;
    frame->Ss = WIT_USER_SS;
    frame->Rflags = USER_FLAGS_FIXED;
    frame->Rsp = stack_pointer;
    return frame;
}

WitArchFrame *wit_arch_frame_create(WitU32 slot, WitU32 thread, WitU64 entry, WitU64 argument, WitU64 stack_top)
{
    /* Aligned ABI entry, zero return address traps accidental RET. */
    return build_frame(slot, thread, entry, argument, stack_top - CALL_FRAME_BYTES);
}

WitArchFrame *wit_arch_frame_create_at(WitU32 slot, WitU32 thread, WitU64 entry, WitU64 argument, WitU64 stack_pointer)
{
    return build_frame(slot, thread, entry, argument, stack_pointer);
}

int wit_arch_user_tls_settable(void)
{
    return 1;
}

/* SYSCALL (RFC 0011 section 6.1, K5.2a): the SysV argument registers, the one x64 transport since K8.4b. The entry
 * (user_entry.S) builds the same frame as an interrupt gate's. */
WitInterruptContext *wit_x64_user_syscall_sysv(WitInterruptContext *context)
{
    return wit_user_syscall(context, context->Rax, context->Rdi, context->Rsi, context->Rdx);
}

/* Enables SYSCALL: EFER.SCE; STAR's kernel selectors 8/0x10 and the SYSRET base 0x23 (SS 0x2B, CS 0x33); LSTAR the
 * entry; SFMASK clears IF, TF, DF and AC on entry as the interrupt gate does. */
void wit_x64_enable_syscall(void)
{
    wit_x64_write_msr(0xC0000080, wit_x64_read_msr(0xC0000080) | 1ULL);
    wit_x64_write_msr(0xC0000081, (0x23ULL << 48) | (0x08ULL << 32));
    wit_x64_write_msr(0xC0000082, (WitU64)wit_x64_syscall_entry);
    wit_x64_write_msr(0xC0000084, 0x40700ULL);
}

int wit_arch_kernel_stack_contains(WitU32 slot, WitU32 thread, const void *object, WitU64 size)
{
    const WitU64 low = stack_low(slot, thread);
    return (WitU64)object >= low && (WitU64)object <= low + WIT_KERNEL_STACK_SIZE - size;
}

int wit_arch_frame_owned(const WitArchFrame *frame, WitU32 slot, WitU32 thread)
{
    return wit_arch_kernel_stack_contains(slot, thread, frame, sizeof(*frame)) && ((WitU64)frame & 15) == 0;
}

int wit_arch_frame_from_user(const WitArchFrame *frame)
{
    return (frame->Cs & 3) == 3;
}

int wit_arch_frame_returns_to_user(const WitArchFrame *frame)
{
    return frame->Cs == WIT_USER_CS && frame->Ss == WIT_USER_SS;
}

int wit_arch_frame_is_idle(const WitArchFrame *frame, WitU32 slot, WitU32 thread)
{
    const WitU64 low = stack_low(slot, thread);
    return frame->Cs == 8 &&
        (frame->Ss == 0 || frame->Ss == 0x10) &&
        frame->Rsp >= low &&
        frame->Rsp < low + WIT_KERNEL_STACK_SIZE &&
        frame->Rip == (WitU64)wit_x64_idle_resume &&
        wit_arch_user_tls_is_reset();
}

WitU64 wit_arch_frame_pc(const WitArchFrame *frame)
{
    return frame->Rip;
}

WitU64 wit_arch_frame_sp(const WitArchFrame *frame)
{
    return frame->Rsp;
}

void wit_arch_frame_prepare_return(WitArchFrame *frame, int syscall)
{
    frame->Rflags = syscall ? USER_FLAGS_FIXED : (frame->Rflags & USER_FLAGS) | USER_FLAGS_FIXED;
}

WitU64 *wit_arch_frame_status(WitArchFrame *frame)
{
    return &frame->Rax;
}

WitU64 *wit_arch_frame_value(WitArchFrame *frame)
{
    return &frame->Rdx;
}

void wit_arch_frame_set_result(WitArchFrame *frame, WitU64 status, WitU64 value)
{
    frame->Rax = status;
    frame->Rdx = value;
}

WitU64 wit_arch_callback_stack(WitU64 sp, WitU32 *call_frame_bytes)
{
    *call_frame_bytes = CALL_FRAME_BYTES;
    return ((sp - RED_ZONE_BYTES) & ~15ULL) - CALL_FRAME_BYTES;
}

void wit_arch_frame_enter_callback(
    WitArchFrame *frame, WitU64 entry, WitU64 stack, WitU64 argument0, WitU64 argument1, WitU64 argument2)
{
    frame->Rip = entry;
    frame->Rsp = stack;
    frame->Rflags = USER_FLAGS_FIXED;
    frame->Rdi = argument0; /* the SysV argument registers, as a thread's entry (K8.4d) */
    frame->Rsi = argument1;
    frame->Rdx = argument2;
}

void wit_arch_frame_describe(const WitArchFrame *frame)
{
    const WitU64 values[] = {
        frame->Rip, frame->Rsp, frame->Rax, frame->Rcx, frame->Rdx, frame->Rbx, frame->Rsi, frame->Rdi};
    wit_console_write("rip/rsp/rax/rcx/rdx/rbx/rsi/rdi: ");
    for (WitU32 j = 0; j < 8; ++j) {
        wit_console_write_hex(values[j]);
        wit_console_write(j == 7 ? "\n" : "/");
    }
}

int wit_arch_exception_deliverable(WitU64 vector)
{
    return vector == 0 || vector == 3 || vector == 6 || vector == 13 || vector == 14;
}

WitArchExceptionKind wit_arch_exception_kind(WitU64 vector, WitU64 error, WitU64 address)
{
    if (vector == 14 && address == 0 && !(error & 16)) {
        return (error & 2) ? WitArchExceptionNullWrite : WitArchExceptionNullRead;
    }
    if (vector == 0) {
        return WitArchExceptionDivide;
    }
    if (vector == 6) {
        return WitArchExceptionIllegal;
    }
    return WitArchExceptionOther;
}

void wit_arch_exception_record(WitUserExceptionInfo *info, const WitArchFrame *frame, WitU64 address)
{
    info->Address = info->Vector == 14 ? address : 0;
    info->RawState = frame->Rflags;
    info->Context.Rflags = (info->Context.Rflags & USER_FLAGS) | USER_FLAGS_FIXED;
}

void wit_arch_fault_from_frame(WitArchFaultState *state, const WitArchFrame *frame)
{
    state->Rip = frame->Rip;
    state->Cs = frame->Cs;
    state->Ss = frame->Ss;
}

void wit_arch_fault_from_record(WitArchFaultState *state, const WitUserExceptionInfo *record)
{
    state->Rip = record->Context.Rip;
    state->Cs = record->Context.Cs;
    state->Ss = record->Context.Ss;
}

int wit_arch_fault_from_user(const WitArchFaultState *state)
{
    return (state->Cs & 3) == 3;
}

void wit_arch_fault_describe(const WitArchFaultState *state)
{
    wit_console_write(" cs=");
    wit_console_write_hex(state->Cs);
}

WitInterruptContext *wit_x64_user_exception(WitInterruptContext *context, WitU64 vector, WitU64 error, WitU64 address)
{
    return wit_user_exception_trap(context, vector, error, address);
}

WIT_NORETURN void wit_x64_user_fault(const WitExceptionFrame *frame, WitU64 address)
{
    const WitArchFaultState state = {frame->Rip, frame->Cs, frame->Ss};
    wit_user_fault(frame, sizeof(*frame), frame->Vector, frame->Error, address, &state);
}
