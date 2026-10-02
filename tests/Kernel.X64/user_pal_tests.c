#include "x64.h"
#include "user.h"
#include "witos/platform.h"
#include "protocol.h"
#include "pal_image.h"

static WitUserProcess process;
static WitU32 last_native_id;

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

static void run(WitPageAllocator *pages, int tls, WitU64 base, WitU64 mode)
{
    const WitU64 before = wit_pages_free_count(pages);
    const WitU8 *file = tls ? wit_pal_tls_image : wit_pal_plain_image;
    const WitU32 size = tls ? sizeof(wit_pal_tls_image) : sizeof(wit_pal_plain_image);
    WitU32 owned;
    WitUserTestConfig *config;
    const WitUserThreadInfo *report;
    require(wit_user_create_pe(&process, pages, 0, file, size, base) == WitPeOk, "PAL fixture load failed");
    config = (WitUserTestConfig *)wit_user_space_physical(&process.Space, WIT_USER_INFO, 0, 0);
    config->Mode = mode;
    config->KernelProbe = (WitU64)file;
    owned = process.Space.OwnedCount;
    wit_user_run(&process);
    report = (const WitUserThreadInfo *)wit_user_space_physical(&process.Space, WIT_GC_INFO_REPORT, 0, 0);
    require(report &&
            report->Version == WIT_THREAD_INFO_VERSION &&
            report->Size == sizeof(*report) &&
            report->ThreadId == process.Threads[0].Handle &&
            report->NativeId == process.Threads[0].NativeId &&
            report->NativeId > last_native_id &&
            !report->Reserved &&
            report->StackLow == process.Threads[0].StackBottom &&
            report->StackHigh == process.Threads[0].StackTop &&
            report->RawTls == process.Threads[0].Tls &&
            report->CompilerTls == process.Threads[0].CompilerTls &&
            (report->CompilerTls != 0) == tls &&
            report->ProcessId == process.Id &&
            report->ProcessorCount == 1,
        "Guest thread snapshot differs from kernel records");
    last_native_id = report->NativeId;
    if (!mode || mode == 4) {
        if (process.State != WitUserExited || process.ExitCode != WIT_TEST_EXIT_CODE) {
            wit_console_write("PAL state/code: ");
            wit_console_write_u64(process.State);
            wit_console_write("/");
            wit_console_write_u64(process.ExitCode);
            wit_console_write("\n");
            wit_panic("PAL thread contract failed");
        }
        require(process.ProcessWriteBarriers == 2, "PAL process barrier count or argument validation failed");
        const WitU64 *sleep = (const WitU64 *)(report + 1);
        const int valid_counts = process.ThreadCreates == 4 && process.ThreadJoins == 3 && process.ThreadReaps == 3;
        if (!valid_counts ||
            !process.ThreadTimerSwitches ||
            process.Space.OwnedCount != owned ||
            (mode == 4 && process.IdleHalts)) {
            wit_console_write("PAL mode/tls/create/join/reap/timer/idle/owned/expected: ");
            wit_console_write_u64(mode);
            wit_console_write("/");
            wit_console_write_u64((WitU64)tls);
            wit_console_write("/");
            wit_console_write_u64(process.ThreadCreates);
            wit_console_write("/");
            wit_console_write_u64(process.ThreadJoins);
            wit_console_write("/");
            wit_console_write_u64(process.ThreadReaps);
            wit_console_write("/");
            wit_console_write_u64(process.ThreadTimerSwitches);
            wit_console_write("/");
            wit_console_write_u64(process.IdleHalts);
            wit_console_write("/");
            wit_console_write_u64(process.Space.OwnedCount);
            wit_console_write("/");
            wit_console_write_u64(owned);
            wit_console_write("\n");
        }
        require(valid_counts, "PAL thread create/join/reap accounting failed");
        require(process.ThreadTimerSwitches > 0, "PAL thread timer preemption missing");
        require(process.Space.OwnedCount == owned, "PAL thread resources leaked");
        require(sleep[0] && sleep[2] >= sleep[0] && sleep[2] >= sleep[1], "PAL sleep returned before its deadline");
        if (mode == 4) {
            require(sleep[1] >= sleep[0] && !process.IdleHalts, "Expired absolute sleep unexpectedly entered idle");
        }
    } else {
        require(process.State == WitUserFaulted &&
                process.FaultVector == 14 &&
                process.FaultError == 6 &&
                process.FaultAddress == (mode == 2 ? report->StackLow - 1 : report->StackHigh) &&
                process.FaultState.Cs == WIT_USER_CS &&
                process.FaultState.Ss == WIT_USER_SS,
            "Reported PAL stack bounds include a guard page");
    }
    require(!process.Handles.Count && !process.Events.Count, "PAL query leaked handles");
    wit_user_destroy(&process);
    require(wit_pages_free_count(pages) == before, "PAL fixture teardown leaked memory");
}

void wit_user_pal_self_test(WitPageAllocator *pages)
{
    run(pages, 0, WIT_USER_IMAGE_BASE, 0);
    run(pages, 1, WIT_USER_IMAGE_BASE, 0);
    run(pages, 1, WIT_USER_IMAGE_ALTERNATE, 0);
    wit_console_write(
        "[TEST-PASS] User.PalThreadSnapshot\n[TEST-PASS] User.PalThreadBuffers\n[TEST-PASS] User.PalThreadSwitching\n");
    run(pages, 0, WIT_USER_IMAGE_BASE, 4);
    run(pages, 1, WIT_USER_IMAGE_ALTERNATE, 4);
    wit_console_write("[TEST-PASS] User.PalExpiredSleep\n");
    run(pages, 1, WIT_USER_IMAGE_BASE, 2);
    run(pages, 1, WIT_USER_IMAGE_BASE, 3);
    run(pages, 0, WIT_USER_IMAGE_BASE, 0);
    wit_console_write("[TEST-PASS] User.PalStackGuards\n");
}
