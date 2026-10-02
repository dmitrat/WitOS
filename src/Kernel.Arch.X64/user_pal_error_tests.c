#include "user.h"
#include "witos/platform.h"
#include "protocol.h"
#include "pal_image.h"

static WitUserProcess process;

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

static void run(WitPageAllocator *pages, WitU64 mode, int tls, WitU64 base)
{
    const WitU64 before = wit_pages_free_count(pages);
    const WitU8 *image = tls ? wit_pal_tls_image : wit_pal_plain_image;
    const WitU32 size = tls ? sizeof(wit_pal_tls_image) : sizeof(wit_pal_plain_image);
    require(wit_user_create_pe(&process, pages, 0, image, size, base) == WitPeOk, "PAL error fixture load failed");
    const WitU32 owned = process.Space.OwnedCount;
    WitUserTestConfig *config = (WitUserTestConfig *)wit_user_space_physical(&process.Space, WIT_USER_INFO, 0, 0);
    config->Mode = mode;
    wit_user_run(&process);
    if (mode == 32) {
        const WitU64 *report = (const WitU64 *)wit_user_space_physical(&process.Space, WIT_GC_INFO_REPORT, 0, 0);
        require(report &&
                process.State == WitUserFaulted &&
                process.FaultVector == 14 &&
                process.FaultError == 7 &&
                process.FaultAddress == *report &&
                process.FaultCs == WIT_USER_CS &&
                process.FaultSs == WIT_USER_SS &&
                *report >= base &&
                *report < base + process.ImageSize &&
                !wit_user_space_physical(&process.Space, *report, 1, 0),
            "Last-error import binding was writable");
    } else {
        if (process.State != WitUserExited || process.ExitCode != WIT_TEST_EXIT_CODE) {
            wit_console_write("Last-error mode/state/code: ");
            wit_console_write_u64(mode);
            wit_console_write("/");
            wit_console_write_u64(process.State);
            wit_console_write("/");
            wit_console_write_u64(process.ExitCode);
            wit_console_write("\n");
            wit_panic("Native last-error contract failed");
        }
        if (mode == 30) {
            require(process.ThreadCreates == 4 &&
                    process.ThreadJoins == 3 &&
                    process.ThreadReaps == 3 &&
                    process.ThreadTimerSwitches > 0 &&
                    process.IdleHalts > 0,
                "Last-error isolation did not exercise preemption/reuse/idle");
        }
        require(process.Space.OwnedCount == owned, "Last-error test leaked frames");
        for (WitU32 i = 0; i < WIT_USER_RESERVATION_CAPACITY; ++i) {
            require(!process.Space.Reservations[i].Size, "Last-error rollback leaked a reservation");
        }
    }
    require(!process.Handles.Count && !process.Events.Count, "Last-error test leaked handles");
    wit_user_destroy(&process);
    require(wit_pages_free_count(pages) == before, "Last-error teardown leaked physical memory");
}

void wit_user_pal_error_self_test(WitPageAllocator *pages)
{
    run(pages, 30, 0, WIT_USER_IMAGE_BASE);
    run(pages, 30, 1, WIT_USER_IMAGE_BASE);
    run(pages, 30, 1, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.NativeLastError\n");
    run(pages, 31, 0, WIT_USER_IMAGE_BASE);
    run(pages, 31, 1, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.PalErrorCodes\n");
    run(pages, 32, 1, WIT_USER_IMAGE_BASE);
    run(pages, 30, 0, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.LastErrorBindingProtection\n");
}
