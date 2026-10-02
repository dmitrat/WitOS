#include "x64.h"
#include "user.h"
#include "witos/platform.h"

unsigned __int64 __readcr3(void);
void __halt(void);
void _disable(void);
#pragma intrinsic(__readcr3, __halt, _disable)

__declspec(align(16)) static WitU64 gdt[7];
__declspec(align(16)) static WitInterruptGate idt[256];
static WitTaskState task_state;

void wit_arch_initialize(void)
{
    const WitU64 stack_pointer = wit_x64_stack_pointer();
    const WitU64 stack_begin = (WitU64)wit_x64_kernel_stack + 4096;
    const WitU64 tss_base = (WitU64)&task_state;
    const WitU64 tss_limit = sizeof(task_state) - 1;
    WitDescriptorPointer gdt_pointer;
    WitDescriptorPointer idt_pointer;

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
        idt[i].Attributes = (i == 3 || i == 128) ? 0xEE : 0x8E; /* User INT3 trap and syscall gate only. */
        idt[i].OffsetMiddle = (WitU16)(address >> 16);
        idt[i].OffsetHigh = (WitU32)(address >> 32);
        idt[i].Reserved = 0;
    }
    gdt_pointer.Limit = sizeof(gdt) - 1;
    gdt_pointer.Base = (WitU64)gdt;
    idt_pointer.Limit = sizeof(idt) - 1;
    idt_pointer.Base = (WitU64)idt;
    wit_x64_load_tables(&gdt_pointer, &idt_pointer);
    wit_console_write("[TEST-PASS] Cpu.ExceptionTables\n");
}

void wit_x64_set_kernel_stack(WitU64 top)
{
    task_state.Rsp[0] = top;
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

void wit_arch_fault_self_test(void)
{
#if defined(WITOS_TEST_PAGE_FAULT) || defined(WITOS_TEST_DOUBLE_FAULT)
    /* The kernel's own page tables must leave the fault probe absent. */
    const WitU64 *pml4 = (const WitU64 *)(__readcr3() & 0x000FFFFFFFFFF000ULL);
    if ((pml4[(WIT_PAGE_FAULT_PROBE >> 39) & 511] & 1) != 0) {
        wit_panic("Page fault probe is mapped");
    }
#endif
#if defined(WITOS_TEST_BREAKPOINT)
    wit_console_write("[TEST-BEGIN] Cpu.Breakpoint\n");
    wit_x64_trigger_breakpoint();
#elif defined(WITOS_TEST_DIVIDE_ERROR)
    wit_console_write("[TEST-BEGIN] Cpu.DivideError\n");
    wit_x64_trigger_divide_error();
#elif defined(WITOS_TEST_INVALID_OPCODE)
    wit_console_write("[TEST-BEGIN] Cpu.InvalidOpcode\n");
    wit_x64_trigger_invalid_opcode();
#elif defined(WITOS_TEST_GENERAL_PROTECTION)
    wit_console_write("[TEST-BEGIN] Cpu.GeneralProtection\n");
    wit_x64_trigger_general_protection();
#elif defined(WITOS_TEST_PAGE_FAULT)
    wit_console_write("[TEST-BEGIN] Cpu.PageFault\n");
    wit_x64_trigger_page_fault();
#elif defined(WITOS_TEST_DOUBLE_FAULT)
    wit_console_write("[TEST-BEGIN] Cpu.DoubleFault\n");
    wit_x64_trigger_double_fault();
#endif
#if defined(WITOS_TEST_BREAKPOINT) || \
    defined(WITOS_TEST_DIVIDE_ERROR) || \
    defined(WITOS_TEST_INVALID_OPCODE) || \
    defined(WITOS_TEST_GENERAL_PROTECTION) || \
    defined(WITOS_TEST_PAGE_FAULT) || \
    defined(WITOS_TEST_DOUBLE_FAULT)
    wit_panic("Fault injection unexpectedly returned");
#endif
}

void wit_arch_disable_interrupts(void)
{
    _disable();
}

WIT_NORETURN void wit_arch_halt(void)
{
    _disable();
    for (;;) {
        __halt();
    }
}
