#include "user.h"
#include "witos/platform.h"
#include "protocol.h"
#include "user_arch_tests.h"
#include "user_image.h"
#include "user_thread_image.h"

static WitUserProcess process;
static WitU64 stack_base; /* the one page stack reservation of the kernel-side creations */

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

static void create(WitPageAllocator *pages, WitU64 mode)
{
    WitUserTestConfig *info;
    require(wit_user_create(&process, pages, 0, wit_user_thread_image, sizeof(wit_user_thread_image)),
        "Thread test process creation failed");
    info = (WitUserTestConfig *)wit_user_space_physical(&process.Space, WIT_USER_INFO, 0, 0);
    info->Mode = mode;
    info->ForeignHandle = 0xFFFFFFFF00010001ULL;
}

/* THREAD_CREATE from the kernel side of a test: a request of the version given, placed at the bottom page of the main
 * thread's stack, which every component maps and no running code of the fixture reaches, for a thread on the stack
 * reservation the test made. */
static WitU64 start_thread(WitU32 version, WitU64 entry, WitU64 argument, WitU64 *result)
{
    const WitU64 address = process.Threads[0].StackBottom;
    WitThreadCreateRequest2 request = {version, sizeof(request), entry, argument, stack_base + 4096, 0, 0, 0};
    require(wit_user_copy_to(&process.Space, address, (const WitU8 *)&request, sizeof(request)),
        "Thread request setup failed");
    return wit_user_thread_create(&process, address, sizeof(request), result);
}

static void recovery(WitPageAllocator *pages)
{
    const WitU64 before = wit_pages_free_count(pages);
    WitUserTestConfig *info;
    require(wit_user_create(&process, pages, 0, wit_user_test_image, sizeof(wit_user_test_image)),
        "Thread recovery process creation failed");
    info = (WitUserTestConfig *)wit_user_space_physical(&process.Space, WIT_USER_INFO, 0, 0);
    info->ReadOnlyHandle = wit_handle_grant(&process.Handles, WIT_HANDLE_CONSOLE, 0);
    info->SelfHandle = wit_handle_grant(&process.Handles, WIT_HANDLE_SELF, 0);
    info->ForeignHandle = 0xFFFFFFFF00010001ULL;
    info->InstanceId = process.Id;
    wit_user_run(&process);
    require(process.State == WitUserExited && process.ExitCode == WIT_TEST_EXIT_CODE && process.Writes == 2,
        "Thread failure prevented later component execution");
    wit_user_destroy(&process);
    require(wit_pages_free_count(pages) == before, "Thread recovery leaked");
}

static void check_exit(WitU64 code)
{
    if (process.State != WitUserExited || process.ExitCode != code) {
        wit_console_write("Thread test state/code: ");
        wit_console_write_u64(process.State);
        wit_console_write("/");
        wit_console_write_u64(process.ExitCode);
        wit_console_write("\n");
        wit_panic("User thread test failed");
    }
    require(process.Handles.Count == 0, "Thread process left live handles");
}

/* A refused creation leaves nothing behind (K8.4b: the one form maps nothing, so its rollback is the handles'): version
 * 1 is retired; the thread handle and the identity are both checked before either is granted, with one handle slot
 * left as with none; with the slots back, the creation succeeds and takes no page. */
static void allocation_failure(WitPageAllocator *pages)
{
    WitU64 result, handles[WIT_HANDLE_CAPACITY];
    WitU32 count = 0;
    create(pages, WIT_THREAD_TEST_NORMAL);
    require(wit_user_memory_reserve(&process.Space, 4096, 4096, 0, &stack_base) == WIT_STATUS_OK &&
            wit_user_memory_commit(&process.Space, stack_base, 4096, WIT_MEMORY_READ | WIT_MEMORY_WRITE) ==
                WIT_STATUS_OK,
        "Thread rollback stack setup failed");
    const WitU64 before = wit_pages_free_count(pages);
    const WitU32 owned = process.Space.OwnedCount;
    require(start_thread(1, WIT_USER_CODE, WIT_USER_INFO, &result) == WIT_STATUS_UNSUPPORTED &&
            result == 0 &&
            process.Handles.Count == 2,
        "Version 1 thread creation accepted");
    while (process.Handles.Count < WIT_HANDLE_CAPACITY) {
        handles[count++] = wit_handle_grant(&process.Handles, WIT_HANDLE_SELF, 0);
    }
    require(start_thread(WIT_THREAD_CREATE_VERSION_2, WIT_USER_CODE, 0, &result) == WIT_STATUS_NO_MEMORY &&
            result == 0 &&
            process.Handles.Count == WIT_HANDLE_CAPACITY &&
            process.Threads[1].State == WitThreadEmpty,
        "Handle exhaustion allocated a thread");
    require(wit_handle_close(&process.Handles, handles[--count]) == WIT_STATUS_OK,
        "Thread rollback test handle close failed");
    require(start_thread(WIT_THREAD_CREATE_VERSION_2, WIT_USER_CODE, 0, &result) == WIT_STATUS_NO_MEMORY &&
            result == 0 &&
            process.Handles.Count == WIT_HANDLE_CAPACITY - 1 &&
            process.Threads[1].State == WitThreadEmpty,
        "Identity exhaustion left a thread handle");
    while (count) {
        require(wit_handle_close(&process.Handles, handles[--count]) == WIT_STATUS_OK,
            "Thread rollback test handle close failed");
    }
    require(process.ThreadCreates == 1 && wit_pages_free_count(pages) == before && process.Space.OwnedCount == owned,
        "Refused thread creations changed the component");
    require(start_thread(WIT_THREAD_CREATE_VERSION_2, WIT_USER_CODE, WIT_USER_INFO, &result) == WIT_STATUS_OK &&
            result != 0 &&
            process.ThreadCreates == 2 &&
            process.Threads[1].State == WitThreadReady &&
            process.Threads[1].StackBottom == stack_base &&
            process.Space.OwnedCount == owned &&
            wit_pages_free_count(pages) == before,
        "Thread creation failed after the handles came back");
    wit_user_destroy(&process);
    wit_console_write("[TEST-PASS] User.ThreadCreationRollback\n");
}

