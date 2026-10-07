#include "user.h"
#include "witos/platform.h"
#include "protocol.h"
#include "self_test.h"
#include "user_memory_object_image.h"

/* Memory objects (RFC 0011 section 7.2) from user mode on both ISAs. The fixture creates objects, maps them at chosen
 * and fixed addresses under every protection, shares pages between views, changes protections within the handle's
 * rights, lets an object outlive its handle and end with its last mapping, runs code through a published executable
 * view of pages written through a writable one, moves an object through a channel and exhausts the object, page and
 * mapping quotas without partial state. */

static WitUserProcess process;

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

static void create(WitPageAllocator *pages, WitU64 mode)
{
    WitUserTestConfig *info;
    require(wit_user_create(&process, pages, 0, wit_user_memory_object_image, sizeof(wit_user_memory_object_image)),
        "Memory object test process creation failed");
    info = (WitUserTestConfig *)wit_user_space_physical(&process.Space, WIT_USER_INFO, 0, 0);
    info->Mode = mode;
}

void wit_user_memory_object_self_test(WitPageAllocator *pages)
{
    const WitU64 before = wit_pages_free_count(pages);
    static const char *names[] = {
        "MemoryObjectViews", "MemoryObjectCode", "MemoryObjectTransfer", "MemoryObjectLimits"};
    for (WitU64 mode = 0; mode < sizeof(names) / sizeof(names[0]); ++mode) {
        create(pages, mode);
        const WitU32 owned = process.Space.OwnedCount;
        wit_user_run(&process);
        if (process.State != WitUserExited || process.ExitCode != WIT_TEST_EXIT_CODE) {
            wit_console_write("Memory object test mode/state/code: ");
            wit_console_write_u64(mode);
            wit_console_write("/");
            wit_console_write_u64(process.State);
            wit_console_write("/");
            wit_console_write_u64(process.ExitCode);
            wit_console_write(" last status: "); /* The fixture records the status its failed check saw. */
            wit_console_write_u64(*(const WitU64 *)wit_user_space_physical(&process.Space, WIT_USER_DATA + 1304, 0, 0));
            wit_console_write("\n");
            wit_panic("User memory object test failed");
        }
        /* Every object ended with its last handle or mapping, every mapping was released, and the pages of the
         * objects went back: the component owns what it owned before its code ran. */
        require(process.Handles.Count == 0 &&
                process.MemoryObjects.Count == 0 &&
                process.Channels.Count == 0 &&
                process.Space.AliasCount == 0 &&
                process.Space.OwnedCount == owned,
            "Memory object scenario left objects, mappings or pages behind");
        for (WitU32 i = 0; i < WIT_RUNTIME_RESERVATION_CAPACITY; ++i) {
            require(!process.Space.MappedObjects[i] && !process.Space.MappedRights[i],
                "A mapping record survived its release");
        }
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Memory object scenario leaked physical pages");
        wit_console_write("[TEST-PASS] User.");
        wit_console_write(names[mode]);
        wit_console_write("\n");
    }
}
