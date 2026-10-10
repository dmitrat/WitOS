#include "x64.h"
#include "user.h"
#include "witos/platform.h"
#include "witos/x64_instructions.h"

__declspec(align(16)) static WitU64 gdt[7];
__declspec(align(16)) static WitInterruptGate idt[256];
static WitTaskState task_state;
static WitDescriptorPointer gdt_pointer; /* loaded by every processor (K7.2) */
static WitDescriptorPointer idt_pointer;

void wit_arch_initialize(void)
{
    const WitU64 stack_pointer = wit_x64_stack_pointer();
    const WitU64 stack_begin = (WitU64)wit_x64_kernel_stack + 4096;
    const WitU64 tss_base = (WitU64)&task_state;
    const WitU64 tss_limit = sizeof(task_state) - 1;

    if (stack_pointer < stack_begin || stack_pointer >= stack_begin + WIT_KERNEL_STACK_SIZE) {
        wit_panic("Not running on kernel stack");
    }
    wit_console_write("[TEST-PASS] Cpu.KernelStack\n");
    wit_console_write("Kernel stack: ");
    wit_console_write_hex(stack_begin);
    wit_console_write("-");
    wit_console_write_hex(stack_begin + WIT_KERNEL_STACK_SIZE);
    wit_console_write("\n");

    gdt[0] = 0;
    gdt[1] = 0x00AF9A000000FFFFULL; /* ring 0, present, long-mode code */
    gdt[2] = 0x00CF92000000FFFFULL; /* ring 0 data */
    task_state.Rsp[0] = stack_begin + WIT_KERNEL_STACK_SIZE;
    wit_x64_syscall_kernel_rsp = task_state.Rsp[0];
    task_state.Ist[0] = (WitU64)wit_x64_double_fault_stack + 4096 + WIT_EMERGENCY_STACK_SIZE;
    task_state.IoMapBase = sizeof(task_state);
    gdt[3] = (tss_limit & 0xFFFFULL) |
        ((tss_base & 0xFFFFFFULL) << 16) |
        (0x89ULL << 40) |
        (((tss_limit >> 16) & 0xFULL) << 48) |
        (((tss_base >> 24) & 0xFFULL) << 56);
    gdt[4] = tss_base >> 32;
    gdt[5] = 0x00CFF2000000FFFFULL; /* ring-3 data, selector 0x2B */
    gdt[6] = 0x00AFFA000000FFFFULL; /* ring-3 code, selector 0x33 */

    for (WitU32 i = 0; i < 256; ++i) {
        const WitU64 address = wit_x64_isr_table[i];
        idt[i].OffsetLow = (WitU16)address;
        idt[i].Selector = 8;
        idt[i].Ist = i == 8 ? 1 : 0;
        idt[i].Attributes = i == 3 ? 0xEE : 0x8E; /* User INT3 alone; calls enter through SYSCALL (K8.4b). */
        idt[i].OffsetMiddle = (WitU16)(address >> 16);
        idt[i].OffsetHigh = (WitU32)(address >> 32);
        idt[i].Reserved = 0;
    }
    gdt_pointer.Limit = sizeof(gdt) - 1;
    gdt_pointer.Base = (WitU64)gdt;
    idt_pointer.Limit = sizeof(idt) - 1;
    idt_pointer.Base = (WitU64)idt;
    wit_x64_load_tables(&gdt_pointer, &idt_pointer);
    wit_x64_enable_syscall();
    wit_console_write("[TEST-PASS] Cpu.ExceptionTables\n");
}

/* The kernel's tables for a secondary processor's entry: it loads them without a task register (K7.2). */
void wit_x64_tables(const WitDescriptorPointer **gdt_out, const WitDescriptorPointer **idt_out)
{
    *gdt_out = &gdt_pointer;
    *idt_out = &idt_pointer;
}

void wit_x64_set_kernel_stack(WitU64 top)
{
    task_state.Rsp[0] = top;
    wit_x64_syscall_kernel_rsp = top; /* SYSCALL switches no stack; the entry loads this one (K5.2a). */
}

static const char *exception_name(WitU64 vector)
{
    switch (vector) {
    case 0:
        return "Divide error";
    case 3:
        return "Breakpoint";
    case 6:
        return "Invalid opcode";
    case 8:
        return "Double fault";
    case 13:
        return "General protection";
    case 14:
        return "Page fault";
    default:
        return "Unhandled exception or interrupt";
    }
}

WIT_NORETURN void wit_x64_exception(const WitExceptionFrame *frame, WitU64 fault_address)
{
    const WitU64 stack_pointer = wit_x64_stack_pointer();
    const WitU64 emergency_begin = (WitU64)wit_x64_double_fault_stack + 4096;
    const int on_emergency_stack =
        stack_pointer >= emergency_begin && stack_pointer < emergency_begin + WIT_EMERGENCY_STACK_SIZE;

    if ((frame->Cs & 3) == 3) {
        wit_x64_user_fault(frame, fault_address);
    }

    wit_console_write("[EXCEPTION] vector=");
    wit_console_write_u64(frame->Vector);
    wit_console_write(" error=");
    wit_console_write_hex(frame->Error);
    wit_console_write(" rip=");
    wit_console_write_hex(frame->Rip);
    wit_console_write(" cs=");
    wit_console_write_hex(frame->Cs);
    wit_console_write(" rflags=");
    wit_console_write_hex(frame->Rflags);
    wit_console_write(" rsp=");
    wit_console_write_hex(frame->Rsp);
    wit_console_write(" ss=");
    wit_console_write_hex(frame->Ss);
    wit_console_write(" cr2=");
    wit_console_write_hex(fault_address);
    wit_console_write(on_emergency_stack ? " stack=emergency\n" : " stack=kernel\n");
    wit_panic(exception_name(frame->Vector));
}

void wit_arch_disable_interrupts(void)
{
    wit_x64_disable_interrupts();
}

WIT_NORETURN void wit_arch_halt(void)
{
    wit_x64_disable_interrupts();
    for (;;) {
        wit_x64_halt();
    }
}
