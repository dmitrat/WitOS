#include "x64.h"
#include "user.h"
#include "witos/platform.h"
#include "witos/x64_instructions.h"

/* The task state of the boot processor sits at selector 0x18 (entries 3 and 4), before the user selectors SYSRET
 * requires at 0x2B and 0x33; each other processor's follows them, two entries apiece from entry 7 (P1.1). */
#define TSS_BOOT_SELECTOR 0x18U
#define TSS_FIRST_SECONDARY 7U
#define GDT_ENTRIES (TSS_FIRST_SECONDARY + 2U * (WIT_PROCESSOR_CAPACITY - 1U))
WIT_STATIC_ASSERT(WIT_PROCESSOR_CAPACITY == 8U, "user_entry.S has a SYSCALL entry and its two words for 8 processors");

__declspec(align(16)) static WitU64 gdt[GDT_ENTRIES];
__declspec(align(16)) static WitInterruptGate idt[256];
static WitTaskState task_states[WIT_PROCESSOR_CAPACITY]; /* one a processor (P1.1) */
static WitDescriptorPointer gdt_pointer; /* loaded by every processor (K7.2) */
static WitDescriptorPointer idt_pointer;

static WitU16 task_selector(WitU32 index)
{
    return (WitU16)(index == 0 ? TSS_BOOT_SELECTOR : (TSS_FIRST_SECONDARY + 2U * (index - 1U)) * 8U);
}

/* The two entries of a 64-bit available task state descriptor. */
static void describe_task(WitU32 entry, const WitTaskState *task)
{
    const WitU64 base = (WitU64)task;
    const WitU64 limit = sizeof(*task) - 1;
    gdt[entry] = (limit & 0xFFFFULL) |
        ((base & 0xFFFFFFULL) << 16) |
        (0x89ULL << 40) |
        (((limit >> 16) & 0xFULL) << 48) |
        (((base >> 24) & 0xFFULL) << 56);
    gdt[entry + 1] = base >> 32;
}

void wit_arch_initialize(void)
{
    const WitU64 stack_pointer = wit_x64_stack_pointer();
    const WitU64 stack_begin = (WitU64)wit_x64_kernel_stack + 4096;

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
    for (WitU32 i = 0; i < WIT_PROCESSOR_CAPACITY; ++i) {
        task_states[i].Ist[0] = (WitU64)wit_x64_double_fault_stack + 4096 + WIT_EMERGENCY_STACK_SIZE;
        task_states[i].IoMapBase = sizeof(task_states[i]);
        describe_task(task_selector(i) / 8U, &task_states[i]);
    }
    task_states[0].Rsp[0] = stack_begin + WIT_KERNEL_STACK_SIZE;
    wit_x64_syscall_kernel_rsp[0] = task_states[0].Rsp[0];
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
    wit_x64_enable_syscall(0);
    wit_console_write("[TEST-PASS] Cpu.ExceptionTables\n");
}

/* The kernel's tables for a secondary processor's entry; it loads its own task register as it records its number. */
void wit_x64_tables(const WitDescriptorPointer **gdt_out, const WitDescriptorPointer **idt_out)
{
    *gdt_out = &gdt_pointer;
    *idt_out = &idt_pointer;
}

/* A processor's number is its task register's selector (P1.1): the boot processor loaded 0x18 with the tables, and a
 * secondary loads its own descriptor here, once, as it starts. */
void wit_arch_processor_set_index(WitU32 index)
{
    if (index >= WIT_PROCESSOR_CAPACITY) {
        wit_panic("Processor number outside the table");
    }
    if (wit_x64_store_task_register() != task_selector(index)) {
        wit_x64_load_task_register(task_selector(index));
    }
}

WitU32 wit_arch_processor_index(void)
{
    const WitU16 selector = wit_x64_store_task_register();
    return selector <= TSS_BOOT_SELECTOR ? 0U : 1U + (selector - TSS_FIRST_SECONDARY * 8U) / 16U;
}

void wit_x64_set_kernel_stack(WitU64 top)
{
    const WitU32 index = wit_arch_processor_index();
    task_states[index].Rsp[0] = top;
    wit_x64_syscall_kernel_rsp[index] = top; /* SYSCALL switches no stack; the processor's entry loads this (K5.2a). */
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
