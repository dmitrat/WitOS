#include "witos/arch.h"
#include "witos/boot.h"
#include "witos/platform.h"
#include "a64.h"

/* ARM64 bring-up: leave the firmware stack and keep interrupts masked until the timer arrives with A1.3. */

__declspec(align(16)) WitU8 wit_a64_boot_stack[WIT_A64_BOOT_STACK_SIZE];

static WIT_NORETURN void enter_kernel(const void *boot)
{
    wit_kernel_entry((const WitBootInfo *)boot);
}

WIT_NORETURN void wit_arch_enter(const struct WitBootInfo *boot)
{
    wit_a64_call_on_stack(boot, wit_a64_boot_stack + WIT_A64_BOOT_STACK_SIZE, enter_kernel);
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
