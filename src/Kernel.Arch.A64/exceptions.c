#include "witos/arch.h"
#include "witos/platform.h"
#include "a64.h"

/* ARM64 exception vectors of the kernel. Every exception taken at EL1 is fatal for now and is reported with its
 * syndrome; the timer interrupt arrives with A1.3. */

WIT_STATIC_ASSERT(sizeof(WitA64Frame) == 288, "vectors.asm frame layout");

/* Vector entries: 0-3 current EL on SP_EL0, 4-7 current EL on SP_ELx, 8-15 lower EL; synchronous, IRQ, FIQ,
 * SError within each group. */
#define VECTOR_KERNEL_SYNCHRONOUS 4U
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

static int on_boot_stack(WitU64 address)
{
    const WitU64 begin = (WitU64)wit_a64_boot_stack;
    return address > begin && address <= begin + WIT_A64_BOOT_STACK_SIZE;
}

void wit_arch_initialize(void)
{
    const WitU64 stack_begin = (WitU64)wit_a64_boot_stack;

    /* UEFI hands over at EL1 on QEMU virt; the kernel takes no EL2 or EL3 duties. */
    if (wit_a64_exception_level() != 1) {
        wit_panic("Unsupported exception level");
    }
    if (!on_boot_stack(wit_a64_stack_pointer())) {
        wit_panic("Not running on kernel stack");
    }
    wit_console_write("[TEST-PASS] Cpu.KernelStack\n");
    wit_console_write("Kernel stack: ");
    wit_console_write_hex(stack_begin);
    wit_console_write("-");
    wit_console_write_hex(stack_begin + WIT_A64_BOOT_STACK_SIZE);
    wit_console_write("\n");

    wit_a64_set_vectors(wit_a64_vectors);
    if (wit_a64_vectors_base() != (WitU64)wit_a64_vectors) {
        wit_panic("Exception vectors were not installed");
    }
    wit_console_write("[TEST-PASS] Cpu.ExceptionTables\n");
}

WIT_NORETURN void wit_a64_exception(const WitA64Frame *frame, WitU64 kind)
{
    const WitU64 exception_class = (frame->Esr >> 26) & 0x3F;

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
    wit_console_write(on_boot_stack(frame->Sp) ? " stack=kernel\n" : " stack=unknown\n");
    wit_panic(exception_name(kind, exception_class));
}
