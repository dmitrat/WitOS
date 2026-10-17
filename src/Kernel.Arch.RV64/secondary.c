#include "witos/arch.h"
#include "witos/boot.h"
#include "witos/cpu.h"
#include "witos/platform.h"
#include "witos/processor.h"
#include "witos/rv64_csr.h"
#include "witos/rv64_sbi.h"
#include "rv64.h"

/* Secondary harts of RISC-V (plan step V1.4, K7.2's role). The boot hart records its translation root for the entry in
 * entry.S, gives each hart a kernel stack, lets the board map that hart's interrupt controller context, and starts it
 * through SBI HSM hart_start with the kernel's number as the opaque word. A started hart enters in S-mode with the
 * bare physical space, takes its stack, installs the trap vector and the boot hart's root, enables the software
 * interrupt alone, reports itself ready and idles in WFI; the inter-processor interrupts reach it as the supervisor
 * software interrupt, which the firmware raises for SBI send_ipi. S-mode reads no mhartid, so a running secondary knows
 * its hart by the stack it runs on, which it never leaves until phase P. */

WitU64 wit_rv64_boot_satp;
WitU64 wit_rv64_secondary_stack_tops[WIT_PROCESSOR_CAPACITY];

__attribute__((aligned(4096))) static WitU8 stacks[WIT_PROCESSOR_CAPACITY][WIT_RV64_KERNEL_STACK_SIZE];
static WitU64 hart_ids[WIT_PROCESSOR_CAPACITY];
static volatile WitU32 started[WIT_PROCESSOR_CAPACITY];

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

void wit_arch_secondary_prepare(const WitBootInfo *boot, WitU32 index, WitU64 hardware_id)
{
    require(index != 0 && index < WIT_PROCESSOR_CAPACITY, "Secondary processor index out of range");
    if (!wit_rv64_boot_satp) {
        wit_rv64_boot_satp = wit_rv64_read_satp();
        require(wit_rv64_boot_satp != 0, "The kernel's translation root is not installed");
    }
    hart_ids[index] = hardware_id;
    wit_rv64_secondary_stack_tops[index] = (WitU64)stacks[index] + WIT_RV64_KERNEL_STACK_SIZE;
    wit_platform_processor_prepare(boot, index, hardware_id);
    /* The entry reads the root and its stack top from memory before the start: the harts are coherent, and the fence
     * orders the writes before the firmware call. */
    wit_rv64_fence();
}

/* hart_start(hart, address, opaque): the hart enters the address in S-mode with a0 the hart and a1 the opaque word,
 * satp bare and interrupts disabled; the kernel is identity mapped, so the entry's address is its physical one too. */
int wit_arch_secondary_start(WitU32 index, WitU64 hardware_id)
{
    return wit_rv64_sbi(
               WIT_RV64_SBI_HSM, WIT_RV64_SBI_HSM_START, hardware_id, (WitU64)wit_rv64_secondary_entry, index, 0) == 0;
}

void wit_arch_ipi(WitU64 hardware_id, WitU32 kind)
{
    wit_platform_ipi(hardware_id, kind);
}

/* The hart of the running secondary processor, by the kernel stack it runs on; false on the boot hart. */
int wit_rv64_secondary_hart(WitU64 *hart)
{
    const WitU64 sp = wit_rv64_stack_pointer();
    for (WitU32 i = 1; i < WIT_PROCESSOR_CAPACITY; ++i) {
        if (started[i] && sp >= (WitU64)stacks[i] && sp < (WitU64)stacks[i] + WIT_RV64_KERNEL_STACK_SIZE) {
            *hart = hart_ids[i];
            return 1;
        }
    }
    return 0;
}

/* A started hart, on its own stack with the kernel's translation on: its interrupt controller context masked, the
 * software interrupt enabled, it reports itself ready and waits for interrupts. */
WIT_NORETURN void wit_rv64_secondary_main(WitU32 index, WitU64 hart)
{
    require(index != 0 && index < WIT_PROCESSOR_CAPACITY && hart_ids[index] == hart,
        "A started hart is not the one its number names");
    started[index] = 1;
    wit_rv64_fence();
    wit_platform_processor_interrupts_enable(index, hart);
    /* The inter-processor interrupt alone: no tick and no device line reaches a secondary hart until phase P. */
    wit_rv64_set_sie(WIT_RV64_SIE_SOFTWARE);
    wit_processors_set_features(index, wit_arch_processor_features(), wit_arch_cache_size());
    wit_cpus_secondary_ready(index);
    wit_rv64_set_sstatus(WIT_RV64_SSTATUS_SIE);
    for (;;) {
        wit_rv64_wait_for_interrupt();
    }
}
