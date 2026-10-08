#include "witos/arch.h"
#include "witos/boot.h"
#include "witos/cpu.h"
#include "witos/platform.h"
#include "witos/processor.h"
#include "a64.h"

/* Secondary processors of ARM64 (plan step K7.2). The boot processor records its translation registers, SCTLR and
 * CPACR for the entry in secondary_entry.asm, gives each processor a kernel stack, lets the platform map that processor's
 * interrupt controller frames, and starts it through PSCI CPU_ON with the kernel's number as the context. A started
 * processor enables its interrupts through the platform, reports itself ready and idles in WFI; the shared vector
 * table brings its inter-processor interrupts to wit_a64_interrupt. */

WitU64 wit_a64_boot_mair, wit_a64_boot_tcr, wit_a64_boot_ttbr0, wit_a64_boot_ttbr1, wit_a64_boot_sctlr,
    wit_a64_boot_cpacr;
WitU64 wit_a64_secondary_stack_tops[WIT_PROCESSOR_CAPACITY];

__declspec(align(4096)) static WitU8 stacks[WIT_PROCESSOR_CAPACITY][WIT_A64_KERNEL_STACK_SIZE];

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

void wit_arch_secondary_prepare(const WitBootInfo *boot, WitU32 index, WitU64 hardware_id)
{
    require(index != 0 && index < WIT_PROCESSOR_CAPACITY, "Secondary processor index out of range");
    if (!wit_a64_boot_sctlr) {
        wit_a64_boot_mair = wit_a64_memory_attributes();
        wit_a64_boot_tcr = wit_a64_translation_control();
        wit_a64_boot_ttbr0 = wit_a64_translation_base();
        wit_a64_boot_ttbr1 = wit_a64_translation_base_high();
        wit_a64_boot_sctlr = wit_a64_system_control();
        wit_a64_boot_cpacr = wit_a64_fp_access(); /* FP/SIMD without traps: the vectors save the q registers */
    }
    wit_a64_secondary_stack_tops[index] = (WitU64)stacks[index] + WIT_A64_KERNEL_STACK_SIZE;
    wit_platform_processor_prepare(boot, index, hardware_id);
    /* The entry reads these with the MMU and caches off: they must be in memory, not in this processor's cache. */
    wit_a64_clean_data((WitU64)&wit_a64_boot_mair, sizeof(wit_a64_boot_mair));
    wit_a64_clean_data((WitU64)&wit_a64_boot_tcr, sizeof(wit_a64_boot_tcr));
    wit_a64_clean_data((WitU64)&wit_a64_boot_ttbr0, sizeof(wit_a64_boot_ttbr0));
    wit_a64_clean_data((WitU64)&wit_a64_boot_ttbr1, sizeof(wit_a64_boot_ttbr1));
    wit_a64_clean_data((WitU64)&wit_a64_boot_sctlr, sizeof(wit_a64_boot_sctlr));
    wit_a64_clean_data((WitU64)&wit_a64_boot_cpacr, sizeof(wit_a64_boot_cpacr));
    wit_a64_clean_data((WitU64)wit_a64_secondary_stack_tops, sizeof(wit_a64_secondary_stack_tops));
}

int wit_arch_secondary_start(WitU32 index, WitU64 hardware_id)
{
    return wit_a64_psci_cpu_on(hardware_id, (WitU64)wit_a64_secondary_entry, index) == 0;
}

void wit_arch_ipi(WitU64 hardware_id, WitU32 kind)
{
    wit_platform_ipi(hardware_id, kind);
}

void wit_arch_invalidate_local(WitU64 address)
{
    wit_a64_invalidate_page(address);
}

/* A started processor, on its own stack with translation on: its interrupt controller frames awake and its SGIs
 * enabled, it reports itself ready and waits for interrupts. */
WIT_NORETURN void wit_a64_secondary_main(WitU32 index)
{
    require(wit_a64_system_control() == wit_a64_boot_sctlr && wit_a64_fp_access() == wit_a64_boot_cpacr,
        "Secondary processor control registers differ from the boot processor's");
    wit_platform_processor_interrupts_enable(index, wit_arch_processor_id());
    wit_processors_set_features(index, wit_arch_processor_features(), wit_arch_cache_size());
    wit_cpus_secondary_ready(index);
    wit_a64_enable_interrupts();
    for (;;) {
        wit_a64_wait();
    }
}
