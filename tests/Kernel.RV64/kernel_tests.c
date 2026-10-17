#include "witos/memory.h"
#include "witos/virtual.h"
#include "user.h"
#include "self_test.h"

/* Runs the RISC-V kernel self-tests after the clock is ready, in the order the boot markers expect (plan steps V1.2
 * and V1.3): the foundation, then the user-mode suites shared with the other ISAs, and the summary. */
void wit_kernel_self_test(const WitBootInfo *boot, WitPageAllocator *pages)
{
    WitU64 before;
    wit_clock_self_test(); /* The clock markers precede the memory markers in the foundation order. */
    wit_processor_self_test();
    wit_smp_self_test();
    wit_memory_self_test(boot, pages);
    wit_virtual_self_test(pages);
    wit_virtual_fault_test();
    wit_arch_fault_self_test();
    wit_arch_scheduler_self_test();

    before = wit_user_isolation_begin_self_test(pages);
    wit_user_thread_self_test(pages);
    wit_user_wait_self_test(pages);
    wit_user_exception_self_test(pages);
    wit_user_channel_self_test(pages);
    wit_user_memory_object_self_test(pages);
    wit_user_device_self_test(pages);
    wit_user_interrupt_self_test(pages);
    wit_user_thread2_self_test(pages);
    wit_user_process_self_test(pages);
    wit_user_processor_self_test(pages);
    wit_root_task_self_test(boot, pages);
    wit_user_isolation_end_self_test(pages, before);
    wit_test_summary();
}
