#include "x64.h"
#include "user.h"
#include "witos/platform.h"
#include "protocol.h"
#include "pal_environment_image.h"
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
    require(wit_user_create_pe(
                &process, pages, 0, wit_pal_environment_image, sizeof(wit_pal_environment_image), base) == WitPeOk,
        "PAL environment fixture load failed");
    const WitU32 owned = process.Space.OwnedCount;
    WitUserTestConfig *config = (WitUserTestConfig *)wit_user_space_physical(&process.Space, WIT_USER_INFO, 0, 0);
    config->Mode = mode;
    wit_user_run(&process);
    const WitU64 *report = (const WitU64 *)wit_user_space_physical(&process.Space, WIT_GC_INFO_REPORT, 0, 0);
    require(report && report[0] == mode, "PAL environment fixture missed entry");
    if (process.State != WitUserExited || process.ExitCode != WIT_TEST_EXIT_CODE) {
        wit_console_write("PAL environment state/code: ");
        wit_console_write_u64(process.State);
        wit_console_write("/");
        wit_console_write_u64(process.ExitCode);
        wit_console_write("\n");
        wit_panic("PAL environment contract failed");
    }
    require(process.ThreadCreates == 4 && process.ThreadReaps == 3 && process.ThreadSwitches > 0,
        "PAL environment missed worker switching/reuse");
    require(process.Space.OwnedCount == owned && !process.Handles.Count && !process.Events.Count,
        "PAL environment fixture leaked owned resources");
    for (WitU32 i = 0; i < WIT_USER_RESERVATION_CAPACITY; ++i) {
        require(!process.Space.Reservations[i].Size, "PAL environment fixture leaked reservation");
    }
    wit_user_destroy(&process);
    require(wit_pages_free_count(pages) == before, "PAL environment teardown leaked physical memory");
}

void wit_user_pal_environment_self_test(WitPageAllocator *pages)
{
    run(pages, 0, WIT_USER_IMAGE_BASE);
    run(pages, 0, WIT_USER_IMAGE_ALTERNATE);
    run(pages, 1, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.PalEnvironment\n[TEST-PASS] User.PalEnvironmentValidation\n[TEST-PASS] "
                      "User.PalEnvironmentBlocks\n");
    wit_console_write("[TEST-PASS] User.PalUtf8Copy\n[TEST-PASS] User.PalEnvironmentThreads\n");
}
