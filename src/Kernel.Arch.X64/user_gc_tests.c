#include "user.h"
#include "witos/platform.h"
#include "protocol.h"
#include "gc_memory_image.h"

static WitUserProcess process;
static void require(int condition, const char* message)
{
    if (!condition) wit_panic(message);
}
static void run(WitPageAllocator* pages, WitU64 mode, WitU64 base)
{
    const WitU64 free_before = wit_pages_free_count(pages);
    WitU32 owned;
    WitUserTestConfig* config;
    require(wit_user_create_pe(&process, pages, 0, wit_gc_memory_image,
        sizeof(wit_gc_memory_image), base) == WitPeOk, "GC memory fixture load failed");
    config = (WitUserTestConfig*)wit_user_space_physical(&process.Space, WIT_USER_INFO, 0, 0);
    config->Mode = mode;
    owned = process.Space.OwnedCount;
    wit_user_run(&process);
    if (mode == WIT_GC_TEST_NORMAL) {
        if (process.State != WitUserExited || process.ExitCode != WIT_TEST_EXIT_CODE) {
            wit_console_write("GC memory fixture state/code: ");
            wit_console_write_u64(process.State); wit_console_write("/");
            wit_console_write_u64(process.ExitCode); wit_console_write("\n");
            wit_panic("GC memory adapter contract failed");
        }
        require(process.Space.OwnedCount == owned, "GC adapter leaked backing or private page tables");
        for (WitU32 i = 0; i < WIT_USER_RESERVATION_CAPACITY; ++i)
            require(!process.Space.Reservations[i].Size, "GC adapter leaked reservation");
    } else {
        const WitU64 error = mode == WIT_GC_TEST_RESERVED ? 6 : mode == WIT_GC_TEST_NX ? 21 : 4;
        require(process.State == WitUserFaulted && process.FaultVector == 14 && process.FaultError == error &&
            process.FaultAddress == WIT_USER_MEMORY_BASE + (mode == WIT_GC_TEST_ROLLBACK ? 4096 : 0) &&
            process.FaultCs == WIT_USER_CS &&
            process.FaultSs == WIT_USER_SS, "GC adapter memory protection fault mismatch");
        if (mode == WIT_GC_TEST_ROLLBACK) {
            const WitU64 physical = wit_user_space_physical(&process.Space, WIT_USER_MEMORY_BASE, 1, 0);
            require(physical && *(WitU8*)physical == 0xCE && process.Space.OwnedCount == owned + 4,
                "Failed GC commit changed existing page or leaked backing/tables");
            for (WitU64 i = 1; i < 128; ++i)
                require(!wit_user_space_physical(&process.Space, WIT_USER_MEMORY_BASE + i * 4096, 0, 0),
                    "Failed GC commit retained a new page");
        } else if (mode != WIT_GC_TEST_NX)
            require(process.Space.OwnedCount == owned, "GC reserve/decommit retained backing pages");
    }
    require(!process.Handles.Count, "GC adapter component left handles");
    wit_user_destroy(&process);
    require(wit_pages_free_count(pages) == free_before, "GC adapter teardown leaked physical memory");
}
void wit_user_gc_self_test(WitPageAllocator* pages)
{
    run(pages, WIT_GC_TEST_NORMAL, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.GcMemoryContract\n");
    run(pages, WIT_GC_TEST_ROLLBACK, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.GcMemoryOwnership\n");
    run(pages, WIT_GC_TEST_NORMAL, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.GcMemoryRelocation\n");
    run(pages, WIT_GC_TEST_RESERVED, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.GcReserveProtection\n");
    run(pages, WIT_GC_TEST_DECOMMITTED, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.GcDecommitProtection\n");
    run(pages, WIT_GC_TEST_NX, WIT_USER_IMAGE_BASE);
    run(pages, WIT_GC_TEST_NORMAL, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.GcMemoryNx\n");
}
