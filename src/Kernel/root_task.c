#include "user.h"
#include "root_task.h"
#include "witos/boot.h"
#include "witos/devices.h"
#include "witos/platform.h"
#include "witos/root.h"
#include "witos/virtual.h"

/* The root task (RFC 0011 section 7.11, plan step K4): the first component, created from the flat image the boot
 * contract names, started with a startup descriptor and its initial capabilities. The kernel parses nothing of the
 * package for it; the package is a read-only memory object over the boot extents. */

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

static WitUserProcess root_process;

int wit_root_task_create(WitUserProcess *process, WitPageAllocator *allocator, WitU32 slot, const WitBootInfo *boot,
    const WitFlatLayout *layout)
{
    WitU32 package = 0, table = 0;
    if (!wit_user_create_flat(process, allocator, slot, layout)) {
        return 0;
    }
    WitRootStartup *startup = (WitRootStartup *)wit_user_space_physical(&process->Space, WIT_USER_INFO, 0, 0);
    for (WitU32 i = 0; i < sizeof(*startup); ++i) {
        ((WitU8 *)startup)[i] = 0;
    }
    startup->Version = WIT_ROOT_STARTUP_VERSION;
    startup->Size = sizeof(*startup);
    startup->AbiVersion = WIT_ABI_VERSION;
    startup->Features = WIT_ABI_FEATURES;
    startup->MemoryBase = WIT_USER_MEMORY_BASE;
    startup->MemoryLimit = WIT_USER_MEMORY_LIMIT;
    startup->CodeBase = WIT_USER_CODE_BASE;
    startup->CodeLimit = WIT_USER_CODE_LIMIT;
    startup->PackageBytes = boot->StorageBytes;
    /* The kernel log (S5.2): the root task's output, which it may delegate to the processes it starts. */
    startup->Handles[WIT_ROOT_HANDLE_LOG] = wit_handle_grant(
        &process->Handles, WIT_HANDLE_CONSOLE, WIT_RIGHT_WRITE | WIT_RIGHT_DUPLICATE | WIT_RIGHT_TRANSFER);
    if (wit_user_memory_object_adopt_extents(process, WIT_MEMORY_OBJECT_PACKAGE, boot->StorageExtents,
            boot->StorageExtentCount, boot->StorageBytes, &package)) {
        startup->Handles[WIT_ROOT_HANDLE_PACKAGE] = wit_handle_grant_object(&process->Handles, WIT_HANDLE_MEMORY_OBJECT,
            WIT_RIGHT_MAP | WIT_RIGHT_EXECUTE | WIT_RIGHT_QUERY | WIT_RIGHT_DUPLICATE | WIT_RIGHT_TRANSFER, package);
        if (!startup->Handles[WIT_ROOT_HANDLE_PACKAGE]) {
            wit_user_memory_object_release(package);
        }
    }
    startup->Handles[WIT_ROOT_HANDLE_DEVICES] = wit_user_device_table_grant(
        process, WIT_RIGHT_MAP | WIT_RIGHT_QUERY | WIT_RIGHT_DUPLICATE | WIT_RIGHT_TRANSFER | WIT_RIGHT_ACQUIRE);
    /* The clock capability (K6): the authority to set UTC, the root task's to keep or to hand on. */
    startup->Handles[WIT_ROOT_HANDLE_CLOCK] = wit_handle_grant(
        &process->Handles, WIT_HANDLE_CLOCK, WIT_RIGHT_WRITE | WIT_RIGHT_DUPLICATE | WIT_RIGHT_TRANSFER);
    startup->HandleCount = 4;
    (void)table;
    if (!startup->Handles[WIT_ROOT_HANDLE_LOG] ||
        !startup->Handles[WIT_ROOT_HANDLE_PACKAGE] ||
        !startup->Handles[WIT_ROOT_HANDLE_DEVICES] ||
        !startup->Handles[WIT_ROOT_HANDLE_CLOCK]) {
        wit_user_destroy(process);
        return 0;
    }
    return 1;
}

int wit_root_task_run(const WitBootInfo *boot, WitPageAllocator *allocator, WitU64 *exit_code)
{
    WitFlatLayout layout;
    *exit_code = 0;
    if (!boot->RootTaskBytes) {
        return 0;
    }
    const WitU8 *file = wit_virtual_root_task();
    require(file != 0, "Root task image is not mapped");
    if (!wit_flat_validate(file, boot->RootTaskBytes, &layout)) {
        wit_panic("Invalid root task image");
    }
    if (!wit_root_task_create(&root_process, allocator, 0, boot, &layout)) {
        wit_panic("Root task creation failed");
    }
    wit_console_write("[ROOT] starting\n");
    wit_user_run(&root_process);
    if (root_process.State != WitUserExited) {
        wit_console_write("[ROOT] ended without exiting: state ");
        wit_console_write_u64(root_process.State);
        wit_console_write("\n");
        wit_panic("Root task did not exit");
    }
    *exit_code = root_process.ExitCode;
    wit_console_write("[ROOT] exit code ");
    wit_console_write_u64(root_process.ExitCode);
    wit_console_write("\n");
    wit_user_destroy(&root_process);
    return 1;
}
