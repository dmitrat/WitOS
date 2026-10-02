#include "user.h"
#include "witos/platform.h"
#include "protocol.h"
#include "pal_background_image.h"

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
    require(wit_user_create_pe(&process, pages, 0, wit_pal_background_image, sizeof(wit_pal_background_image), base) ==
            WitPeOk,
        "PAL background fixture load failed");
    const WitU32 owned = process.Space.OwnedCount;
    WitUserTestConfig *config = (WitUserTestConfig *)wit_user_space_physical(&process.Space, WIT_USER_INFO, 0, 0);
    config->Mode = mode;
    wit_user_run(&process);
    const WitU64 *report = (const WitU64 *)wit_user_space_physical(&process.Space, WIT_GC_INFO_REPORT, 0, 0);
    require(report && report[0] == mode, "PAL worker failed before intended boundary");
    if (mode <= 2 || mode == 6) {
        const WitU64 expected = mode == 6 ? 73 : WIT_TEST_EXIT_CODE;
        if (process.State != WitUserExited || process.ExitCode != expected) {
            wit_console_write("PAL background mode/state/code: ");
            wit_console_write_u64(mode);
            wit_console_write("/");
            wit_console_write_u64(process.State);
            wit_console_write("/");
            wit_console_write_u64(process.ExitCode);
            wit_console_write("\n");
            wit_console_write("PAL background ticks/limit/rollback-boundaries: ");
            wit_console_write_u64(process.Ticks);
            wit_console_write("/");
            wit_console_write_u64(process.TickLimit);
            wit_console_write("/");
            wit_console_write_u64(report[3]);
            wit_console_write("\n");
            wit_panic("Detached PAL worker lifecycle failed");
        }
        const WitU64 detached = mode == 0 ? 12 : mode == 1 ? 4 : 1;
        require(process.ThreadCreates == detached + 1 &&
                process.DetachedCreates == detached &&
                process.ThreadReaps == detached &&
                process.DetachedReaps == detached &&
                process.ThreadJoins == 0 &&
                process.Space.OwnedCount == owned,
            "Detached worker resources were not automatically reaped");
        if (mode == 2) {
            require(report[3] == (WIT_USER_STACK_TOP - WIT_USER_STACK_BOTTOM) / 4096 + 2,
                "PAL rollback did not cover every stack/TLS allocation boundary");
            wit_console_write("PAL rollback boundaries/ticks/limit: ");
            wit_console_write_u64(report[3]);
            wit_console_write("/");
            wit_console_write_u64(process.Ticks);
            wit_console_write("/");
            wit_console_write_u64(process.TickLimit);
            wit_console_write("\n");
        }
        if (mode == 6) {
            require(report[1] == 1 && report[2] == 1, "Last-thread completion skipped TLS cleanup");
        }
        for (WitU32 i = 0; i < WIT_USER_RESERVATION_CAPACITY; ++i) {
            require(!process.Space.Reservations[i].Size, "PAL worker TLS cleanup leaked a reservation");
        }
    } else {
        require(process.DetachedCreates == 1 && process.DetachedReaps == 0 && process.Space.OwnedCount > owned,
            "PAL worker failure did not exercise a live detached thread");
        if (mode == 5) {
            require(process.State == WitUserExited && process.ExitCode == WIT_GC_TEST_FAIL_FAST_EXIT,
                "Detached TLS constructor failure was not contained");
        } else {
            require(process.State == WitUserFaulted &&
                    process.FaultThread != 0 &&
                    process.FaultVector == 14 &&
                    process.FaultError == 6 &&
                    process.FaultAddress == 0 &&
                    process.FaultState.Cs == WIT_USER_CS &&
                    process.FaultState.Ss == WIT_USER_SS,
                "Detached callback/destructor fault was not contained");
        }
    }
    require(!process.Handles.Count && !process.Events.Count, "PAL background handles leaked");
    wit_user_destroy(&process);
    require(wit_pages_free_count(pages) == before, "PAL background teardown leaked physical frames");
}

void wit_user_pal_background_self_test(WitPageAllocator *pages)
{
    run(pages, 0, WIT_USER_IMAGE_BASE);
    run(pages, 0, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.PalBackgroundLifecycle\n");
    run(pages, 1, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.PalBackgroundCapacity\n");
    run(pages, 2, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.PalBackgroundRollback\n");
    run(pages, 6, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.DetachedLastExit\n");
    for (WitU64 mode = 3; mode <= 5; ++mode) {
        run(pages, mode, WIT_USER_IMAGE_BASE);
    }
    run(pages, 0, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.PalBackgroundIsolation\n");
}
