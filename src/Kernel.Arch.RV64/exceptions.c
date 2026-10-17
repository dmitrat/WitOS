#include "witos/arch.h"
#include "witos/arch_types.h"
#include "witos/platform.h"
#include "witos/rv64_csr.h"
#include "user.h"
#include "rv64.h"

/* RISC-V traps of the kernel (plan step V1.2). Interrupts go to the scheduler and traps from U-mode to the common
 * kernel (V1.3); every exception taken in S-mode is fatal and is reported with its cause and value. */

#define FRAME_OFFSET(field) __builtin_offsetof(WitRv64Frame, field)

WIT_STATIC_ASSERT(sizeof(WitRv64Frame) == WIT_RV64_FRAME_SIZE, "entry.S frame size");
WIT_STATIC_ASSERT(FRAME_OFFSET(Sepc) == 256 &&
        FRAME_OFFSET(Sstatus) == 264 &&
        FRAME_OFFSET(Scause) == 272 &&
        FRAME_OFFSET(Stval) == 280 &&
        FRAME_OFFSET(F) == 288 &&
        FRAME_OFFSET(Fcsr) == 544,
    "entry.S frame layout");

static const char *exception_name(WitU64 cause)
{
    switch (cause) {
    case WIT_RV64_EXCEPTION_INSTRUCTION_MISALIGNED:
        return "Instruction address misaligned";
    case WIT_RV64_EXCEPTION_INSTRUCTION_ACCESS:
        return "Instruction access fault";
    case WIT_RV64_EXCEPTION_ILLEGAL:
        return "Illegal instruction";
    case WIT_RV64_EXCEPTION_BREAKPOINT:
        return "Breakpoint";
    case WIT_RV64_EXCEPTION_LOAD_MISALIGNED:
        return "Load address misaligned";
    case WIT_RV64_EXCEPTION_LOAD_ACCESS:
        return "Load access fault";
    case WIT_RV64_EXCEPTION_STORE_MISALIGNED:
        return "Store address misaligned";
    case WIT_RV64_EXCEPTION_STORE_ACCESS:
        return "Store access fault";
    case WIT_RV64_EXCEPTION_INSTRUCTION_PAGE:
        return "Instruction page fault";
    case WIT_RV64_EXCEPTION_LOAD_PAGE:
        return "Load page fault";
    case WIT_RV64_EXCEPTION_STORE_PAGE:
        return "Store page fault";
    default:
        return "Unhandled exception";
    }
}

static WitU64 kernel_stack_begin(void)
{
    return (WitU64)wit_rv64_kernel_stack + 4096;
}

static int on_kernel_stack(WitU64 address)
{
    const WitU64 begin = kernel_stack_begin();
    return address > begin && address <= begin + WIT_RV64_KERNEL_STACK_SIZE;
}

void wit_arch_initialize(void)
{
    const WitU64 stack_begin = kernel_stack_begin();
    if (!on_kernel_stack(wit_rv64_stack_pointer())) {
        wit_panic("Not running on kernel stack");
    }
    wit_console_write("[TEST-PASS] Cpu.KernelStack\n");
    wit_console_write("Kernel stack: ");
    wit_console_write_hex(stack_begin);
    wit_console_write("-");
    wit_console_write_hex(stack_begin + WIT_RV64_KERNEL_STACK_SIZE);
    wit_console_write("\n");

    /* The trap vector in direct mode (the low bits of stvec are the mode); sscratch zero names S-mode as the running
     * mode to the entry. */
    if (((WitU64)wit_rv64_trap_vector & 3) != 0) {
        wit_panic("Trap vector is not aligned");
    }
    wit_rv64_write_sscratch(0);
    wit_rv64_write_stvec((WitU64)wit_rv64_trap_vector);
    if (wit_rv64_read_stvec() != (WitU64)wit_rv64_trap_vector) {
        wit_panic("Trap vector was not installed");
    }
    /* No interrupt source is enabled until the timer starts; the floating-point unit is on, so that the trap entry
     * can save its registers; the kernel reads user pages through its own view, never through SUM; U-mode reads no
     * counter directly. */
    wit_rv64_clear_sie(WIT_RV64_SIE_SOFTWARE | WIT_RV64_SIE_TIMER | WIT_RV64_SIE_EXTERNAL);
    wit_rv64_clear_sstatus(WIT_RV64_SSTATUS_FS | WIT_RV64_SSTATUS_SUM | WIT_RV64_SSTATUS_MXR);
    wit_rv64_set_sstatus(WIT_RV64_SSTATUS_FS_INITIAL);
    wit_rv64_write_scounteren(0);
    if ((wit_rv64_read_sstatus() & WIT_RV64_SSTATUS_FS) == 0) {
        wit_panic("Floating-point unit could not be enabled");
    }
    wit_console_write("[TEST-PASS] Cpu.ExceptionTables\n");
}

