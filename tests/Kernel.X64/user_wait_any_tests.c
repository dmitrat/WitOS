#include "x64.h"
#include "user.h"
#include "witos/platform.h"
#include "protocol.h"
#include "pal_image.h"

static WitUserProcess process;

static void require(int ok, const char *message)
{
    if (!ok) {
        wit_panic(message);
    }
}

static void run(WitPageAllocator *pages, WitU64 mode, WitU64 base, int tls)
{
    const WitU64 before = wit_pages_free_count(pages);
    const WitU8 *image = tls ? wit_pal_tls_image : wit_pal_plain_image;
    const WitU32 size = tls ? sizeof(wit_pal_tls_image) : sizeof(wit_pal_plain_image);
    require(wit_user_create_pe(&process, pages, 0, image, size, base) == WitPeOk, "Wait-any fixture load failed");
    const WitU32 owned = process.Space.OwnedCount;
    ((WitUserTestConfig *)wit_user_space_physical(&process.Space, WIT_USER_INFO, 0, 0))->Mode = mode;
    wit_user_run(&process);
    const WitU64 *report = (const WitU64 *)wit_user_space_physical(&process.Space, WIT_GC_INFO_REPORT, 0, 0);
    if (process.State != WitUserExited || process.ExitCode != WIT_TEST_EXIT_CODE) {
        wit_console_write("Wait-any mode/state/code: ");
        wit_console_write_u64(mode);
        wit_console_write("/");
        wit_console_write_u64(process.State);
        wit_console_write("/");
        wit_console_write_u64(process.ExitCode);
        wit_console_write("\n");
        wit_panic("PAL wait-any contract failed");
    }
    require(report && report[0] == mode && report[1] == 1, "Wait-any checks incomplete");
    const WitU64 workers = mode == 50 ? 0 : (mode >= 54 && mode != 56) ? 1 : 2;
    require(process.ThreadCreates == workers + 1 &&
            process.ThreadJoins == workers &&
            process.ThreadReaps == workers &&
            process.Space.OwnedCount == owned &&
            !process.Handles.Count &&
            !process.Events.Count,
        "Wait-any resource lifecycle failed");
    if (workers && mode != 54) {
        require(process.EventParks == workers, "Wait-any worker did not park exactly once");
    }
    if (mode == 54) {
        require(process.EventParks <= 1, "Finite wait parked more than once");
    }
    if (mode == 53) {
        require(process.WaitCloses == 2 && !process.EventWakes, "Close did not cancel the original wait generation");
    } else if (mode == 54) {
        require(process.WaitTimeouts == 1 && !process.EventWakes, "Deadline did not win before a later signal");
    } else if (workers) {
        require(process.EventWakes == workers, "Wait-any signals did not complete each waiter once");
    }
    for (WitU32 i = 0; i < WIT_USER_RESERVATION_CAPACITY; ++i) {
        require(!process.Space.Reservations[i].Size, "Wait-any reservation leak");
    }
    wit_user_destroy(&process);
    require(wit_pages_free_count(pages) == before, "Wait-any teardown leaked pages");
}

static void deadline_order(WitPageAllocator *pages)
{
    // Exact deadline boundary without wall-clock timing assumptions. Use a real
    // component, user buffer, event table and saved user context, but no user run.
    const WitU64 before = wit_pages_free_count(pages);
    WitU64 handle, index = 99;
    require(wit_user_create_pe(
                &process, pages, 0, wit_pal_plain_image, sizeof(wit_pal_plain_image), WIT_USER_IMAGE_BASE) == WitPeOk,
        "Deadline fixture load failed");
    require(wit_event_create(&process.Events, &process.Handles, 0, WIT_RIGHT_WAIT | WIT_RIGHT_SIGNAL, &handle) ==
            WIT_STATUS_OK,
        "Deadline fixture event creation failed");
    *(WitU64 *)wit_user_space_physical(&process.Space, WIT_GC_INFO_REPORT, 1, 0) = handle;
    require(wit_user_event_wait_any_until(&process, WIT_GC_INFO_REPORT, 1, 10, 1, &index) == WIT_STATUS_OK && !index,
        "Deadline wait did not publish");
    wit_user_wait_expire(&process, 1000); // Legacy ticks must not expire a monotonic wait.
    require(process.Threads[0].State == WitThreadWaiting, "Mixed deadline domains");
    wit_user_wait_expire_time(&process, 10);
    require(process.Threads[0].State == WitThreadReady &&
            process.Threads[0].Context->Rax == WIT_STATUS_TIMED_OUT &&
            !process.Threads[0].WaitCount &&
            process.WaitTimeouts == 1,
        "Wait-any did not expire at its exact deadline");
    require(wit_user_event_set(&process, handle) == WIT_STATUS_OK &&
            !process.EventWakes &&
            process.Threads[0].Context->Rax == WIT_STATUS_TIMED_OUT,
        "Late signal replaced timeout completion");
    require(wit_user_event_wait_any_until(&process, WIT_GC_INFO_REPORT, 1, 0, 10, &index) == WIT_STATUS_OK && !index,
        "Late signal was lost instead of retained for the next wait");
    wit_user_destroy(&process);
    require(wit_pages_free_count(pages) == before, "Deadline state-test teardown leaked pages");
}

void wit_user_wait_any_self_test(WitPageAllocator *pages)
{
    run(pages, 50, WIT_USER_IMAGE_BASE, 0);
    run(pages, 50, WIT_USER_IMAGE_ALTERNATE, 1);
    wit_console_write("[TEST-PASS] User.WaitAnyValidation\n");
    run(pages, 51, WIT_USER_IMAGE_BASE, 0);
    wit_console_write("[TEST-PASS] User.WaitAnyAutoReset\n");
    run(pages, 52, WIT_USER_IMAGE_BASE, 0);
    wit_console_write("[TEST-PASS] User.WaitAnyManualAndReuse\n");
    run(pages, 53, WIT_USER_IMAGE_BASE, 0);
    wit_console_write("[TEST-PASS] User.WaitAnyClose\n");
    deadline_order(pages);
    run(pages, 54, WIT_USER_IMAGE_BASE, 0);
    wit_console_write("[TEST-PASS] User.WaitAnyDeadline\n");
    run(pages, 55, WIT_USER_IMAGE_BASE, 0);
    wit_console_write("[TEST-PASS] User.WaitAnySnapshot\n");
    run(pages, 56, WIT_USER_IMAGE_BASE, 0);
    run(pages, 57, WIT_USER_IMAGE_BASE, 0);
    wit_console_write("[TEST-PASS] User.WaitAnySingleAndMixed\n");
}
