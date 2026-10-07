#include "user.h"
#include "witos/platform.h"
#include "protocol.h"
#include "self_test.h"
#include "user_arch_tests.h"
#include "user_exception_image.h"

/* The fault callback and the thread contexts of ABI-1 (RFC 0011 sections 7.3 and 7.5) from user mode on both ISAs.
 * The exception fixture registers a callback, takes a read fault at address zero, inspects the record the callback
 * receives, continues a changed context, activates its own thread through the same callback, queries CONTEXT_PROFILE
 * and its own context; its second mode rejects the fault, which the kernel then reports as the original fault. */

static WitUserProcess process;

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

static void create(WitPageAllocator *pages, WitU64 mode)
{
    WitUserTestConfig *info;
    require(wit_user_create(&process, pages, 0, wit_user_exception_image, sizeof(wit_user_exception_image)),
        "Exception test process creation failed");
    info = (WitUserTestConfig *)wit_user_space_physical(&process.Space, WIT_USER_INFO, 0, 0);
    info->Mode = mode;
}

void wit_user_exception_self_test(WitPageAllocator *pages)
{
    const WitU64 before = wit_pages_free_count(pages);

    create(pages, WIT_EXCEPTION_TEST_NORMAL);
    wit_user_run(&process);
    if (process.State != WitUserExited || process.ExitCode != WIT_TEST_EXIT_CODE) {
        wit_console_write("Exception test state/code: ");
        wit_console_write_u64(process.State);
        wit_console_write("/");
        wit_console_write_u64(process.ExitCode);
        wit_console_write("\n");
        wit_panic("User exception test failed");
    }
    /* One delivered read fault and one activation, each continued once; nothing of either survives the component. */
    require(process.Handles.Count == 0 &&
            process.HardwareNullReads == 1 &&
            process.ExceptionContinuations == 2 &&
            process.ActivationDeliveries == 1 &&
            !process.WaitInterruptions &&
            !process.ExceptionCallback,
        "Exception callback accounting failed");
    for (WitU32 i = 0; i < WIT_USER_THREAD_CAPACITY; ++i) {
        require(!process.Threads[i].Exception.Token && !process.Threads[i].ActivationCount,
            "Delivery state survived component completion");
    }
    wit_user_destroy(&process);
    require(wit_pages_free_count(pages) == before, "Exception callback test leaked");
    wit_console_write("[TEST-PASS] User.ExceptionCallbackAndContext\n");

    create(pages, WIT_EXCEPTION_TEST_REJECT);
    wit_user_run(&process);
    require(wit_test_user_fault_contained(&process, &wit_test_exception_fault, 1) &&
            process.HardwareNullReads == 1 &&
            !process.ExceptionContinuations,
        "Rejected fault was not reported as the original fault");
    wit_user_destroy(&process);
    require(wit_pages_free_count(pages) == before, "Exception reject test leaked");
    wit_console_write("[TEST-PASS] User.ExceptionReject\n");
}
