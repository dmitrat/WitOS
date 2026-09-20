#include "user.h"
#if defined(WITOS_TEST_RUNTIME_CONFIG)
#include "witos/platform.h"
#include "protocol.h"
#include "runtime_config_image.h"

static WitUserProcess process;
static void require(int condition, const char* message) { if (!condition) wit_panic(message); }
static void run(WitPageAllocator* pages, WitU64 mode, WitU64 base)
{
    const WitU64 before = wit_pages_free_count(pages);
    require(wit_user_create_pe(&process, pages, 0, wit_runtime_config_image, sizeof(wit_runtime_config_image), base) == WitPeOk,
        "Runtime configuration fixture load failed");
    const WitU32 owned = process.Space.OwnedCount;
    WitUserTestConfig* config = (WitUserTestConfig*)wit_user_space_physical(&process.Space, WIT_USER_INFO, 0, 0);
    config->Mode = mode;
    wit_user_run(&process);
    const WitU64* report = (const WitU64*)wit_user_space_physical(&process.Space, WIT_GC_INFO_REPORT, 0, 0);
    if (process.State != WitUserExited || process.ExitCode != WIT_TEST_EXIT_CODE) {
        wit_console_write("Runtime config state/code: "); wit_console_write_u64(process.State);
        wit_console_write("/"); wit_console_write_u64(process.ExitCode); wit_console_write("\n");
        wit_panic("Upstream configuration contract failed");
    }
    require(report && report[0] == mode && report[1] == 63 && process.ThreadCreates == 4 &&
        process.ThreadJoins == 3 && process.ThreadReaps == 3 && process.ThreadSwitches,
        "Runtime configuration missed required checks or thread reuse");
    require(process.Space.OwnedCount == owned && !process.Handles.Count && !process.Events.Count,
        "Runtime configuration leaked resources");
    for (WitU32 i = 0; i < WIT_USER_RESERVATION_CAPACITY; ++i)
        require(!process.Space.Reservations[i].Size, "Runtime configuration leaked a reservation");
    wit_user_destroy(&process);
    require(wit_pages_free_count(pages) == before, "Runtime configuration teardown leaked physical memory");
}
void wit_user_runtime_config_self_test(WitPageAllocator* pages)
{
    run(pages, 0, WIT_USER_IMAGE_BASE);
    run(pages, 0, WIT_USER_IMAGE_ALTERNATE);
    run(pages, 1, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.RuntimeConfigCrt\n[TEST-PASS] User.RhConfigPrecedence\n");
    wit_console_write("[TEST-PASS] User.RhConfigStrings\n[TEST-PASS] User.GcConfigValues\n");
    wit_console_write("[TEST-PASS] User.GcConfigRefresh\n[TEST-PASS] User.RuntimeConfigThreads\n");
}
#endif
