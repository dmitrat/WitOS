#include "witos/arch.h"
#include "witos/boot.h"
#include "witos/cpu.h"
#include "witos/platform.h"
#include "witos/processor.h"
#include "witos/rv64_csr.h"
#include "witos/rv64_sbi.h"
#include "user.h"
#include "rv64.h"

/* Secondary harts of RISC-V (plan step V1.4, K7.2's role). The boot hart records its translation root for the entry in
 * entry.S, gives each hart a kernel stack, lets the board map that hart's interrupt controller context, and starts it
 * through SBI HSM hart_start with the kernel's number as the opaque word. A started hart enters in S-mode with the
 * bare physical space, takes its stack, installs the trap vector and the boot hart's root, enables the software
 * interrupt alone, reports itself ready and idles in WFI; the inter-processor interrupts reach it as the supervisor
 * software interrupt, which the firmware raises for SBI send_ipi. S-mode reads no mhartid, so a started hart records
 * the hart hart_start gave its entry in the record tp names (processor.c), which every stack it runs on reaches. */

WitU64 wit_rv64_boot_satp;
WitU64 wit_rv64_secondary_stack_tops[WIT_PROCESSOR_CAPACITY];

__attribute__((aligned(4096))) static WitU8 stacks[WIT_PROCESSOR_CAPACITY][WIT_RV64_KERNEL_STACK_SIZE];

/* A processor's idle stack (P1.1): a secondary's start stack, which it abandons once it dispatches, and for the boot
 * processor entry 0, which no start uses. */
void wit_arch_idle_stack(WitU32 index, WitU64 *low, WitU64 *high)
{
    if (index >= WIT_PROCESSOR_CAPACITY) {
        wit_panic("Idle stack of a processor outside the table");
    }
    *low = (WitU64)stacks[index];
    *high = *low + WIT_RV64_KERNEL_STACK_SIZE;
}

static WitU64 hart_ids[WIT_PROCESSOR_CAPACITY];

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

/* A started hart, on its own stack with the kernel's translation on: tp names its record (P1.1), its interrupt
 * controller context masked, the software interrupt enabled, it reports itself ready and waits for interrupts. */
WIT_NORETURN void wit_rv64_secondary_main(WitU32 index, WitU64 hart)
{
    require(index != 0 && index < WIT_PROCESSOR_CAPACITY && hart_ids[index] == hart,
        "A started hart is not the one its number names");
    wit_arch_processor_set_index(index);
    wit_rv64_hart_identify(hart); /* before anything asks: the record names the hart on every stack (2026-10-22) */
    wit_rv64_fence();
    wit_platform_processor_interrupts_enable(index, hart);
    /* The inter-processor interrupt; its tick during a run (P1.3a); never a device line, which reaches the boot hart. */
    wit_rv64_set_sie(WIT_RV64_SIE_SOFTWARE);
    wit_processors_set_features(index, wit_arch_processor_features(), wit_arch_cache_size());
    wit_cpus_secondary_ready(index);
    wit_user_processor_main(); /* threads during a run, parked between runs (P1.3b) */
}
