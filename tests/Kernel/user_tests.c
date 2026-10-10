#include "user.h"
#include "witos/platform.h"
#include "protocol.h"
#include "self_test.h"
#include "user_arch_tests.h"
#include "user_image.h"

/* User isolation tests shared by every architecture; tests/Kernel.<isa> supplies the expected faults. */

static WitUserProcess components[2];
volatile WitU64 wit_test_kernel_canary = 0xBADC0FFEE0DDF00DULL;

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

static WitUserTestConfig *config(WitUserProcess *process)
{
    return (WitUserTestConfig *)wit_user_space_physical(&process->Space, WIT_USER_INFO, 0, 0);
}

static void create(WitPageAllocator *pages, WitU32 slot, WitU64 mode)
{
    WitUserProcess *process = &components[slot];
    WitUserTestConfig *info;
    require(wit_test_create_fixture(process, pages, slot, wit_user_test_image, sizeof(wit_user_test_image)),
        "User component creation failed");
    info = config(process);
    info->ReadOnlyHandle = wit_handle_grant(&process->Handles, WIT_HANDLE_CONSOLE, 0);
    info->SelfHandle = wit_handle_grant(&process->Handles, WIT_HANDLE_SELF, 0);
    info->ForeignHandle = 0xFFFFFFFF00010001ULL;
    info->Mode = mode;
    info->KernelProbe = (WitU64)&wit_test_kernel_canary;
    info->InstanceId = process->Id;
    require(info->ReadOnlyHandle != 0 && info->SelfHandle != 0, "User handle grants failed");
}

static void check_normal(WitUserProcess *process)
{
    require(process->State == WitUserExited &&
            process->ExitCode == WIT_TEST_EXIT_CODE &&
            process->Writes == 2 &&
            process->Handles.Count == 0,
        "User ABI/handle checks failed");
    require(*(WitU64 *)wit_user_space_physical(&process->Space, WIT_USER_DATA, 0, 0) == process->Id,
        "User private state incorrect");
    require(wit_test_kernel_canary == 0xBADC0FFEE0DDF00DULL, "Kernel memory was modified by user");
}

static void recovery(WitPageAllocator *pages)
{
    const WitU64 before = wit_pages_free_count(pages);
    create(pages, 0, WIT_TEST_NORMAL);
    wit_user_run(&components[0]);
    check_normal(&components[0]);
    wit_user_destroy(&components[0]);
    require(wit_pages_free_count(pages) == before, "Recovery leaked pages");
}

int wit_test_user_fault_contained(const WitUserProcess *process, const WitUserFaultCase *expected, int check_pc)
{
    WitU64 pc = 0;
    return wit_test_faulted(process) &&
        process->FaultVector == expected->Vector &&
        process->FaultError == expected->Error &&
        (!expected->CheckAddress || process->FaultAddress == expected->Address) &&
        wit_test_user_fault_state(&process->FaultState, &pc) &&
        (!check_pc ||
            (pc >= WIT_USER_BASE && pc < WIT_USER_LIMIT) ||
            (expected->PcAtAddress && pc == expected->Address)) &&
        process->Handles.Count == 0;
}

