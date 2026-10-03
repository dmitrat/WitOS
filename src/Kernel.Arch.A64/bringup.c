#include "witos/arch.h"
#include "witos/boot.h"
#include "witos/platform.h"
#include "a64.h"

/* ARM64 bring-up for the boot-only kernel: leave the firmware stack, check the exception level and keep
 * interrupts masked. Vectors, paging and the timer arrive with A1. */

#define BOOT_STACK_SIZE (64U * 1024U)

__declspec(align(16)) static WitU8 boot_stack[BOOT_STACK_SIZE];

static WIT_NORETURN void enter_kernel(const void *boot)
{
    wit_kernel_entry((const WitBootInfo *)boot);
}

WIT_NORETURN void wit_arch_enter(const struct WitBootInfo *boot)
{
    wit_a64_call_on_stack(boot, boot_stack + BOOT_STACK_SIZE, enter_kernel);
}

void wit_arch_initialize(void)
{
    /* UEFI hands over at EL1 on QEMU virt; the kernel takes no EL2 or EL3 duties. */
    if (wit_a64_exception_level() != 1) {
        wit_panic("Unsupported exception level");
    }
}

void wit_arch_disable_interrupts(void)
{
    wit_a64_mask_interrupts();
}

WIT_NORETURN void wit_arch_halt(void)
{
    wit_a64_mask_interrupts();
    for (;;) {
        wit_a64_wait();
    }
}
