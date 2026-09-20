#include "user.h"
#include "witos/platform.h"
#include "protocol.h"
#include "pal_image.h"

static WitUserProcess process;
static void require(int condition, const char *message) { if (!condition) wit_panic(message); }
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
    require(report && report->Version == WIT_THREAD_INFO_VERSION && report->Size == sizeof(*report) &&
        report->ThreadId == process.Threads[0].Handle && report->StackLow == process.Threads[0].StackBottom &&
        report->StackHigh == process.Threads[0].StackTop && report->RawTls == process.Threads[0].Tls &&
        report->CompilerTls == process.Threads[0].CompilerTls && (report->CompilerTls != 0) == tls &&
        report->ProcessId == process.Id && report->ProcessorCount == 1, "Guest thread snapshot differs from kernel records");
    if (!mode) {
        if (process.State != WitUserExited || process.ExitCode != WIT_TEST_EXIT_CODE) {
            wit_console_write("PAL state/code: "); wit_console_write_u64(process.State);
            wit_console_write("/"); wit_console_write_u64(process.ExitCode); wit_console_write("\n");
            wit_panic("PAL thread contract failed");
        }
        require(process.ThreadCreates == 4 && process.ThreadJoins == 3 && process.ThreadReaps == 3 &&
            process.ThreadTimerSwitches > 0 && process.IdleHalts > 0 && process.Space.OwnedCount == owned,
            "PAL thread preemption/reuse/accounting failed");
    } else require(process.State == WitUserFaulted && process.FaultVector == 14 && process.FaultError == 6 &&
        process.FaultAddress == (mode == 2 ? report->StackLow - 1 : report->StackHigh) &&
        process.FaultCs == WIT_USER_CS && process.FaultSs == WIT_USER_SS, "Reported PAL stack bounds include a guard page");
    require(!process.Handles.Count && !process.Events.Count, "PAL query leaked handles");
    wit_user_destroy(&process);
    require(wit_pages_free_count(pages) == before, "PAL fixture teardown leaked memory");
}
void wit_user_pal_self_test(WitPageAllocator *pages)
{
    run(pages, 0, WIT_USER_IMAGE_BASE, 0);
    run(pages, 1, WIT_USER_IMAGE_BASE, 0);
    run(pages, 1, WIT_USER_IMAGE_ALTERNATE, 0);
    wit_console_write("[TEST-PASS] User.PalThreadSnapshot\n[TEST-PASS] User.PalThreadBuffers\n[TEST-PASS] User.PalThreadSwitching\n");
    run(pages, 1, WIT_USER_IMAGE_BASE, 2);
    run(pages, 1, WIT_USER_IMAGE_BASE, 3);
    run(pages, 0, WIT_USER_IMAGE_BASE, 0);
    wit_console_write("[TEST-PASS] User.PalStackGuards\n");
}