/* Returns the free page count before the first component, which the end of the isolation tests restores. */
WitU64 wit_user_isolation_begin_self_test(WitPageAllocator *pages)
{
    const WitU64 before = wit_pages_free_count(pages);
    WitU64 data_a, data_b;

    wit_console_write("[TEST-BEGIN] User.Isolation\n");
    create(pages, 0, WIT_TEST_NORMAL);
    create(pages, 1, WIT_TEST_NORMAL);
    config(&components[0])->ForeignHandle = config(&components[1])->Startup.Handles[WIT_ROOT_HANDLE_LOG];
    config(&components[1])->ForeignHandle = config(&components[0])->Startup.Handles[WIT_ROOT_HANDLE_LOG];
    data_a = wit_user_space_physical(&components[0].Space, WIT_USER_DATA, 0, 0);
    data_b = wit_user_space_physical(&components[1].Space, WIT_USER_DATA, 0, 0);
    require(components[0].Space.Root != components[1].Space.Root && data_a != data_b,
        "User address spaces share private memory");
    /* B must reject A's still-live handle. Both see the same virtual layout. */
    wit_user_run(&components[1]);
    check_normal(&components[1]);
    require(*(WitU64 *)data_a == 0, "Peer private data was changed");
    wit_user_run(&components[0]);
    check_normal(&components[0]);
    require(*(WitU64 *)data_b == components[1].Id, "Private data was aliased");
    wit_user_destroy(&components[0]);
    wit_user_destroy(&components[1]);
    require(wit_pages_free_count(pages) == before, "User pair leaked pages");
    wit_console_write(
        "[TEST-PASS] User.UnprivilegedMode\n[TEST-PASS] User.AbiAndHandles\n[TEST-PASS] User.PrivateMemory\n");

    /* Keep one peer's private page alive and prove it is absent in the other address space. */
    create(pages, 0, WIT_TEST_NORMAL);
    require(wit_user_space_map(&components[0].Space, WIT_USER_PEER_PAGE, 1, 0), "Peer page setup failed");
    *(WitU64 *)wit_user_space_physical(&components[0].Space, WIT_USER_PEER_PAGE, 0, 0) = 0x12345678;
    create(pages, 1, WIT_TEST_PEER_READ);
    wit_user_run(&components[1]);
    require(wit_test_user_fault_contained(&components[1], &wit_test_user_peer_fault, 0),
        "Peer private page was accessible");
    wit_user_destroy(&components[1]);
    wit_user_run(&components[0]);
    check_normal(&components[0]);
    wit_user_destroy(&components[0]);
    require(wit_pages_free_count(pages) == before, "Peer test leaked pages");
    wit_console_write("[TEST-PASS] User.PeerMemory\n");

    for (WitU32 i = 0; i < wit_test_user_fault_count; ++i) {
        const WitUserFaultCase *fault = &wit_test_user_faults[i];
        create(pages, 0, fault->Mode);
        wit_user_run(&components[0]);
        require(wit_test_user_fault_contained(&components[0], fault, 1), "User fault was not contained");
        require(wit_test_kernel_canary == 0xBADC0FFEE0DDF00DULL, "User fault corrupted kernel");
        wit_user_destroy(&components[0]);
        require(wit_pages_free_count(pages) == before, "Faulted component leaked pages");
        recovery(pages);
        wit_console_write("[TEST-PASS] ");
        wit_console_write(fault->Name);
        wit_console_write("\n");
    }

    wit_user_memory_self_test(pages);
    create(pages, 0, WIT_TEST_MEMORY_LIFECYCLE);
    wit_user_run(&components[0]);
    require(components[0].State == WitUserExited &&
            components[0].ExitCode == WIT_TEST_EXIT_CODE &&
            components[0].Writes == 1 &&
            components[0].Handles.Count == 0 &&
            components[0].Space.OwnedCount == 8 + (WIT_USER_STACK_TOP - WIT_USER_STACK_BOTTOM) / 4096,
        "User memory lifecycle failed");
    wit_user_destroy(&components[0]);
    require(wit_pages_free_count(pages) == before, "User memory lifecycle leaked");
    recovery(pages);
    wit_console_write("[TEST-PASS] User.MemoryLifecycle\n");
    return before;
}

void wit_user_isolation_end_self_test(WitPageAllocator *pages, WitU64 before)
{
    WitU64 old_data, old_console;

    create(pages, 0, WIT_TEST_BAD_RETURN);
    wit_user_run(&components[0]);
    require(components[0].State == WitUserBadReturn && components[0].Handles.Count == 0,
        "User stack outside its owning range reached the return to user mode");
    wit_user_destroy(&components[0]);
    recovery(pages);
    wit_console_write("[TEST-PASS] User.BadReturn\n");

    create(pages, 0, WIT_TEST_SPIN);
    wit_user_run(&components[0]);
    require(components[0].State == WitUserBudgetExpired &&
            components[0].Ticks == WIT_USER_TICK_BUDGET &&
            components[0].Handles.Count == 0,
        "Uncooperative user did not lose execution");
    wit_user_destroy(&components[0]);
    recovery(pages);
    wit_console_write("[TEST-PASS] User.TimerBudget\n");

    create(pages, 0, WIT_TEST_PREEMPTION_STATE);
    wit_user_run(&components[0]);
    require(components[0].State == WitUserBudgetExpired &&
            components[0].Ticks == WIT_USER_TICK_BUDGET &&
            components[0].Handles.Count == 0,
        "Timer corrupted user flags, GPR or SIMD state");
    wit_user_destroy(&components[0]);
    recovery(pages);
    wit_console_write("[TEST-PASS] User.PreemptionState\n");

    create(pages, 0, WIT_TEST_NORMAL);
    old_data = wit_user_space_physical(&components[0].Space, WIT_USER_DATA, 0, 0);
    old_console = config(&components[0])->Startup.Handles[WIT_ROOT_HANDLE_LOG];
    for (WitU32 i = 0; i < 4096; ++i) {
        ((WitU8 *)old_data)[i] = 0xA5;
    }
    wit_user_destroy(&components[0]);
    create(pages, 0, WIT_TEST_NORMAL);
    require(wit_user_space_physical(&components[0].Space, WIT_USER_DATA, 0, 0) == old_data,
        "Zero-fill test did not exercise physical reuse");
    config(&components[0])->ForeignHandle = old_console;
    /* The normal fixture scans both data pages and rejects the stale token. */
    wit_user_run(&components[0]);
    check_normal(&components[0]);
    wit_user_destroy(&components[0]);
    require(wit_pages_free_count(pages) == before, "User teardown leaked physical pages");
    wit_console_write(
        "[TEST-PASS] User.ZeroFillAndStaleHandles\n[TEST-PASS] User.Teardown\n[TEST-PASS] User.Isolation\n");
}
