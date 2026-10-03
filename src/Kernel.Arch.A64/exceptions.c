#include "witos/arch.h"
#include "witos/platform.h"
#include "a64.h"

/* ARM64 exception vectors of the kernel. Interrupts go to the scheduler and synchronous exceptions from EL0 to
 * the common kernel; every other exception is fatal and is reported with its syndrome. */

#define FRAME_OFFSET(field) ((WitU64) & ((WitA64Frame *)0)->field)

WIT_STATIC_ASSERT(sizeof(WitA64Frame) == WIT_A64_FRAME_SIZE, "vectors.asm frame size");
WIT_STATIC_ASSERT(
    FRAME_OFFSET(Sp) == 248 && FRAME_OFFSET(Elr) == 256 && FRAME_OFFSET(Spsr) == 264, "vectors.asm frame layout");
WIT_STATIC_ASSERT(
    FRAME_OFFSET(Far) == 280 && FRAME_OFFSET(Fpcr) == 288 && FRAME_OFFSET(Tpidr) == 304, "vectors.asm frame layout");
WIT_STATIC_ASSERT(FRAME_OFFSET(Q) == 320, "vectors.asm frame layout");

/* Vector entries: 0-3 current EL on SP_EL0, 4-7 current EL on SP_ELx, 8-15 lower EL; synchronous, IRQ, FIQ,
 * SError within each group. */
#define VECTOR_KERNEL_SYNCHRONOUS 4U
#define VECTOR_KERNEL_INTERRUPT 5U
#define VECTOR_USER_SYNCHRONOUS 8U
#define VECTOR_USER_INTERRUPT 9U
#define VECTOR_INTERRUPT 1U
#define VECTOR_SERROR 3U

/* Exception classes (ESR_EL1.EC) that the kernel names. */
#define CLASS_UNKNOWN 0x00U
#define CLASS_INSTRUCTION_ABORT 0x21U
#define CLASS_PC_ALIGNMENT 0x22U
#define CLASS_DATA_ABORT 0x25U
#define CLASS_SP_ALIGNMENT 0x26U
#define CLASS_BREAKPOINT 0x3CU

static const char *exception_name(WitU64 kind, WitU64 exception_class)
{
    if (kind != VECTOR_KERNEL_SYNCHRONOUS) {
        return (kind & 3) == VECTOR_INTERRUPT ? "Unexpected interrupt"
            : (kind & 3) == VECTOR_SERROR     ? "SError"
                                              : "Unhandled exception";
    }
    switch (exception_class) {
    case CLASS_UNKNOWN:
        return "Undefined instruction";
    case CLASS_INSTRUCTION_ABORT:
        return "Instruction abort";
    case CLASS_PC_ALIGNMENT:
        return "PC alignment fault";
    case CLASS_DATA_ABORT:
        return "Data abort";
    case CLASS_SP_ALIGNMENT:
        return "SP alignment fault";
    case CLASS_BREAKPOINT:
        return "Breakpoint";
    default:
        return "Unhandled exception";
    }
}

static WitU64 kernel_stack_begin(void)
{
    return (WitU64)wit_a64_kernel_stack + 4096;
}

static int on_kernel_stack(WitU64 address)
{
    const WitU64 begin = kernel_stack_begin();
    return address > begin && address <= begin + WIT_A64_KERNEL_STACK_SIZE;
}

/* EL0 may not mask interrupts (UMA), wait for interrupts or events (nTWI, nTWE) or use a misaligned stack
 * (SA0); it may use FP/SIMD without traps (CPACR_EL1.FPEN). */
#define SCTLR_SA0 (1ULL << 4)
#define SCTLR_UMA (1ULL << 9)
#define SCTLR_NTWI (1ULL << 16)
#define SCTLR_NTWE (1ULL << 18)
#define SCTLR_E0E (1ULL << 24)
#define CPACR_FPEN (3ULL << 20)

static void configure_user_mode(void)
{
    const WitU64 control = (wit_a64_system_control() & ~(SCTLR_UMA | SCTLR_NTWI | SCTLR_NTWE)) | SCTLR_SA0;
    wit_a64_set_system_control(control);
    wit_a64_set_fp_access(wit_a64_fp_access() | CPACR_FPEN);
    if (wit_a64_system_control() != control ||
        (control & SCTLR_E0E) ||
        (wit_a64_fp_access() & CPACR_FPEN) != CPACR_FPEN) {
        wit_panic("EL0 control was not applied");
    }
}

void wit_arch_initialize(void)
{
    const WitU64 stack_begin = kernel_stack_begin();

    /* UEFI hands over at EL1 on QEMU virt; the kernel takes no EL2 or EL3 duties. */
    if (wit_a64_exception_level() != 1) {
        wit_panic("Unsupported exception level");
    }
    if (!on_kernel_stack(wit_a64_stack_pointer())) {
        wit_panic("Not running on kernel stack");
    }
    wit_console_write("[TEST-PASS] Cpu.KernelStack\n");
    wit_console_write("Kernel stack: ");
    wit_console_write_hex(stack_begin);
    wit_console_write("-");
    wit_console_write_hex(stack_begin + WIT_A64_KERNEL_STACK_SIZE);
    wit_console_write("\n");

    wit_a64_set_vectors(wit_a64_vectors);
    if (wit_a64_vectors_base() != (WitU64)wit_a64_vectors) {
        wit_panic("Exception vectors were not installed");
    }
    configure_user_mode();
    wit_console_write("[TEST-PASS] Cpu.ExceptionTables\n");
}

WitA64Frame *wit_a64_exception(WitA64Frame *frame, WitU64 kind)
{
    const WitU64 exception_class = (frame->Esr >> 26) & 0x3F;

    if (kind == VECTOR_KERNEL_INTERRUPT || kind == VECTOR_USER_INTERRUPT) {
        return wit_a64_prepare_resume(wit_a64_interrupt(frame));
    }
    if (kind == VECTOR_USER_SYNCHRONOUS) {
        return wit_a64_prepare_resume(wit_a64_user_trap(frame));
    }

    wit_console_write("[EXCEPTION] kind=");
    wit_console_write_u64(kind);
    wit_console_write(" class=");
    wit_console_write_hex(exception_class);
    wit_console_write(" esr=");
    wit_console_write_hex(frame->Esr);
    wit_console_write(" elr=");
    wit_console_write_hex(frame->Elr);
    wit_console_write(" far=");
    wit_console_write_hex(frame->Far);
    wit_console_write(" spsr=");
    wit_console_write_hex(frame->Spsr);
    wit_console_write(" sp=");
    wit_console_write_hex(frame->Sp);
    wit_console_write(on_kernel_stack(frame->Sp) ? " stack=kernel\n" : " stack=unknown\n");
    wit_panic(exception_name(kind, exception_class));
}
