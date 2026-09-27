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
    const WitU8* image = (mode == 8 || mode == 11 || mode == 13 || mode == 16 || mode == 18) ? wit_runtime_config_raw_image : wit_runtime_config_image;
    const WitU32 size = (mode == 8 || mode == 11 || mode == 13 || mode == 16 || mode == 18) ? sizeof(wit_runtime_config_raw_image) : sizeof(wit_runtime_config_image);
    require(wit_user_create_pe(&process, pages, 0, image, size, base) == WitPeOk,
        "Runtime configuration fixture load failed");
    const WitU32 owned = process.Space.OwnedCount;
    WitUserTestConfig* config = (WitUserTestConfig*)wit_user_space_physical(&process.Space, WIT_USER_INFO, 0, 0);
    config->Mode = mode;
    wit_user_run(&process);
    const WitU64* report = (const WitU64*)wit_user_space_physical(&process.Space, WIT_GC_INFO_REPORT, 0, 0);
    if (mode == 11) {
        require(process.State == WitUserExited && process.ExitCode == WIT_GC_TEST_FAIL_FAST_EXIT &&
            report && report[0] == 11 && report[1] == 32768 && !process.Threads[0].CompilerTls &&
            process.Space.OwnedCount == owned && !process.Handles.Count && !process.Events.Count,
            "ThreadStore metadata accessed unavailable compiler TLS");
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "ThreadStore rejected-TLS teardown leaked pages");
        return;
    }
    if (process.State != WitUserExited || process.ExitCode != WIT_TEST_EXIT_CODE) {
        wit_console_write("Runtime config state/code: "); wit_console_write_u64(process.State);
        wit_console_write("/"); wit_console_write_u64(process.ExitCode); wit_console_write("\n");
        wit_panic("Upstream configuration contract failed");
    }
    if (mode == 18 || mode == 19) {
        require(report && report[0] == mode && report[1] == 524288 &&
            process.ThreadCreates == (mode == 18 ? 1U : 7U) && process.ThreadJoins == (mode == 18 ? 0U : 6U) &&
            process.ThreadReaps == (mode == 18 ? 0U : 6U) &&
            (process.Threads[0].CompilerTls != 0) == (mode == 19) && process.Space.OwnedCount == owned &&
            !process.Handles.Count && !process.Events.Count, "Native CRT range/parse/resource contract failed");
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Native CRT teardown leaked pages");
        return;
    }
    if (mode >= 15 && mode <= 17) {
        require(report && report[0] == mode && report[1] == 262144 &&
            process.ThreadCreates == (mode == 16 ? 1U : 7U) && process.ThreadJoins == (mode == 16 ? 0U : 6U) &&
            process.ThreadReaps == (mode == 16 ? 0U : 6U) &&
            (process.Threads[0].CompilerTls != 0) == (mode != 16) && process.Space.OwnedCount == owned &&
            !process.Handles.Count && !process.Events.Count, "Minipal time/TLS or native resource contract failed");
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Minipal time teardown leaked pages");
        return;
    }
    if (mode == 12 || mode == 13) {
        require(report && report[0] == mode && report[1] == 65536 &&
            process.ProcessWriteBarriers == (mode == 12 ? 50U : 2U) &&
            process.ThreadCreates == (mode == 12 ? 4U : 1U) && process.ThreadJoins == (mode == 12 ? 3U : 0U) &&
            process.ThreadReaps == (mode == 12 ? 3U : 0U) &&
            (process.Threads[0].CompilerTls != 0) == (mode == 12) &&
            process.Space.OwnedCount == owned && !process.Handles.Count && !process.Events.Count,
            "GC/PAL barrier count, thread lifecycle or no-allocation contract failed");
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Process barrier teardown leaked pages");
        return;
    }
    if (mode == 9 || mode == 10 || mode == 14) {
        require(report && report[0] == mode && report[1] == (mode == 9 ? 8192U : (mode == 14 ? 155648U : 24576U)) &&
            process.ThreadCreates == (mode == 9 ? 1U : (mode == 14 ? 16U : 4U)) && process.ThreadJoins == (mode == 9 ? 0U : (mode == 14 ? 15U : 3U)) &&
            process.ThreadReaps == (mode == 9 ? 0U : (mode == 14 ? 15U : 3U)) &&
            process.Space.OwnedCount > owned, "Interface dispatch initialization missed real allocations");
        WitU32 reservations = 0;
        for (WitU32 i = 0; i < WIT_USER_RESERVATION_CAPACITY; ++i)
            if (process.Space.Reservations[i].Size) ++reservations;
        require(reservations == 2 && !process.Handles.Count && !process.Events.Count,
            "Interface dispatch retained unexpected resources");
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Interface dispatch process teardown leaked pages");
        return;
    }
    if (mode == 3 || mode == 7 || mode == 8)
        require(report && report[0] == mode && report[1] == ((mode == 8 || mode == 11 || mode == 13 || mode == 16 || mode == 18) ? 2048 : 1089) &&
            process.ThreadCreates == 1 && !process.ThreadJoins && !process.ThreadReaps &&
            (process.Threads[0].CompilerTls != 0) == (mode != 8), "PAL initialization rejection missed its intended boundary");
    else
        require(report && report[0] == mode && report[1] == 5119 && process.ThreadCreates == 7 &&
            process.ThreadJoins == 6 && process.ThreadReaps == 6 && process.ThreadSwitches,
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
    for (WitU64 mode = 2; mode <= 8; ++mode) run(pages, mode, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.RuntimeConfigCrt\n[TEST-PASS] User.RhConfigPrecedence\n");
    wit_console_write("[TEST-PASS] User.RhConfigStrings\n[TEST-PASS] User.GcConfigValues\n");
    wit_console_write("[TEST-PASS] User.GcConfigRefresh\n[TEST-PASS] User.RuntimeConfigThreads\n");
    wit_console_write("[TEST-PASS] User.PalInitPrerequisites\n[TEST-PASS] User.PalInitPolicy\n");
    wit_console_write("[TEST-PASS] User.PalInitLifecycle\n");
    wit_console_write("[TEST-PASS] User.RuntimeAllocHeap\n");
    run(pages, 9, WIT_USER_IMAGE_BASE); run(pages, 9, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.InterfaceDispatchInit\n");
    run(pages, 10, WIT_USER_IMAGE_BASE); run(pages, 10, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.RuntimeInstanceStartup\n");
    run(pages, 14, WIT_USER_IMAGE_BASE); run(pages, 14, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.RuntimeThreadRecord\n");
    run(pages, 11, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.ThreadStoreTlsPrerequisite\n");
    run(pages, 12, WIT_USER_IMAGE_BASE); run(pages, 12, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.GcProcessWriteBarrier\n");
    run(pages, 13, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.ProcessBarrierWithoutTls\n");
    run(pages, 15, WIT_USER_IMAGE_BASE); run(pages, 15, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.MinipalTime\n");
    run(pages, 16, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.MinipalTimeWithoutTls\n");
    run(pages, 17, WIT_USER_IMAGE_BASE); run(pages, 17, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.RuntimeRandomTls\n");
    run(pages, 18, WIT_USER_IMAGE_BASE); run(pages, 18, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.CrtMemoryAndStrings\n");
    run(pages, 19, WIT_USER_IMAGE_BASE); run(pages, 19, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.CrtUnsignedLong\n");
}
#endif
