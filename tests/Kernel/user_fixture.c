#include "user.h"
#include "root_task.h"
#include "witos/root.h"
#include "self_test.h"

/* A mechanism fixture of a self-test kernel (K8.4c): its flat image validated whole, as the root task's is, the
 * component created with the fixture profile, and the startup descriptor at WIT_USER_INFO holding the kernel log
 * alone (WRITE). A test writes its own configuration after the descriptor (tests/User/protocol.h). */
int wit_test_create_fixture(
    struct WitUserProcess *process, WitPageAllocator *pages, WitU32 slot, const WitU8 *image, WitU32 size)
{
    WitFlatLayout layout;
    if (!wit_flat_validate(image, size, &layout) || !wit_user_create_flat(process, pages, slot, &layout, 0)) {
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
    startup->Handles[WIT_ROOT_HANDLE_LOG] = wit_handle_grant(&process->Handles, WIT_HANDLE_CONSOLE, WIT_RIGHT_WRITE);
    startup->HandleCount = 1;
    if (!startup->Handles[WIT_ROOT_HANDLE_LOG]) {
        wit_user_destroy(process);
        return 0;
    }
    return 1;
}
