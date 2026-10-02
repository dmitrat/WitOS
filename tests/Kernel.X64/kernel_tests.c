#include "witos/memory.h"
#include "witos/platform.h"
#include "witos/virtual.h"
#include "self_test.h"

/* Runs the kernel self-tests after the clock is ready, in the order the boot markers expect. */
void wit_kernel_self_test(const WitBootInfo *boot, WitPageAllocator *pages)
{
    wit_memory_self_test(boot, pages);
    wit_virtual_self_test(pages);
    wit_virtual_fault_test();
    wit_arch_fault_self_test();
    wit_arch_scheduler_self_test();
    wit_user_self_test(pages);
}
