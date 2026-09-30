#include "user.h"
#include "witos/platform.h"
#include "protocol.h"
#include "user_image.h"
#include "user_thread_image.h"

static WitUserProcess process;

static void require(int condition, const char *message)
{
    if (!condition) wit_panic(message);
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

static void allocation_failure(WitPageAllocator *pages)
{
    WitU64 base, result, handles[WIT_HANDLE_CAPACITY];
    WitU32 count = 0;
    WitU64 before;
    create(pages, WIT_THREAD_TEST_NORMAL);
    const WitU32 baseline=process.Space.OwnedCount;
    const WitU32 stackPages=(WIT_USER_STACK_TOP-WIT_USER_STACK_BOTTOM)/4096;
    /* Fail every stack-page allocation and the raw TLS allocation. Three
     * private paging levels back the otherwise empty dynamic arena. */
    for (WitU32 remaining = 0; remaining < stackPages+1; ++remaining) {
        const WitU64 size = (WIT_USER_PAGE_CAPACITY-baseline-3ULL-remaining) * 4096;
        require(wit_user_memory_reserve(&process.Space, size, 4096, &base) == WIT_STATUS_OK &&
            wit_user_memory_commit(&process.Space, base, size, 3) == WIT_STATUS_OK &&
            process.Space.OwnedCount == WIT_USER_PAGE_CAPACITY - remaining, "Thread OOM setup failed");
        before = wit_pages_free_count(pages);
        require(wit_user_thread_create(&process, WIT_USER_CODE, WIT_USER_INFO, &result) == WIT_STATUS_NO_MEMORY &&
            result == 0 && process.Threads[1].State == WitThreadEmpty && process.ThreadCreates == 1 &&
            process.Handles.Count == 2 && wit_pages_free_count(pages) == before &&
            !wit_user_space_physical(&process.Space, WIT_USER_STACK_BOTTOM + WIT_USER_THREAD_STRIDE, 0, 0) &&
            !wit_user_space_physical(&process.Space, WIT_USER_TLS + WIT_USER_THREAD_STRIDE, 0, 0),
            "Partial thread creation leaked");
        require(wit_user_memory_release(&process.Space, base) == WIT_STATUS_OK &&
            process.Space.OwnedCount == 9 + (WIT_USER_STACK_TOP - WIT_USER_STACK_BOTTOM) / 4096, "Thread OOM recovery leaked");
    }
    while (process.Handles.Count < WIT_HANDLE_CAPACITY)
        handles[count++] = wit_handle_grant(&process.Handles, WIT_HANDLE_SELF, 0);
    before = wit_pages_free_count(pages);
    require(wit_user_thread_create(&process, WIT_USER_CODE, 0, &result) == WIT_STATUS_NO_MEMORY &&
        result == 0 && wit_pages_free_count(pages) == before, "Handle exhaustion allocated a thread");
    while (count) require(wit_handle_close(&process.Handles, handles[--count]) == WIT_STATUS_OK,
        "Thread OOM test handle close failed");
    require(wit_user_thread_create(&process, WIT_USER_CODE, WIT_USER_INFO, &result) == WIT_STATUS_OK &&
        result != 0 && process.Space.OwnedCount == 10 + 2 * (WIT_USER_STACK_TOP - WIT_USER_STACK_BOTTOM) / 4096, "Thread creation failed after resource recovery");
    wit_user_destroy(&process);
    wit_console_write("[TEST-PASS] User.ThreadCreationRollback\n");
}

void wit_user_thread_self_test(WitPageAllocator *pages)
{
    wit_user_native_id_self_test();
    const WitU64 before = wit_pages_free_count(pages);
    static const struct {
        WitU64 Mode, Vector, Error, Address;
        const char *Name;
    } faults[] = {
        { WIT_THREAD_TEST_FAULT, 6, 0, 0, "User.ThreadFault" },
        { WIT_THREAD_TEST_GUARD_LOW, 14, 6, WIT_USER_STACK_BOTTOM + WIT_USER_THREAD_STRIDE - 1, "User.ThreadGuardLow" },
        { WIT_THREAD_TEST_GUARD_HIGH, 14, 6, WIT_USER_STACK_TOP + WIT_USER_THREAD_STRIDE, "User.ThreadGuardHigh" }
    };
    create(pages, WIT_THREAD_TEST_NORMAL);
    wit_user_run(&process);
    check_exit(WIT_TEST_EXIT_CODE);
    require(process.ThreadCreates == 5 && process.ThreadJoins == 3 && process.ThreadReaps == 4 &&
        process.ThreadDeadlocks == 1 && process.ThreadTimerSwitches >= 2 &&
        process.ThreadSwitches >= 6 && process.Space.OwnedCount == 9 + (WIT_USER_STACK_TOP - WIT_USER_STACK_BOTTOM) / 4096, "Thread progress/accounting failed");
    wit_console_write("User thread timer switches: ");
    wit_console_write_u64(process.ThreadTimerSwitches);
    wit_console_write("\n");
    wit_user_destroy(&process);
    require(wit_pages_free_count(pages) == before, "Thread lifecycle leaked");
    wit_console_write("[TEST-PASS] User.ThreadPreemptionAndTls\n[TEST-PASS] User.ThreadJoinAndReuse\n");

    create(pages, WIT_THREAD_TEST_CYCLE);
    wit_user_run(&process);
    check_exit(43);
    require(process.ThreadDeadlocks == 1 && process.ThreadJoins == 1 && process.ThreadReaps == 1,
        "Join cycle was not rejected");
    wit_user_destroy(&process);
    require(wit_pages_free_count(pages) == before, "Join cycle leaked");
    wit_console_write("[TEST-PASS] User.ThreadJoinCycle\n");

    create(pages, WIT_THREAD_TEST_CAPACITY);
    wit_user_run(&process);
    check_exit(WIT_TEST_EXIT_CODE);
    require(process.ThreadCreates == 4 && process.ThreadJoins == 3 && process.ThreadReaps == 3 &&
        process.Space.OwnedCount == 9 + (WIT_USER_STACK_TOP - WIT_USER_STACK_BOTTOM) / 4096, "Thread quota/recovery failed");
    wit_user_destroy(&process);
    require(wit_pages_free_count(pages) == before, "Thread capacity test leaked");
    wit_console_write("[TEST-PASS] User.ThreadCapacity\n");

    allocation_failure(pages);
    require(wit_pages_free_count(pages) == before, "Thread OOM test leaked");
    for (WitU32 i = 0; i < sizeof(faults) / sizeof(faults[0]); ++i) {
        create(pages, faults[i].Mode);
        wit_user_run(&process);
        require(process.State == WitUserFaulted && process.FaultThread == 1 &&
            process.FaultVector == faults[i].Vector && process.FaultError == faults[i].Error &&
            process.FaultCs == WIT_USER_CS && process.FaultSs == WIT_USER_SS &&
            process.Handles.Count == 0, "Child fault was not contained");
        if (faults[i].Vector == 14) require(process.FaultAddress == faults[i].Address, "Wrong child guard address");
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Child fault leaked");
        recovery(pages);
        wit_console_write("[TEST-PASS] ");
        wit_console_write(faults[i].Name);
        wit_console_write("\n");
    }
    create(pages, WIT_THREAD_TEST_BAD_RETURN);
    wit_user_run(&process);
    require(process.State == WitUserBadReturn && process.CurrentThread == 1 &&
        process.Handles.Count == 0, "Accepted return on another thread's stack");
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
