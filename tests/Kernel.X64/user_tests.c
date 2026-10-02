#include "x64.h"
#include "user.h"
#include "witos/platform.h"
#include "protocol.h"
#include "user_image.h"

static WitUserProcess components[2];
static volatile WitU64 kernel_canary = 0xBADC0FFEE0DDF00DULL;

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
    require(wit_user_create(process, pages, slot, wit_user_test_image, sizeof(wit_user_test_image)),
        "User component creation failed");
    info = config(process);
    info->ReadOnlyHandle = wit_handle_grant(&process->Handles, WIT_HANDLE_CONSOLE, 0);
    info->SelfHandle = wit_handle_grant(&process->Handles, WIT_HANDLE_SELF, 0);
    info->ForeignHandle = 0xFFFFFFFF00010001ULL;
    info->Mode = mode;
    info->KernelProbe = (WitU64)&kernel_canary;
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
    require(kernel_canary == 0xBADC0FFEE0DDF00DULL, "Kernel memory was modified by user");
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

typedef struct UserFaultCase {
    WitU64 Mode;
    const char *Name;
    WitU64 Vector;
    WitU64 Error;
    WitU64 Address;
} UserFaultCase;

void wit_user_self_test(WitPageAllocator *pages)
{
    const WitU64 before = wit_pages_free_count(pages);
    static const UserFaultCase faults[] = {{WIT_TEST_KERNEL_READ, "User.KernelRead", 14, 5, (WitU64)&kernel_canary},
        {WIT_TEST_KERNEL_WRITE, "User.KernelWrite", 14, 7, (WitU64)&kernel_canary},
        {WIT_TEST_PRIVILEGED_CLI, "User.PrivilegedCli", 13, 0, 0},
        {WIT_TEST_PRIVILEGED_PORT, "User.PrivilegedPort", 13, 0, 0},
        {WIT_TEST_EXECUTE_DATA, "User.Nx", 14, 21, WIT_USER_DATA},
        {WIT_TEST_GUARD_LOW, "User.GuardLow", 14, 6, WIT_USER_STACK_BOTTOM - 1},
        {WIT_TEST_GUARD_HIGH, "User.GuardHigh", 14, 6, WIT_USER_STACK_TOP},
        {WIT_TEST_WRITE_CODE, "User.WriteCode", 14, 7, WIT_USER_CODE},
        {WIT_TEST_WRITE_INFO, "User.WriteInfo", 14, 7, WIT_USER_INFO}, {WIT_TEST_NULL_READ, "User.NullRead", 14, 4, 0},
        {WIT_TEST_INVALID_OPCODE, "User.InvalidOpcode", 6, 0, 0},
        {WIT_TEST_MEMORY_RESERVED, "User.MemoryReservedFault", 14, 4, WIT_USER_MEMORY_BASE},
        {WIT_TEST_MEMORY_DECOMMITTED, "User.MemoryDecommittedFault", 14, 4, WIT_USER_MEMORY_BASE},
        {WIT_TEST_MEMORY_RELEASED, "User.MemoryReleasedFault", 14, 4, WIT_USER_MEMORY_BASE},
        {WIT_TEST_MEMORY_READONLY, "User.MemoryReadOnlyFault", 14, 7, WIT_USER_MEMORY_BASE},
        {WIT_TEST_MEMORY_NOACCESS, "User.MemoryNoAccessFault", 14, 4, WIT_USER_MEMORY_BASE},
        {WIT_TEST_MEMORY_NX, "User.MemoryNxFault", 14, 21, WIT_USER_MEMORY_BASE}};
    WitU64 data_a, data_b, old_data, old_console;

    wit_console_write("[TEST-BEGIN] User.Isolation\n");
    create(pages, 0, WIT_TEST_NORMAL);
    create(pages, 1, WIT_TEST_NORMAL);
    config(&components[0])->ForeignHandle = config(&components[1])->Startup.ConsoleHandle;
    config(&components[1])->ForeignHandle = config(&components[0])->Startup.ConsoleHandle;
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
    wit_console_write("[TEST-PASS] User.Ring3\n[TEST-PASS] User.AbiAndHandles\n[TEST-PASS] User.PrivateMemory\n");

    /* Keep one peer's private page alive and prove it is absent in the other CR3. */
    create(pages, 0, WIT_TEST_NORMAL);
    require(wit_user_space_map(&components[0].Space, WIT_USER_PEER_PAGE, 1, 0), "Peer page setup failed");
    *(WitU64 *)wit_user_space_physical(&components[0].Space, WIT_USER_PEER_PAGE, 0, 0) = 0x12345678;
    create(pages, 1, WIT_TEST_PEER_READ);
    wit_user_run(&components[1]);
    require(components[1].State == WitUserFaulted &&
            components[1].FaultVector == 14 &&
            components[1].FaultError == 4 &&
            components[1].FaultAddress == WIT_USER_PEER_PAGE &&
            components[1].FaultState.Cs == WIT_USER_CS &&
            components[1].Handles.Count == 0,
        "Peer private page was accessible");
    wit_user_destroy(&components[1]);
    wit_user_run(&components[0]);
    check_normal(&components[0]);
    wit_user_destroy(&components[0]);
    require(wit_pages_free_count(pages) == before, "Peer test leaked pages");
    wit_console_write("[TEST-PASS] User.PeerMemory\n");

    for (WitU32 i = 0; i < sizeof(faults) / sizeof(faults[0]); ++i) {
        create(pages, 0, faults[i].Mode);
        wit_user_run(&components[0]);
        require(components[0].State == WitUserFaulted &&
                components[0].FaultVector == faults[i].Vector &&
                components[0].FaultError == faults[i].Error &&
                components[0].FaultState.Cs == WIT_USER_CS &&
                components[0].FaultState.Ss == WIT_USER_SS &&
                ((components[0].FaultState.Rip >= WIT_USER_CODE && components[0].FaultState.Rip < WIT_USER_LIMIT) ||
                    (faults[i].Mode == WIT_TEST_MEMORY_NX && components[0].FaultState.Rip == WIT_USER_MEMORY_BASE)) &&
                components[0].Handles.Count == 0,
            "User fault was not contained");
        if (faults[i].Vector == 14) {
            require(components[0].FaultAddress == faults[i].Address, "Unexpected user fault address");
        }
        require(kernel_canary == 0xBADC0FFEE0DDF00DULL, "User fault corrupted kernel");
        wit_user_destroy(&components[0]);
        require(wit_pages_free_count(pages) == before, "Faulted component leaked pages");
        recovery(pages);
        wit_console_write("[TEST-PASS] ");
        wit_console_write(faults[i].Name);
        wit_console_write("\n");
    }

    wit_user_memory_self_test(pages);
    create(pages, 0, WIT_TEST_MEMORY_LIFECYCLE);
    wit_user_run(&components[0]);
    require(components[0].State == WitUserExited &&
            components[0].ExitCode == WIT_TEST_EXIT_CODE &&
            components[0].Writes == 1 &&
            components[0].Handles.Count == 0 &&
            components[0].Space.OwnedCount == 9 + (WIT_USER_STACK_TOP - WIT_USER_STACK_BOTTOM) / 4096,
        "User memory lifecycle failed");
    wit_user_destroy(&components[0]);
    require(wit_pages_free_count(pages) == before, "User memory lifecycle leaked");
    recovery(pages);
    wit_console_write("[TEST-PASS] User.MemoryLifecycle\n");

    wit_user_thread_self_test(pages);
    wit_user_wait_self_test(pages);
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

    create(pages, 0, WIT_TEST_BAD_RETURN);
    wit_user_run(&components[0]);
    require(components[0].State == WitUserBadReturn && components[0].Handles.Count == 0,
        "Noncanonical user stack reached IRETQ");
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
    old_console = config(&components[0])->Startup.ConsoleHandle;
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
    wit_user_runtime_boot_test(pages);
    wit_user_code_self_test(pages);
    wit_user_file_self_test(pages);
}
