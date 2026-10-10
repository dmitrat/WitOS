#include "user.h"
#include "root_task.h"
#include "witos/boot.h"
#include "witos/platform.h"
#include "protocol.h"
#include "self_test.h"
#include "user_root_image.h"

/* The root task (RFC 0011 section 7.11, plan step K4) on both ISAs: the flat image of the root fixture, validated
 * whole, started with its startup descriptor and initial capabilities; the fixture checks the descriptor, writes
 * to the kernel log, maps the package's first page and the device table, and exits with zero as a root task does. Then the
 * validation of broken images: a wrong magic, a segment outside the window, overlapping segments, a file range
 * beyond the image and an entry outside executable code are refused whole. */

static WitUserProcess process;

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

static void expect_invalid(WitU8 *copy, WitU64 size, WitU32 offset, WitU64 value, WitU32 bytes, const char *what)
{
    WitFlatLayout layout;
    WitU8 saved[8];
    for (WitU32 i = 0; i < bytes; ++i) {
        saved[i] = copy[offset + i];
        copy[offset + i] = (WitU8)(value >> (i * 8));
    }
    require(!wit_flat_validate(copy, size, &layout), what);
    for (WitU32 i = 0; i < bytes; ++i) {
        copy[offset + i] = saved[i];
    }
}

void wit_root_task_self_test(const WitBootInfo *boot, WitPageAllocator *pages)
{
    static WitU8 copy[sizeof(wit_user_root_image)];
    WitFlatLayout layout;
    const WitU64 before = wit_pages_free_count(pages);
    require(wit_flat_validate(wit_user_root_image, sizeof(wit_user_root_image), &layout), "Root image rejected");
    require(layout.SegmentCount >= 1 && layout.Entry >= WIT_USER_FLAT_BASE, "Root image layout unexpected");
    require(wit_root_task_create(&process, pages, 0, boot, &layout), "Root task creation failed");
    wit_user_run(&process);
    if (process.State != WitUserExited || process.ExitCode != 0) {
        wit_console_write("Root task state/code: ");
        wit_console_write_u64(process.State);
        wit_console_write("/");
        wit_console_write_u64(process.ExitCode);
        wit_console_write(" last status: ");
        wit_console_write_u64(*(const WitU64 *)wit_user_space_physical(&process.Space, WIT_USER_DATA + 1304, 0, 0));
        wit_console_write(" checks passed: ");
        wit_console_write_u64(*(const WitU64 *)wit_user_space_physical(&process.Space, WIT_USER_DATA + 1312, 0, 0));
        wit_console_write("\n");
        wit_panic("Root task test failed");
    }
    wit_user_destroy(&process);
    require(wit_pages_free_count(pages) == before, "Root task leaked physical pages");
    wit_console_write("[TEST-PASS] Root.Started\n");

    for (WitU32 i = 0; i < sizeof(copy); ++i) {
        copy[i] = wit_user_root_image[i];
    }
    expect_invalid(copy, sizeof(copy), 0, 0x3054414C46544957ULL, 8, "Wrong magic accepted"); /* "WITFLAT0" */
    expect_invalid(copy, sizeof(copy), 8, 2, 4, "Foreign version accepted");
    expect_invalid(copy, sizeof(copy), 24, 0, 4, "Empty image accepted");
    expect_invalid(copy, sizeof(copy), 24, 5, 4, "Too many segments accepted");
    expect_invalid(copy, sizeof(copy), 64, WIT_USER_DATA, 8, "Segment over the kernel's pages accepted");
    expect_invalid(copy, sizeof(copy), 64, WIT_PROCESS_USER_LIMIT, 8, "Segment at the limit accepted");
    expect_invalid(copy, sizeof(copy), 64 + 8, sizeof(copy) + 4096, 8, "File range beyond the image accepted");
    expect_invalid(copy, sizeof(copy), 64 + 16, 0xFFFFFFFFULL, 4, "File size beyond memory size accepted");
    expect_invalid(copy, sizeof(copy), 64 + 24, WIT_MEMORY_READ | WIT_MEMORY_WRITE | WIT_MEMORY_EXECUTE, 4,
        "Writable executable segment accepted");
    expect_invalid(copy, sizeof(copy), 16, WIT_USER_FLAT_BASE - 4096, 8, "Entry outside the image accepted");
    require(wit_flat_validate(copy, sizeof(copy), &layout), "Restored image rejected");
    wit_console_write("[TEST-PASS] Root.ImageValidation\n");
}
