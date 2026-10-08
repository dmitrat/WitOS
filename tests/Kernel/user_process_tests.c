#include "user.h"
#include "witos/platform.h"
#include "protocol.h"
#include "self_test.h"
#include "user_process_image.h"

/* Processes (RFC 0011 section 7.8, plan step K5.2c) from user mode on both ISAs. The fixture creates a channel and a
 * process with one end of it, writes a program into a memory object and maps it executable into the child beside a
 * stack object, starts the child's first thread with the version 3 request, waits for the process, reads the message
 * the child sent and its exit code, then creates and kills a second child; broken requests (a non-endpoint
 * capability, a handle without MANAGE, a stack outside the child, a thread into an ended process) are refused
 * whole. The kernel checks that no created process, object, channel, handle or page survives the scenario. */

static WitUserProcess process;

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

void wit_user_process_self_test(WitPageAllocator *pages)
{
    const WitU64 before = wit_pages_free_count(pages);
    require(wit_user_create(&process, pages, 0, wit_user_process_image, sizeof(wit_user_process_image)),
        "Process test component creation failed");
    const WitU32 owned = process.Space.OwnedCount;
    wit_user_run(&process);
    if (process.State != WitUserExited || process.ExitCode != WIT_TEST_EXIT_CODE) {
        wit_console_write("Process test state/code: ");
        wit_console_write_u64(process.State);
        wit_console_write("/");
        wit_console_write_u64(process.ExitCode);
        wit_console_write(" last status: ");
        wit_console_write_u64(*(const WitU64 *)wit_user_space_physical(&process.Space, WIT_USER_DATA + 1304, 0, 0));
        wit_console_write(" checks passed: ");
        wit_console_write_u64(*(const WitU64 *)wit_user_space_physical(&process.Space, WIT_USER_DATA + 1312, 0, 0));
        wit_console_write("\n");
        wit_panic("User process test failed");
    }
    /* Both children ended and their records went with the fixture's last handles; every object and channel ended;
     * the fixture owns what it owned before. */
    require(wit_user_processes_pooled() == 0, "A created process survived the scenario");
    require(process.Handles.Count == 0 &&
            wit_memory_objects_live() == 0 &&
            wit_channels_live() == 0 &&
            process.Space.ChargedPages == 0 &&
            process.Space.AliasCount == 0 &&
            process.Space.OwnedCount == owned,
        "Process scenario left objects, channels, mappings or pages behind");
    wit_user_destroy(&process);
    require(wit_pages_free_count(pages) == before, "Process scenario leaked physical pages");
    wit_console_write("[TEST-PASS] User.ProcessLifecycle\n");
}
