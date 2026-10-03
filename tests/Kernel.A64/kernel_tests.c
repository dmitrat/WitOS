#include "witos/memory.h"
#include "self_test.h"

/* Runs the ARM64 kernel self-tests of the boot-only profile, in the order the boot markers expect. */
void wit_kernel_self_test(const WitBootInfo *boot, WitPageAllocator *pages)
{
    wit_memory_self_test(boot, pages);
    wit_arch_fault_self_test();
}
