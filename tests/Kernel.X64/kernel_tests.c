#include "witos/memory.h"
#include "witos/platform.h"
#include "witos/virtual.h"
#include "user.h"
#include "self_test.h"

/* Runs the kernel self-tests after the clock is ready, in the order the boot markers expect. */
void wit_kernel_self_test(const WitBootInfo *boot, WitPageAllocator *pages)
{
    WitU64 before;
    wit_clock_self_test(); /* The clock markers precede the memory markers in the foundation order. */
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
    wit_root_task_self_test(boot, pages);
    wit_user_image_self_test(pages);
    wit_user_bootstrap_self_test(pages);
    wit_user_gc_self_test(pages);
    wit_user_tls_self_test(pages);
    wit_user_dynamic_tls_self_test(pages);
    wit_user_pal_self_test(pages);
    wit_user_pal_services_self_test(pages);
    wit_user_wait_any_self_test(pages);
    wit_user_pressure_self_test(pages);
    wit_user_pal_module_self_test(pages);
    wit_user_pal_environment_self_test(pages);
    wit_user_process_exit_self_test(pages);
    wit_user_pal_background_self_test(pages);
    wit_user_pal_error_self_test(pages);
#if defined(WITOS_TEST_RUNTIME_CONFIG)
    wit_user_runtime_config_self_test(pages);
#endif
    wit_user_isolation_end_self_test(pages, before);
    wit_user_runtime_boot_test(pages);
    wit_user_code_self_test(pages);
    wit_user_file_self_test(pages);
    wit_test_summary();
}
