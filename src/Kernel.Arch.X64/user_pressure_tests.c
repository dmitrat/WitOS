#include "user.h"
#include "witos/platform.h"
#include "protocol.h"
#include "pal_image.h"

static WitUserProcess process;
static WitPageAllocator limited;

static void require(int ok, const char *message)
{
    if (!ok) {
        wit_panic(message);
    }
}

static void run(WitPageAllocator *pages, WitU64 mode, WitU64 base)
{
    const WitU64 before = wit_pages_free_count(pages);
    require(wit_user_create_pe(&process, pages, 0, wit_pal_plain_image, sizeof(wit_pal_plain_image), base) == WitPeOk,
        "Pressure fixture load failed");
    const WitU32 owned = process.Space.OwnedCount;
    ((WitUserTestConfig *)wit_user_space_physical(&process.Space, WIT_USER_INFO, 0, 0))->Mode = mode;
    wit_user_run(&process);
    const WitU64 *report = (const WitU64 *)wit_user_space_physical(&process.Space, WIT_GC_INFO_REPORT, 0, 0);
    if (process.State != WitUserExited || process.ExitCode != WIT_TEST_EXIT_CODE) {
        wit_console_write("Pressure mode/state/code: ");
        wit_console_write_u64(mode);
        wit_console_write("/");
        wit_console_write_u64(process.State);
        wit_console_write("/");
        wit_console_write_u64(process.ExitCode);
        wit_console_write("\n");
        wit_panic("Memory-pressure contract failed");
    }
    require(report &&
            report[0] == mode &&
            report[1] == 1 &&
            process.Space.OwnedCount == owned &&
            !process.Handles.Count &&
            !process.Events.Count,
        "Pressure lifecycle or accounting failed");
    if (mode == 61) {
        require(process.ThreadCreates == 3 &&
                process.ThreadJoins == 2 &&
                process.ThreadReaps == 2 &&
                process.EventParks == 2 &&
                process.EventWakes == 1 &&
                process.WaitCloses == 1,
            "Pressure did not wake/cancel captured waits");
    }
    for (WitU32 i = 0; i < WIT_USER_RESERVATION_CAPACITY; ++i) {
        require(!process.Space.Reservations[i].Size, "Pressure reservation leak");
    }
    wit_user_destroy(&process);
    require(wit_pages_free_count(pages) == before, "Pressure teardown leaked pages");
}

void wit_user_pressure_self_test(WitPageAllocator *pages)
{
    run(pages, 60, WIT_USER_IMAGE_BASE);
    run(pages, 60, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.MemoryPressurePolicy\n");
    run(pages, 61, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.MemoryPressureWaitAndReuse\n");
    run(pages, 62, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.MemoryPressureCapacity\n");
    WitU64 borrowed[80];
    WitMemoryRegion regions[80];
    const WitU64 before = wit_pages_free_count(pages);
    for (WitU32 i = 0; i < 80; ++i) {
        require(wit_page_allocate(pages, &borrowed[i]), "Cannot borrow pressure frame");
        regions[i].Base = borrowed[i];
        regions[i].Length = WIT_PAGE_SIZE;
        regions[i].Kind = WIT_MEMORY_USABLE;
        regions[i].Reserved = 0;
    }
    require(wit_pages_initialize(&limited, regions, 80), "Cannot initialize pressure allocator");
    run(&limited, 63, WIT_USER_IMAGE_BASE);
    require(wit_pages_free_count(&limited) == 80, "Physical pressure leaked borrowed memory");
    for (WitU32 i = 0; i < 80; ++i) {
        require(wit_page_free(pages, borrowed[i]), "Cannot return pressure frame");
    }
    require(wit_pages_free_count(pages) == before, "Physical pressure fixture leaked memory");
    wit_console_write("[TEST-PASS] User.MemoryPressurePhysical\n");
}
