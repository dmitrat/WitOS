#include "witos/memory.h"
#include "witos/virtual.h"
#include "self_test.h"

/* Runs the RISC-V kernel self-tests after the clock is ready, in the order the boot markers expect: the foundation
 * alone until user mode exists (plan step V1.3), when the isolation suites and the summary join as on ARM64. */
void wit_kernel_self_test(const WitBootInfo *boot, WitPageAllocator *pages)
{
    wit_clock_self_test(); /* The clock markers precede the memory markers in the foundation order. */
    wit_processor_self_test();
    wit_smp_self_test();
    wit_memory_self_test(boot, pages);
    wit_virtual_self_test(pages);
    wit_virtual_fault_test();
    wit_arch_fault_self_test();
    wit_arch_scheduler_self_test();
}
