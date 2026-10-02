#include "x64.h"
#include "user.h"
#include "witos/platform.h"
#include "protocol.h"
#include "dynamic_tls_image.h"

static WitUserProcess process;

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

static void run(WitPageAllocator *pages, WitU64 base, WitU64 mode)
{
    const WitU64 before = wit_pages_free_count(pages);
    WitUserTestConfig *config;
    WitU32 owned;
    const WitPeStatus status =
        wit_user_create_pe(&process, pages, 0, wit_dynamic_tls_image, sizeof(wit_dynamic_tls_image), base);
    if (status != WitPeOk) {
        wit_console_write("Dynamic TLS image status: ");
        wit_console_write_u64(status);
        wit_console_write("\n");
        wit_panic("Dynamic TLS image rejected");
    }
    config = (WitUserTestConfig *)wit_user_space_physical(&process.Space, WIT_USER_INFO, 0, 0);
    config->Mode = mode;
    owned = process.Space.OwnedCount;
    if (mode == 9) {
        const WitU64 target = wit_user_space_physical(&process.Space, base + WIT_DYNAMIC_TLS_INITIALIZER_RVA, 0, 0);
        require(target != 0, "Dynamic TLS initializer address missing");
        *(WitU64 *)target = WIT_USER_DATA; // Writable/NX address must be rejected before invoking any initializer.
    }
    wit_user_run(&process);
    if (mode == 0 || mode == 1 || mode == 8) {
        if (process.State != WitUserExited || process.ExitCode != WIT_TEST_EXIT_CODE) {
            wit_console_write("Dynamic TLS state/code: ");
            wit_console_write_u64(process.State);
            wit_console_write("/");
            wit_console_write_u64(process.ExitCode);
            wit_console_write("\n");
            wit_panic("Dynamic C++ TLS lifecycle failed");
        }
        require(process.ThreadCreates == 6 &&
                process.ThreadJoins == 5 &&
                process.ThreadReaps == 5 &&
                process.ThreadSwitches >= 4 &&
                process.Space.OwnedCount == owned,
            "Dynamic TLS thread/heap resources leaked");
        for (WitU32 i = 0; i < WIT_USER_RESERVATION_CAPACITY; ++i) {
            require(!process.Space.Reservations[i].Size, "TLS constructors/destructors leaked heap reservations");
        }
    } else {
        const WitU64 report = wit_user_space_physical(&process.Space, WIT_GC_INFO_REPORT, 0, 0);
        require(report && *(const WitU64 *)report == mode, "Dynamic TLS failure occurred before intended boundary");
        if (mode == 5) {
            require(process.State == WitUserFaulted &&
                    process.FaultVector == 14 &&
                    process.FaultError == 6 &&
                    process.FaultAddress == 0 &&
                    process.FaultState.Cs == WIT_USER_CS &&
                    process.FaultState.Ss == WIT_USER_SS,
                "TLS destructor fault was not contained");
        } else {
            require(process.State == WitUserExited && process.ExitCode == WIT_GC_TEST_FAIL_FAST_EXIT,
                "Dynamic TLS misuse did not fail fast");
        }
        if (mode == 9) {
            require(process.Space.OwnedCount == owned, "Bad initializer ran before validation finished");
        }
    }
    require(!process.Handles.Count && !process.Events.Count, "Dynamic TLS handles leaked");
    wit_user_destroy(&process);
    require(wit_pages_free_count(pages) == before, "Dynamic TLS component teardown leaked physical pages");
}

void wit_user_dynamic_tls_self_test(WitPageAllocator *pages)
{
    run(pages, WIT_USER_IMAGE_BASE, 0);
    run(pages, WIT_USER_IMAGE_ALTERNATE, 0);
    wit_console_write("[TEST-PASS] User.DynamicTlsLifecycle\n");
    run(pages, WIT_USER_IMAGE_BASE, 1);
    wit_console_write("[TEST-PASS] User.DynamicTlsExplicitExit\n");
    run(pages, WIT_USER_IMAGE_BASE, 8);
    wit_console_write("[TEST-PASS] User.DynamicTlsDestructorOrder\n");
    for (WitU64 mode = 2; mode <= 7; ++mode) {
        if (mode != 5) {
            run(pages, WIT_USER_IMAGE_BASE, mode);
        }
    }
    run(pages, WIT_USER_IMAGE_BASE, 9);
    run(pages, WIT_USER_IMAGE_BASE, 10);
    run(pages, WIT_USER_IMAGE_BASE, 0);
    wit_console_write("[TEST-PASS] User.DynamicTlsFailFast\n");
    run(pages, WIT_USER_IMAGE_BASE, 5);
    run(pages, WIT_USER_IMAGE_BASE, 0);
    wit_console_write("[TEST-PASS] User.DynamicTlsFaultIsolation\n");
}
