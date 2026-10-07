#include "x64.h"
#include "user.h"
#include "witos/platform.h"
#include "protocol.h"
#include "pal_image.h"
#include "self_test.h"

static WitUserProcess process;

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

static void run(WitPageAllocator *pages, WitU64 mode, WitU64 base)
{
    const WitU64 before = wit_pages_free_count(pages);
    require(wit_user_create_pe(&process, pages, 0, wit_pal_tls_image, sizeof(wit_pal_tls_image), base) == WitPeOk,
        "PAL service fixture load failed");
    const WitU32 owned = process.Space.OwnedCount;
    WitUserTestConfig *config = (WitUserTestConfig *)wit_user_space_physical(&process.Space, WIT_USER_INFO, 0, 0);
    config->Mode = mode;
    wit_user_run(&process);
    if (mode <= 15) {
        if (process.State != WitUserExited || process.ExitCode != WIT_TEST_EXIT_CODE) {
            wit_console_write("PAL service mode/state/code: ");
            wit_console_write_u64(mode);
            wit_console_write("/");
            wit_console_write_u64(process.State);
            wit_console_write("/");
            wit_console_write_u64(process.ExitCode);
            wit_console_write("\n");
            wit_panic("PAL service contract failed");
        }
        require(process.Space.OwnedCount == owned, "PAL services leaked committed frames");
        for (WitU32 i = 0; i < WIT_USER_RESERVATION_CAPACITY; ++i) {
            require(!process.Space.Reservations[i].Size, "PAL services leaked reservations");
        }
        if (mode == 13) {
            require(process.ThreadCreates == 5 &&
                    process.ThreadReaps == 4 &&
                    process.EventParks > 0 &&
                    process.EventWakes > 0,
                "PAL handoff did not park/wake workers");
        }
        if (mode == 14) {
            require(process.IdleHalts > 0 && process.WaitTimeouts > 0, "PAL finite waits did not use idle/deadlines");
        }
        if (mode == 15) {
            require(process.ThreadCreates == 2 && process.ThreadReaps == 1 && process.WaitCloses == 1,
                "PAL close did not cancel the parked generation");
        }
    } else if (mode == 20) {
        const WitU64 report = wit_user_space_physical(&process.Space, WIT_GC_INFO_REPORT, 0, 0);
        require(process.State == WitUserExited &&
                process.ExitCode == WIT_GC_TEST_FAIL_FAST_EXIT &&
                report &&
                *(WitU64 *)report == mode,
            "Invalid PAL free did not fail fast");
    } else {
        const WitU64 error = mode == 16 ? 7 : mode == 18 ? 21 : 4;
        require(wit_test_faulted(&process) &&
                process.FaultVector == 14 &&
                process.FaultError == error &&
                process.FaultAddress == WIT_USER_MEMORY_BASE &&
                process.FaultState.Cs == WIT_USER_CS &&
                process.FaultState.Ss == WIT_USER_SS,
            "PAL memory hardware protection failed");
    }
    require(!process.Handles.Count && !process.Events.Count, "PAL service handles leaked");
    wit_user_destroy(&process);
    require(wit_pages_free_count(pages) == before, "PAL service component teardown leaked");
}

void wit_user_pal_services_self_test(WitPageAllocator *pages)
{
    run(pages, 10, WIT_USER_IMAGE_BASE);
    run(pages, 10, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.PalMemory\n");
    run(pages, 11, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.PalMemoryRollback\n");
    run(pages, 12, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.PalEventState\n");
    run(pages, 13, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.PalEventHandoff\n");
    run(pages, 14, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.PalWaitTime\n");
    run(pages, 15, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.PalCloseCancellation\n");
    for (WitU64 mode = 16; mode <= 19; ++mode) {
        run(pages, mode, WIT_USER_IMAGE_BASE);
    }
    run(pages, 10, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.PalMemoryProtection\n");
    run(pages, 20, WIT_USER_IMAGE_BASE);
    run(pages, 10, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.PalFreeFailFast\n");
}
