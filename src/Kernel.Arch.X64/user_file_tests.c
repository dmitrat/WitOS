#include "x64.h"
#include "user.h"
#if defined(WITOS_TEST_CORECLR_STORAGE)
#include "witos/platform.h"
#include "witos/storage.h"
#include "coreclr_storage_image.h"
#include "process_exit_image.h"
#include "storage_manifest.h"
#include "protocol.h"
static WitUserProcess process, peer;

static void require(int ok, const char *message)
{
    if (!ok) {
        wit_panic(message);
    }
}

void wit_user_file_self_test(WitPageAllocator *pages)
{
    const WitU64 before = wit_pages_free_count(pages);
    WitU64 stale = 0, total = 0;
    for (WitU32 i = 0; i < WIT_STORAGE_FILE_COUNT; ++i) {
        total += wit_storage_files[i].Bytes;
    }
    for (WitU32 round = 0; round < 2; ++round) {
        const WitPeStatus loaded = wit_user_create_pe_profile(&process, pages, 0, wit_storage_image,
            sizeof(wit_storage_image), WIT_USER_IMAGE_BASE, "boot:/CoreClrStorageFixture.pe", WIT_PE_UNWIND_RUNTIME);
        if (loaded != WitPeOk) {
            wit_console_write("Storage PE status: ");
            wit_console_write_u64(loaded);
            wit_console_write("\n");
        }
        require(loaded == WitPeOk, "Storage fixture load failed");
        require(wit_user_create_pe_profile(&peer, pages, 1, wit_storage_image, sizeof(wit_storage_image),
                    WIT_USER_IMAGE_BASE, "boot:/CoreClrStorageFixture.pe", WIT_PE_UNWIND_RUNTIME) == WitPeOk,
            "Storage peer load failed");
        const char path[] = "app/CoreClrProbe.dll";
        WitU64 foreign = 0;
        require(wit_file_open(&peer.Files, &peer.Handles, wit_storage_package(), (const WitU8 *)path, sizeof(path) - 1,
                    &foreign) == WIT_STATUS_OK,
            "Storage foreign handle setup failed");
        WitUserTestConfig *config = (WitUserTestConfig *)wit_user_space_physical(&process.Space, WIT_USER_INFO, 0, 0);
        config->ForeignHandle = round ? stale : foreign;
        // This profile streams the complete framework twice; ordinary component
        // and M3 limits are unchanged. Record actual use against the 30 s budget.
        process.TickLimit = WIT_RUNTIME_TICK_BUDGET;
        const WitU32 owned = process.Space.OwnedCount;
        wit_user_run(&process);
        const WitU64 *report = (const WitU64 *)wit_user_space_physical(&process.Space, WIT_GC_INFO_REPORT, 0, 0);
        if (process.State != WitUserExited || process.ExitCode != 42) {
            wit_console_write("Storage state/code/ticks: ");
            wit_console_write_u64(process.State);
            wit_console_write("/");
            wit_console_write_u64(process.ExitCode);
            wit_console_write("/");
            wit_console_write_u64(process.Ticks);
            wit_console_write("\n");
            wit_panic("Guest storage acceptance failed");
        }
        require(report &&
                report[0] == WIT_STORAGE_FILE_COUNT &&
                report[1] == total &&
                report[2] == process.Handles.Limit - 2 &&
                report[3],
            "Storage payload/quota report mismatch");
        stale = report[3];
        require(!process.Handles.Count && process.Space.OwnedCount == owned,
            "File IO consumed owned pages or retained handles");
        for (WitU32 i = 0; i < WIT_RUNTIME_HANDLE_CAPACITY; ++i) {
            require(!process.Files.Entries[i].Token, "File cursor survived component exit");
        }
        wit_console_write("Storage files/bytes/ticks: ");
        wit_console_write_u64(report[0]);
        wit_console_write("/");
        wit_console_write_u64(total);
        wit_console_write("/");
        wit_console_write_u64(process.Ticks);
        wit_console_write("\n");
        require(wit_file_close(&peer.Files, &peer.Handles, foreign) == WIT_STATUS_OK, "File peer cleanup failed");
        wit_user_destroy(&process);
        wit_user_destroy(&peer);
        require(wit_pages_free_count(pages) == before, "Storage component teardown leaked pages");
    }
    for (WitU32 mode = 1; mode <= 3; ++mode) {
        require(wit_user_create_pe_profile(&process, pages, 0, wit_storage_image, sizeof(wit_storage_image),
                    WIT_USER_IMAGE_BASE, "boot:/CoreClrStorageFixture.pe", WIT_PE_UNWIND_RUNTIME) == WitPeOk,
            "Storage isolation fixture failed");
        WitUserTestConfig *config = (WitUserTestConfig *)wit_user_space_physical(&process.Space, WIT_USER_INFO, 0, 0);
        config->Mode = mode;
        config->KernelProbe = (WitU64)wit_storage_package()->Data;
        wit_user_run(&process);
        require(process.State == WitUserFaulted &&
                process.FaultVector == 14 &&
                process.FaultState.Cs == WIT_USER_CS &&
                process.FaultAddress == (WitU64)wit_storage_package()->Data &&
                process.FaultError ==
                    (mode == 1          ? 5ULL
                            : mode == 2 ? 7ULL
                                        : 21ULL),
            "Storage supervisor/NX mapping escaped isolation");
        require(wit_storage_package()->Data[0] == 'W', "User modified immutable package");
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Storage isolation leaked pages");
    }
    for (WitU32 mode = 4; mode <= 5; ++mode) {
        require(wit_user_create_pe_profile(&process, pages, 0, wit_storage_image, sizeof(wit_storage_image),
                    WIT_USER_IMAGE_BASE, "boot:/CoreClrStorageFixture.pe", WIT_PE_UNWIND_RUNTIME) == WitPeOk,
            "File view fault fixture load failed");
        ((WitUserTestConfig *)wit_user_space_physical(&process.Space, WIT_USER_INFO, 0, 0))->Mode = mode;
        wit_user_run(&process);
        const WitU64 *report = (const WitU64 *)wit_user_space_physical(&process.Space, WIT_GC_INFO_REPORT, 0, 0);
        require(report &&
                report[4] &&
                process.State == WitUserFaulted &&
                process.FaultVector == 14 &&
                process.FaultState.Cs == WIT_USER_CS &&
                process.FaultAddress == report[4] &&
                process.FaultError == (mode == 4 ? 7ULL : 21ULL),
            "File view protection did not fault");
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "File view fault leaked pages");
    }
    WitU32 failures = 0;
    int recovered = 0;
    for (WitU32 extra = 0; extra < 20; ++extra) {
        require(wit_user_create_pe_profile(&process, pages, 0, wit_storage_image, sizeof(wit_storage_image),
                    WIT_USER_IMAGE_BASE, "boot:/CoreClrStorageFixture.pe", WIT_PE_UNWIND_RUNTIME) == WitPeOk,
            "File view rollback fixture load failed");
        ((WitUserTestConfig *)wit_user_space_physical(&process.Space, WIT_USER_INFO, 0, 0))->Mode = 6;
        process.Space.PageLimit = process.Space.OwnedCount + extra;
        wit_user_run(&process);
        require(process.State == WitUserExited && (process.ExitCode == 42 || process.ExitCode == 43),
            "File view rollback failed");
        if (process.ExitCode == 43) {
            ++failures;
            require(process.MemoryCommitFailures == 1, "File view did not reach commit failure");
        } else {
            recovered = 1;
        }
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "File view rollback leaked pages");
        if (recovered) {
            break;
        }
    }
    require(recovered && failures >= 3, "File view rollback did not cover backing/table boundaries");
    failures = 0;
    recovered = 0;
    for (WitU32 extra = 0; extra < 32; ++extra) {
        require(wit_user_create_pe_profile(&process, pages, 0, wit_storage_image, sizeof(wit_storage_image),
                    WIT_USER_IMAGE_BASE, "boot:/CoreClrStorageFixture.pe", WIT_PE_UNWIND_RUNTIME) == WitPeOk,
            "Library rollback fixture load failed");
        ((WitUserTestConfig *)wit_user_space_physical(&process.Space, WIT_USER_INFO, 0, 0))->Mode = 7;
        process.Space.PageLimit = process.Space.OwnedCount + extra;
        wit_user_run(&process);
        if (process.State != WitUserExited || (process.ExitCode != 42 && process.ExitCode != 43)) {
            wit_console_write("Library rollback code: ");
            wit_console_write_u64(process.ExitCode);
            wit_console_write("\n");
            wit_panic("Library rollback failed");
        }
        if (process.ExitCode == 43) {
            ++failures;
        } else {
            recovered = 1;
        }
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Library rollback leaked pages");
        if (recovered) {
            break;
        }
    }
    require(recovered && failures >= 3, "Library rollback missed backing/table boundaries");
    require(wit_user_create_pe_profile(&process, pages, 0, wit_storage_image, sizeof(wit_storage_image),
                WIT_USER_IMAGE_BASE, "boot:/CoreClrStorageFixture.pe", WIT_PE_UNWIND_RUNTIME) == WitPeOk,
        "Library protection fixture load failed");
    ((WitUserTestConfig *)wit_user_space_physical(&process.Space, WIT_USER_INFO, 0, 0))->Mode = 8;
    wit_user_run(&process);
    const WitU64 *libraryReport = (const WitU64 *)wit_user_space_physical(&process.Space, WIT_GC_INFO_REPORT, 0, 0);
    require(libraryReport &&
            libraryReport[4] &&
            process.State == WitUserFaulted &&
            process.FaultVector == 14 &&
            process.FaultState.Cs == WIT_USER_CS &&
            process.FaultAddress == libraryReport[4] &&
            process.FaultError == 7,
        "Library RX write did not fault");
    wit_user_destroy(&process);
    require(wit_pages_free_count(pages) == before, "Library fault teardown leaked pages");
    failures = 0;
    recovered = 0;
    for (WitU32 extra = 0; extra < 48; ++extra) {
        require(wit_user_create_pe_profile(&process, pages, 0, wit_storage_image, sizeof(wit_storage_image),
                    WIT_USER_IMAGE_BASE, "boot:/CoreClrStorageFixture.pe", WIT_PE_UNWIND_RUNTIME) == WitPeOk,
            "Library graph rollback load failed");
        ((WitUserTestConfig *)wit_user_space_physical(&process.Space, WIT_USER_INFO, 0, 0))->Mode = 9;
        process.Space.PageLimit = process.Space.OwnedCount + extra;
        wit_user_run(&process);
        if (process.State != WitUserExited || (process.ExitCode != 42 && process.ExitCode != 43)) {
            wit_console_write("Graph rollback code: ");
            wit_console_write_u64(process.ExitCode);
            wit_panic("Library graph rollback failed");
        }
        if (process.ExitCode == 43) {
            ++failures;
        } else {
            recovered = 1;
        }
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Library graph rollback leaked pages");
        if (recovered) {
            break;
        }
    }
    require(recovered && failures >= 8, "Library graph rollback missed multi-image backing boundaries");
    for (WitU32 mode = 10; mode <= 11; ++mode) {
        require(wit_user_create_pe_profile(&process, pages, 0, wit_storage_image, sizeof(wit_storage_image),
                    WIT_USER_IMAGE_BASE, "boot:/CoreClrStorageFixture.pe", WIT_PE_UNWIND_RUNTIME) == WitPeOk,
            "Library graph lifecycle load failed");
        ((WitUserTestConfig *)wit_user_space_physical(&process.Space, WIT_USER_INFO, 0, 0))->Mode = mode;
        if (mode == 11) {
            process.Handles.Limit = process.Handles.Count + 1;
        }
        wit_user_run(&process);
        if (mode == 10) {
            const WitU64 *report = (const WitU64 *)wit_user_space_physical(&process.Space, WIT_GC_INFO_REPORT, 0, 0);
            require(report &&
                    report[4] &&
                    process.State == WitUserFaulted &&
                    process.FaultVector == 14 &&
                    process.FaultState.Cs == WIT_USER_CS &&
                    process.FaultAddress == report[4] &&
                    process.FaultError == 7,
                "Library IAT write did not fault");
        } else {
            require(process.State == WitUserExited && process.ExitCode == 43,
                "Library graph partial-handle rollback failed");
        }
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Library graph lifecycle leaked pages");
    }
    failures = 0;
    recovered = 0;
    for (WitU32 extra = 0; extra < 48; ++extra) {
        require(wit_user_create_pe_profile(&process, pages, 0, wit_storage_image, sizeof(wit_storage_image),
                    WIT_USER_IMAGE_BASE, "boot:/CoreClrStorageFixture.pe", WIT_PE_UNWIND_RUNTIME) == WitPeOk,
            "DLL lifecycle rollback fixture load failed");
        ((WitUserTestConfig *)wit_user_space_physical(&process.Space, WIT_USER_INFO, 0, 0))->Mode = 12;
        process.Space.PageLimit = process.Space.OwnedCount + extra;
        wit_user_run(&process);
        if (process.State != WitUserExited || (process.ExitCode != 42 && process.ExitCode != 43)) {
            wit_console_write("DLL lifecycle code: ");
            wit_console_write_u64(process.ExitCode);
            wit_panic("DLL lifecycle rollback failed");
        }
        if (process.ExitCode == 43) {
            ++failures;
        } else {
            recovered = 1;
        }
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "DLL lifecycle rollback leaked pages");
        if (recovered) {
            break;
        }
    }
    require(recovered && failures >= 10, "DLL lifecycle rollback missed plan/backing boundaries");
    require(wit_user_create_pe_profile(&process, pages, 0, wit_process_exit_image, sizeof(wit_process_exit_image),
                WIT_USER_IMAGE_BASE, "boot:/ProcessExitFixture.pe", WIT_PE_UNWIND_RUNTIME) == WitPeOk,
        "DLL CRT shutdown fixture load failed");
    ((WitUserTestConfig *)wit_user_space_physical(&process.Space, WIT_USER_INFO, 0, 0))->Mode = 16;
    wit_user_run(&process);
    const WitU64 *shutdown = (const WitU64 *)wit_user_space_physical(&process.Space, WIT_GC_INFO_REPORT, 0, 0);
    require(process.State == WitUserExited &&
            process.ExitCode == 42 &&
            shutdown &&
            shutdown[4] == 1 &&
            shutdown[5] == 1 &&
            shutdown[7] == 1 &&
            shutdown[8] == 7311584 &&
            shutdown[9] == 1,
        "DLL CRT shutdown ordering failed");
    wit_user_destroy(&process);
    require(wit_pages_free_count(pages) == before, "DLL CRT shutdown leaked pages");
    for (WitU32 mode = 17; mode <= 19; ++mode) {
        require(wit_user_create_pe_profile(&process, pages, 0, wit_process_exit_image, sizeof(wit_process_exit_image),
                    WIT_USER_IMAGE_BASE, "boot:/ProcessExitFixture.pe", WIT_PE_UNWIND_RUNTIME) == WitPeOk,
            "DLL thread notification fixture load failed");
        ((WitUserTestConfig *)wit_user_space_physical(&process.Space, WIT_USER_INFO, 0, 0))->Mode = mode;
        wit_user_run(&process);
        const WitU64 *report = (const WitU64 *)wit_user_space_physical(&process.Space, WIT_GC_INFO_REPORT, 0, 0);
        if (mode == 17) {
            require(process.State == WitUserExited &&
                    process.ExitCode == 42 &&
                    report &&
                    report[10] == 4 &&
                    report[12] == 4 &&
                    report[13] == 1,
                "DLL thread notification order/identity failed");
        } else {
            require(process.State == WitUserExited &&
                    process.ExitCode == WIT_PROCESS_ABRUPT_THREAD_EXIT &&
                    report &&
                    report[10] == 1 &&
                    !report[12] &&
                    report[11] == process.AbruptThreadId &&
                    process.AbruptThreadCode == 77,
                "DLL thread cleanup bypass resumed peers");
        }
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "DLL thread notification teardown leaked");
    }
    failures = 0;
    recovered = 0;
    for (WitU32 extra = 0; extra < 48; ++extra) {
        require(wit_user_create_pe_profile(&process, pages, 0, wit_storage_image, sizeof(wit_storage_image),
                    WIT_USER_IMAGE_BASE, "boot:/CoreClrStorageFixture.pe", WIT_PE_UNWIND_RUNTIME) == WitPeOk,
            "DLL TLS rollback fixture load failed");
        ((WitUserTestConfig *)wit_user_space_physical(&process.Space, WIT_USER_INFO, 0, 0))->Mode = 13;
        process.Space.PageLimit = process.Space.OwnedCount + extra;
        wit_user_run(&process);
        if (process.State != WitUserExited || (process.ExitCode != 42 && process.ExitCode != 43)) {
            wit_console_write("DLL TLS rollback code: ");
            wit_console_write_u64(process.ExitCode);
            wit_panic("DLL TLS rollback failed");
        }
        if (process.ExitCode == 43) {
            ++failures;
        } else {
            recovered = 1;
        }
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "DLL TLS rollback leaked pages");
        if (recovered) {
            break;
        }
    }
    require(recovered && failures >= 12, "DLL TLS rollback missed header/block/image boundaries");
    failures = 0;
    recovered = 0;
    WitU32 childFailures = 0;
    for (WitU32 extra = 0; extra < 64; ++extra) {
        require(wit_user_create_pe_profile(&process, pages, 0, wit_process_exit_image, sizeof(wit_process_exit_image),
                    WIT_USER_IMAGE_BASE, "boot:/ProcessExitFixture.pe", WIT_PE_UNWIND_RUNTIME) == WitPeOk,
            "DLL TLS main/worker rollback fixture load failed");
        WitUserTestConfig *configuration =
            (WitUserTestConfig *)wit_user_space_physical(&process.Space, WIT_USER_INFO, 0, 0);
        configuration->Mode = 21;
        configuration->KernelProbe = extra;
        wit_user_run(&process);
        const WitU64 *report = (const WitU64 *)wit_user_space_physical(&process.Space, WIT_GC_INFO_REPORT, 0, 0);
        if (process.State != WitUserExited || (process.ExitCode != 42 && process.ExitCode != 43)) {
            wit_console_write("DLL TLS worker code: ");
            wit_console_write_u64(process.ExitCode);
            wit_panic("DLL TLS worker rollback failed");
        }
        if (process.ExitCode == 43) {
            ++failures;
            if (report && report[14] == 2) {
                ++childFailures;
            }
        } else {
            recovered = 1;
        }
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "DLL TLS worker rollback leaked pages");
        if (recovered) {
            break;
        }
    }
    require(recovered && failures == 20 && childFailures == 20,
        "DLL TLS worker rollback missed stack/header/module block boundaries");
    wit_console_write(
        "[TEST-PASS] Storage.AssemblyBytes\n[TEST-PASS] Storage.AtomicReadAndSeek\n[TEST-PASS] "
        "Storage.HandlesAndQuotas\n[TEST-PASS] Storage.NamespaceQueries\n[TEST-PASS] Storage.FileViews\n[TEST-PASS] "
        "Storage.CoreHostPalFiles\n[TEST-PASS] Storage.NativePaths\n[TEST-PASS] Storage.NativeDirectories\n[TEST-PASS] "
        "Storage.NativeLibraries\n[TEST-PASS] Storage.LibraryRollback\n[TEST-PASS] "
        "Storage.LibraryDependencies\n[TEST-PASS] Storage.LibraryReaders\n[TEST-PASS] "
        "Storage.LibraryLifecycle\n[TEST-PASS] Storage.LibraryShutdown\n[TEST-PASS] "
        "Storage.LibraryThreadNotifications\n[TEST-PASS] Storage.LibraryStaticTls\n[TEST-PASS] "
        "Storage.FileViewRollback\n[TEST-PASS] Storage.Isolation\n[TEST-PASS] Storage.Teardown\n");
}
#else
void wit_user_file_self_test(WitPageAllocator *pages)
{
    (void)pages;
}
#endif
