#include "witos/memory.h"
#include "witos/virtual.h"
#include "user.h"
#include "self_test.h"

/* Runs the ARM64 kernel self-tests after the clock is ready, in the order the boot markers expect. */
void wit_kernel_self_test(const WitBootInfo *boot, WitPageAllocator *pages)
{
    WitU64 before;
    wit_memory_self_test(boot, pages);
    wit_virtual_self_test(pages);
    wit_virtual_fault_test();
    wit_arch_fault_self_test();
    wit_arch_scheduler_self_test();

    before = wit_user_isolation_begin_self_test(pages);
    wit_user_thread_self_test(pages);
    wit_user_wait_self_test(pages);
    wit_user_isolation_end_self_test(pages, before);
}