void wit_user_thread_self_test(WitPageAllocator *pages)
{
    const WitU64 before = wit_pages_free_count(pages);

    create(pages, WIT_THREAD_TEST_NORMAL);
    wit_user_run(&process);
    check_exit(WIT_TEST_EXIT_CODE);
    require(process.ThreadCreates == 5 &&
            process.ThreadReaps == 4 &&
            process.ThreadTimerSwitches >= 2 &&
            process.ThreadSwitches >= 6 &&
            process.Space.OwnedCount == 8 + (WIT_USER_STACK_TOP - WIT_USER_STACK_BOTTOM) / 4096,
        "Thread progress/accounting failed");
    wit_console_write("User thread timer switches: ");
    wit_console_write_u64(process.ThreadTimerSwitches);
    wit_console_write("\n");
    wit_user_destroy(&process);
    require(wit_pages_free_count(pages) == before, "Thread lifecycle leaked");
    wit_console_write("[TEST-PASS] User.ThreadPreemptionAndTls\n[TEST-PASS] User.ThreadWaitAndReuse\n");

    create(pages, WIT_THREAD_TEST_CAPACITY);
    wit_user_run(&process);
    check_exit(WIT_TEST_EXIT_CODE);
    require(process.ThreadCreates == 4 &&
            process.ThreadReaps == 3 &&
            process.ReferenceThreadCapacityFailures == 1 &&
            process.Space.OwnedCount == 8 + (WIT_USER_STACK_TOP - WIT_USER_STACK_BOTTOM) / 4096,
        "Thread quota/recovery failed");
    wit_user_destroy(&process);
    require(wit_pages_free_count(pages) == before, "Thread capacity test leaked");
    wit_console_write("[TEST-PASS] User.ThreadCapacity\n");

    allocation_failure(pages);
    require(wit_pages_free_count(pages) == before, "Thread OOM test leaked");
    for (WitU32 i = 0; i < wit_test_thread_fault_count; ++i) {
        const WitUserFaultCase *fault = &wit_test_thread_faults[i];
        create(pages, fault->Mode);
        wit_user_run(&process);
        require(process.FaultThread == 1 && wit_test_user_fault_contained(&process, fault, 0),
            "Child fault was not contained");
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Child fault leaked");
        recovery(pages);
        wit_console_write("[TEST-PASS] ");
        wit_console_write(fault->Name);
        wit_console_write("\n");
    }
    create(pages, WIT_THREAD_TEST_BAD_RETURN);
    wit_user_run(&process);
    require(process.State == WitUserBadReturn && process.CurrentThread == 1 && process.Handles.Count == 0,
        "Accepted return on another thread's stack");
    wit_user_destroy(&process);
    require(wit_pages_free_count(pages) == before, "Bad child return leaked");
    recovery(pages);
    wit_console_write("[TEST-PASS] User.ThreadBadReturn\n");

    create(pages, WIT_THREAD_TEST_PROCESS_EXIT);
    wit_user_run(&process);
    check_exit(WIT_TEST_EXIT_CODE);
    require(process.CurrentThread == 1 && process.ThreadCreates == 2, "Child component exit failed");
    wit_user_destroy(&process);
    require(wit_pages_free_count(pages) == before, "Component exit with threads leaked");
    recovery(pages);
    wit_console_write("[TEST-PASS] User.ThreadProcessExit\n");
}
