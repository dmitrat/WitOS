#include "witos/arch.h"
#include "witos/boot.h"
#include "witos/platform.h"
#include "a64.h"

/* ARM64 bring-up: leave the firmware stack and keep interrupts masked except where the scheduler enables them. */

__declspec(align(4096)) WitU8 wit_a64_kernel_stack[WIT_A64_STACK_REGION_SIZE];
__declspec(align(4096)) WitU8 wit_a64_worker_stacks[2][WIT_A64_STACK_REGION_SIZE];
__declspec(align(4096)) WitU8 wit_a64_user_kernel_stacks[2][WIT_USER_THREAD_CAPACITY][WIT_A64_STACK_REGION_SIZE];

static WIT_NORETURN void enter_kernel(const void *boot)
{
    wit_kernel_entry((const WitBootInfo *)boot);
}

void wit_a64_stack_guards(WitU64 guards[WIT_A64_STACK_GUARD_COUNT])
{
    guards[0] = (WitU64)wit_a64_kernel_stack;
    guards[1] = guards[0] + 4096 + WIT_A64_KERNEL_STACK_SIZE;
    for (WitU32 i = 0; i < 2; ++i) {
        guards[2 + i * 2] = (WitU64)wit_a64_worker_stacks[i];
        guards[3 + i * 2] = guards[2 + i * 2] + 4096 + WIT_A64_KERNEL_STACK_SIZE;
        for (WitU32 t = 0; t < WIT_USER_THREAD_CAPACITY; ++t) {
            const WitU32 index = 6 + (i * WIT_USER_THREAD_CAPACITY + t) * 2;
            guards[index] = (WitU64)wit_a64_user_kernel_stacks[i][t];
            guards[index + 1] = guards[index] + 4096 + WIT_A64_KERNEL_STACK_SIZE;
        }
    }
}

WIT_NORETURN void wit_arch_enter(const struct WitBootInfo *boot)
{
    wit_a64_call_on_stack(boot, wit_a64_kernel_stack + 4096 + WIT_A64_KERNEL_STACK_SIZE, enter_kernel);
}

void wit_arch_disable_interrupts(void)
{
    wit_a64_mask_interrupts();
}

int wit_arch_interrupts_enabled(void)
{
    return (wit_a64_interrupt_mask() & (1U << 7)) == 0; /* DAIF.I */
}

WIT_NORETURN void wit_arch_halt(void)
{
    wit_a64_mask_interrupts();
    for (;;) {
        wit_a64_wait();
    }
}
