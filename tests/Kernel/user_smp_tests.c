#include "user.h"
#include "witos/platform.h"
#include "witos/processor.h"
#include "protocol.h"
#include "self_test.h"
#include "user_smp_image.h"

/* Threads on every processor (plan step P1.3b) from user mode on the three ISAs, with the processors the boot names:
 * two spinning threads together see every online processor (two at most), and a reader another processor runs
 * faults on a page the first thread took the access of, which it reaches only through the shootdown. With one
 * processor the same fixture sees processor 0 and the reader faults after a tick hands it the processor. */

static WitUserProcess process;

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

static WitU64 data(WitU32 offset)
{
    return *(const WitU64 *)wit_user_space_physical(&process.Space, WIT_USER_DATA + offset, 0, 0);
}

static void run(WitPageAllocator *pages, WitU64 mode)
{
    require(wit_test_create_fixture(&process, pages, 0, wit_user_smp_image, sizeof(wit_user_smp_image)),
        "SMP test component creation failed");
    WitUserTestConfig *const config = (WitUserTestConfig *)wit_user_space_physical(&process.Space, WIT_USER_INFO, 0, 0);
    config->Mode = mode;
    wit_user_run(&process);
}

void wit_user_smp_self_test(WitPageAllocator *pages)
{
    const WitU64 before = wit_pages_free_count(pages);
    run(pages, WIT_SMP_TEST_SPIN);
    if (process.State != WitUserExited || process.ExitCode != WIT_TEST_EXIT_CODE) {
        wit_console_write("SMP test state/code: ");
        wit_console_write_u64(process.State);
        wit_console_write("/");
        wit_console_write_u64(process.ExitCode);
        wit_console_write(" last status: ");
        wit_console_write_u64(data(1304));
        wit_console_write(" checks passed: ");
        wit_console_write_u64(data(1312));
        wit_console_write(" processors seen: ");
        wit_console_write_hex(data(1400));
        wit_console_write("\n");
        wit_panic("User SMP test failed");
    }
    const WitU64 seen = data(1400);
    require(seen == (wit_processors_scheduling() > 1 ? 3U : 1U), "The threads did not run on every processor");
    wit_console_write("User threads saw processors: ");
    wit_console_write_hex(seen);
    wit_console_write("\n");
    wit_user_destroy(&process);
    wit_console_write("[TEST-PASS] User.SmpThreads\n");

    run(pages, WIT_SMP_TEST_SHOOTDOWN);
    require(wit_test_faulted(&process) && process.FaultThread == 1 && process.FaultAddress == data(1408),
        "The reader did not fault on the page the first thread took away");
    wit_user_destroy(&process);
    wit_console_write("[TEST-PASS] User.SmpShootdown\n");

    /* Affinity (P1.4): the thread pinned to each of the first two online processors stays there. */
    run(pages, WIT_SMP_TEST_AFFINITY);
    if (process.State != WitUserExited || process.ExitCode != WIT_TEST_EXIT_CODE) {
        wit_console_write("SMP affinity state/code: ");
        wit_console_write_u64(process.State);
        wit_console_write("/");
        wit_console_write_u64(process.ExitCode);
        wit_console_write(" last status: ");
        wit_console_write_u64(data(1304));
        wit_console_write(" checks passed: ");
        wit_console_write_u64(data(1312));
        wit_console_write("\n");
        wit_panic("A pinned thread left its processor or an affinity was mishandled");
    }
    wit_user_destroy(&process);
    require(wit_pages_free_count(pages) == before, "SMP scenarios leaked physical pages");
    wit_console_write("[TEST-PASS] User.SmpAffinity\n");
}
