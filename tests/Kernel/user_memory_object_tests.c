#include "user.h"
#include "witos/platform.h"
#include "protocol.h"
#include "self_test.h"
#include "user_memory_object_image.h"

/* Memory objects (RFC 0011 section 7.2, plan steps K5.1 and K5.2b) from user mode on both ISAs: creation and mapping
 * validated whole, views at chosen and fixed addresses under every protection, shared pages, protection within the
 * handle's rights, an object that outlives its handle, published code, an object moved through a channel, the
 * quotas; and the component's end with everything live, which releases the handles and the capability in flight
 * at the exit and the mapping at the teardown, so that every object ends and every page returns. */

static WitUserProcess process;

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

static void run(WitPageAllocator *pages, WitU64 mode)
{
    WitUserTestConfig *info;
    require(wit_user_create(&process, pages, 0, wit_user_memory_object_image, sizeof(wit_user_memory_object_image)),
        "Memory object test process creation failed");
    info = (WitUserTestConfig *)wit_user_space_physical(&process.Space, WIT_USER_INFO, 0, 0);
    info->Mode = mode;
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
}

void wit_user_memory_object_self_test(WitPageAllocator *pages)
{
    const WitU64 before = wit_pages_free_count(pages);
    static const char *names[] = {
        "MemoryObjectViews", "MemoryObjectCode", "MemoryObjectTransfer", "MemoryObjectLimits"};
    for (WitU64 mode = 0; mode < sizeof(names) / sizeof(names[0]); ++mode) {
        run(pages, mode);
        /* Every object ended with its last handle or mapping, every mapping was released, and the pages of the
         * objects went back: the kernel's table is empty, the component is charged nothing and owns what it owned. */
        require(process.Handles.Count == 0 &&
                wit_memory_objects_live() == 0 &&
                process.Space.ChargedPages == 0 &&
                wit_channels_live() == 0 &&
                process.Space.AliasCount == 0,
            "Memory object scenario left objects, mappings or pages behind");
        for (WitU32 i = 0; i < WIT_PROCESS_RESERVATION_CAPACITY; ++i) {
            require(!process.Space.MappedObjects[i] && !process.Space.MappedRights[i],
                "A mapping record survived its release");
        }
        wit_user_destroy(&process);
        require(wit_pages_free_count(pages) == before, "Memory object scenario leaked physical pages");
        wit_console_write("[TEST-PASS] User.");
        wit_console_write(names[mode]);
        wit_console_write("\n");
    }
    /* The component exits with an object behind a view and a duplicate handle in flight, and a second object behind
     * its handle alone. The exit releases the handles and drops the message, so the second object ends at once and
     * the first keeps the view's reference, charged to the component; the teardown ends it and returns its page. */
    run(pages, WIT_MEMORY_OBJECT_TEST_EXIT);
    require(process.Handles.Count == 0 && wit_channels_live() == 0 && process.ChannelDrops == 1,
        "The exit left handles or channels behind");
    require(wit_memory_objects_live() == 1 &&
            wit_memory_objects_charged(&process) == 1 &&
            process.Space.ChargedPages == 1 &&
            process.Space.AliasCount == 1,
        "The exit did not leave exactly the mapped object");
    wit_user_destroy(&process);
    require(wit_memory_objects_live() == 0 && wit_memory_objects_charged(&process) == 0,
        "The teardown left a memory object behind");
    require(wit_pages_free_count(pages) == before, "Memory object exit scenario leaked physical pages");
    wit_console_write("[TEST-PASS] User.MemoryObjectExit\n");
}