WitRv64Frame *wit_rv64_trap(WitRv64Frame *frame)
{
    const WitU64 cause = frame->Scause & WIT_RV64_CAUSE_CODE;
    if (frame->Scause & WIT_RV64_CAUSE_INTERRUPT) {
        return wit_rv64_interrupt(frame);
    }
    if ((frame->Sstatus & WIT_RV64_SSTATUS_SPP) == 0) {
        wit_panic("A trap from U-mode before user mode exists (V1.3)");
    }
    wit_console_write("[EXCEPTION] cause=");
    wit_console_write_hex(frame->Scause);
    wit_console_write(" sepc=");
    wit_console_write_hex(frame->Sepc);
    wit_console_write(" stval=");
    wit_console_write_hex(frame->Stval);
    wit_console_write(" sstatus=");
    wit_console_write_hex(frame->Sstatus);
    wit_console_write(" sp=");
    wit_console_write_hex(frame->X[2]);
    wit_console_write(on_kernel_stack(frame->X[2]) ? " stack=kernel\n" : " stack=unknown\n");
    wit_panic(exception_name(cause));
}

/* The exceptions the fault callback receives (V1.3): the access, alignment and page faults, illegal instructions and
 * breakpoints; an environment call is a system call, never a fault. */
int wit_arch_exception_deliverable(WitU64 vector)
{
    return vector <= WIT_RV64_EXCEPTION_STORE_ACCESS ||
        vector == WIT_RV64_EXCEPTION_INSTRUCTION_PAGE ||
        vector == WIT_RV64_EXCEPTION_LOAD_PAGE ||
        vector == WIT_RV64_EXCEPTION_STORE_PAGE;
}

/* vector is the exception code, error the whole scause and address the trap value. */
WitArchExceptionKind wit_arch_exception_kind(WitU64 vector, WitU64 error, WitU64 address)
{
    (void)error;
    if (address == 0 && (vector == WIT_RV64_EXCEPTION_LOAD_PAGE || vector == WIT_RV64_EXCEPTION_LOAD_ACCESS)) {
        return WitArchExceptionNullRead;
    }
    if (address == 0 && (vector == WIT_RV64_EXCEPTION_STORE_PAGE || vector == WIT_RV64_EXCEPTION_STORE_ACCESS)) {
        return WitArchExceptionNullWrite;
    }
    if (vector == WIT_RV64_EXCEPTION_ILLEGAL) {
        return WitArchExceptionIllegal;
    }
    return WitArchExceptionOther; /* Integer division by zero does not trap on RISC-V. */
}

void wit_arch_exception_record(WitUserExceptionInfo *info, const WitArchFrame *frame, WitU64 address)
{
    info->Address = address;
    info->RawState = frame->Sstatus;
}

void wit_arch_fault_from_frame(WitArchFaultState *state, const WitArchFrame *frame)
{
    state->Sepc = frame->Sepc;
    state->Sstatus = frame->Sstatus;
    state->Scause = frame->Scause;
    state->Stval = frame->Stval;
}

void wit_arch_fault_from_record(WitArchFaultState *state, const WitUserExceptionInfo *record)
{
    state->Sepc = record->Context.Pc;
    state->Sstatus = 0; /* a record is a user fault's */
    state->Scause = record->Error; /* the cause of the delivered fault; zero for a software record */
    state->Stval = record->Address;
}

int wit_arch_fault_from_user(const WitArchFaultState *state)
{
    return (state->Sstatus & WIT_RV64_SSTATUS_SPP) == 0;
}

void wit_arch_fault_describe(const WitArchFaultState *state)
{
    wit_console_write(" sepc=");
    wit_console_write_hex(state->Sepc);
    wit_console_write(" sstatus=");
    wit_console_write_hex(state->Sstatus);
    wit_console_write(" scause=");
    wit_console_write_hex(state->Scause);
    wit_console_write(" stval=");
    wit_console_write_hex(state->Stval);
}
