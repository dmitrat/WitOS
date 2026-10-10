#include "user.h"
#include "witos/platform.h"
#include "protocol.h"
#include "self_test.h"
#include "user_processor_image.h"

/* PROCESSOR_QUERY and THREAD_AFFINITY (RFC 0011 section 7.9, plan step K7.1) from user mode on both ISAs: the
 * 4-byte form of the frozen line, the record form with the boot processor online and first, the refusals of a wrong
 * size and a foreign version, the thread's default affinity, a set within the table, an empty mask and a mask beyond
 * the table refused, and a handle without the right refused the set. */

static WitUserProcess process;

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

void wit_user_processor_self_test(WitPageAllocator *pages)
{
    const WitU64 before = wit_pages_free_count(pages);
    require(wit_test_create_fixture(&process, pages, 0, wit_user_processor_image, sizeof(wit_user_processor_image)),
        "Processor test component creation failed");
    const WitU32 owned = process.Space.OwnedCount;
    wit_user_run(&process);
    if (process.State != WitUserExited || process.ExitCode != WIT_TEST_EXIT_CODE) {
        wit_console_write("Processor test state/code: ");
        wit_console_write_u64(process.State);
        wit_console_write("/");
        wit_console_write_u64(process.ExitCode);
        wit_console_write(" last status: ");
        wit_console_write_u64(*(const WitU64 *)wit_user_space_physical(&process.Space, WIT_USER_DATA + 1304, 0, 0));
        wit_console_write(" checks passed: ");
        wit_console_write_u64(*(const WitU64 *)wit_user_space_physical(&process.Space, WIT_USER_DATA + 1312, 0, 0));
        wit_console_write("\n");
        wit_panic("User processor test failed");
    }
    require(process.Handles.Count == 0 && process.Space.OwnedCount == owned, "Processor scenario left resources");
    wit_user_destroy(&process);
    require(wit_pages_free_count(pages) == before, "Processor scenario leaked physical pages");
    wit_console_write("[TEST-PASS] User.ProcessorsAndAffinity\n");
}
