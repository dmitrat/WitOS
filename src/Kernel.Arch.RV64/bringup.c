#include "witos/arch.h"
#include "witos/boot.h"
#include "witos/platform.h"
#include "witos/rv64_csr.h"
#include "rv64.h"

/* RISC-V bring-up (plan step V1.2): leave the firmware's stack and translation and enter the common kernel with
 * interrupts disabled. */

__attribute__((aligned(4096))) WitU8 wit_rv64_kernel_stack[WIT_RV64_STACK_REGION_SIZE];
__attribute__((aligned(4096))) WitU8 wit_rv64_worker_stacks[2][WIT_RV64_STACK_REGION_SIZE];
__attribute__((aligned(4096))) WitU8
    wit_rv64_user_kernel_stacks[WIT_PROCESS_CAPACITY][WIT_PROCESS_THREAD_CAPACITY][WIT_RV64_STACK_REGION_SIZE];

static WIT_NORETURN void enter_kernel(const void *boot)
{
    wit_kernel_entry((const WitBootInfo *)boot);
}

void wit_rv64_stack_guards(WitU64 guards[WIT_RV64_STACK_GUARD_COUNT])
{
    guards[0] = (WitU64)wit_rv64_kernel_stack;
    guards[1] = guards[0] + 4096 + WIT_RV64_KERNEL_STACK_SIZE;
    for (WitU32 i = 0; i < 2; ++i) {
        guards[2 + i * 2] = (WitU64)wit_rv64_worker_stacks[i];
        guards[3 + i * 2] = guards[2 + i * 2] + 4096 + WIT_RV64_KERNEL_STACK_SIZE;
    }
    for (WitU32 i = 0; i < WIT_PROCESS_CAPACITY; ++i) {
        for (WitU32 t = 0; t < WIT_PROCESS_THREAD_CAPACITY; ++t) {
            const WitU32 index = 6 + (i * WIT_PROCESS_THREAD_CAPACITY + t) * 2;
            guards[index] = (WitU64)wit_rv64_user_kernel_stacks[i][t];
            guards[index + 1] = guards[index] + 4096 + WIT_RV64_KERNEL_STACK_SIZE;
        }
    }
}

WIT_NORETURN void wit_arch_enter(const struct WitBootInfo *boot)
{
    /* The firmware's translation (an identity mapping of the memory and the devices it drove) stays active after
     * ExitBootServices and maps nothing else — not the board's test finisher, whose write took a page fault under it
     * (V1.1). The bring-up leaves it for the bare physical space, which that identity mapping equals, until
     * wit_virtual_initialize installs the kernel's own tables. */
    wit_rv64_write_satp(0);
    wit_rv64_fence_vma();
    wit_rv64_call_on_stack(boot, wit_rv64_kernel_stack + 4096 + WIT_RV64_KERNEL_STACK_SIZE, enter_kernel);
}

void wit_arch_disable_interrupts(void)
{
    wit_rv64_clear_sstatus(WIT_RV64_SSTATUS_SIE);
}

int wit_arch_interrupts_enabled(void)
{
    return (wit_rv64_read_sstatus() & WIT_RV64_SSTATUS_SIE) != 0;
}

WIT_NORETURN void wit_arch_halt(void)
{
    wit_arch_disable_interrupts();
    for (;;) {
        wit_rv64_wait_for_interrupt();
    }
}
