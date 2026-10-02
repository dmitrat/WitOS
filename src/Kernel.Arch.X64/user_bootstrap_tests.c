#include "user.h"
#include "witos/platform.h"
#include "protocol.h"
#include "bootstrap_image.h"

static WitUserProcess process;
static WitPeImage plan;
static WitU8 modified[65536];

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

static WitU32 u32(const WitU8 *p)
{
    return (WitU32)p[0] | ((WitU32)p[1] << 8) | ((WitU32)p[2] << 16) | ((WitU32)p[3] << 24);
}

static void put32(WitU32 offset, WitU32 value)
{
    for (WitU32 i = 0; i < 4; ++i) {
        modified[offset + i] = (WitU8)(value >> (8 * i));
    }
}

static void reset(void)
{
    for (WitU32 i = 0; i < sizeof(wit_bootstrap_image); ++i) {
        modified[i] = wit_bootstrap_image[i];
    }
}

static void reject(WitPageAllocator *pages, WitPeStatus expected)
{
    const WitU64 before = wit_pages_free_count(pages);
    const WitPeStatus actual =
        wit_user_create_pe(&process, pages, 0, modified, sizeof(wit_bootstrap_image), WIT_USER_IMAGE_BASE);
    if (actual != expected) {
        wit_console_write("Bootstrap PE status actual/expected: ");
        wit_console_write_u64(actual);
        wit_console_write("/");
        wit_console_write_u64(expected);
        wit_console_write("\n");
        wit_panic("Unwind metadata rejection mismatch");
    }
    require(wit_pages_free_count(pages) == before && process.Space.Root == 0 && process.Handles.Count == 0,
        "Unwind rejection allocated resources");
}

static void metadata_tests(WitPageAllocator *pages)
{
    WitU32 table = 0, info = 0, code_info = 0, writable = 0, reloc_raw = 0;
    const WitU32 optional = u32(wit_bootstrap_image + 60) + 24;
    require(wit_pe_validate(wit_bootstrap_image, sizeof(wit_bootstrap_image), &plan) == WitPeOk &&
            plan.UnwindCount > 1 &&
            wit_pe_file_range(&plan, plan.UnwindRva, plan.UnwindSize, &table),
        "Native C image failed unwind validation");
    require(wit_pe_file_range(&plan, plan.UnwindInfo[0].Rva, plan.UnwindInfo[0].Size, &info), "Unwind info missing");
    for (WitU32 i = 0; i < plan.UnwindCount; ++i) {
        WitU32 raw;
        require(
            wit_pe_file_range(&plan, plan.UnwindInfo[i].Rva, plan.UnwindInfo[i].Size, &raw), "Unwind range missing");
        if (wit_bootstrap_image[raw + 2]) {
            code_info = raw;
        }
    }
    for (WitU32 i = 0; i < plan.SectionCount; ++i) {
        if (plan.Sections[i].Flags & WIT_PE_WRITE) {
            writable = plan.Sections[i].Rva;
        }
    }
    require(code_info && writable && wit_pe_file_range(&plan, plan.RelocRva, plan.RelocSize, &reloc_raw),
        "Native C metadata fixture incomplete");
    wit_console_write("[TEST-PASS] User.BootstrapUnwindMetadata\n");

    reset();
    put32(optional + 116 + 3 * 8, plan.UnwindSize + 1);
    reject(pages, WitPeInvalidImage);
    reset();
    put32(optional + 112 + 3 * 8, plan.UnwindRva + 1);
    reject(pages, WitPeInvalidImage);
    reset();
    put32(optional + 116 + 3 * 8, (WIT_PE_MAX_UNWIND_ENTRIES + 1) * 12);
    reject(pages, WitPeTooLarge);
    reset();
    put32(table + 4, u32(modified + table));
    reject(pages, WitPeInvalidImage);
    reset();
    put32(table + 12, u32(modified + table));
    reject(pages, WitPeInvalidImage);
    reset();
    put32(table, writable);
    put32(table + 4, writable + 1);
    reject(pages, WitPeInvalidImage);
    reset();
    put32(table + 8, writable);
    reject(pages, WitPeInvalidImage);
    reset();
    modified[info] = 2;
    reject(pages, WitPeUnsupportedImage);
    reset();
    modified[info] = 9;
    reject(pages, WitPeUnsupportedImage);
    reset();
    modified[info] = 33;
    reject(pages, WitPeUnsupportedImage);
    reset();
    modified[code_info + 4] = 255;
    reject(pages, WitPeInvalidImage);
    reset();
    modified[code_info + 2] = 1;
    modified[code_info + 5] = 0x11;
    reject(pages, WitPeInvalidImage);
    reset();
    modified[code_info + 5] = 15;
    reject(pages, WitPeUnsupportedImage);
    reset();
    modified[code_info + 5] = 0;
    reject(pages, WitPeInvalidImage);
    reset();
    modified[code_info + 3] = 0x10;
    reject(pages, WitPeInvalidImage);
    reset();
    put32(reloc_raw, plan.UnwindInfo[0].Rva & ~4095U);
    modified[reloc_raw + 8] = (WitU8)plan.UnwindInfo[0].Rva;
    modified[reloc_raw + 9] = (WitU8)(0xA0 | ((plan.UnwindInfo[0].Rva >> 8) & 15));
    reject(pages, WitPeInvalidImage);
    wit_console_write("[TEST-PASS] User.BootstrapUnwindRejection\n");
}

static void run(WitPageAllocator *pages, WitU64 mode, WitU64 base)
{
    const WitU64 before = wit_pages_free_count(pages);
    WitUserTestConfig *config;
    WitUserImageInfo *info;
    WitU32 owned;
    require(wit_user_create_pe(&process, pages, 0, wit_bootstrap_image, sizeof(wit_bootstrap_image), base) == WitPeOk,
        "Native bootstrap image creation failed");
    config = (WitUserTestConfig *)wit_user_space_physical(&process.Space, WIT_USER_INFO, 0, 0);
    config->Mode = mode;
    require(config->Startup.Version == WIT_ABI_VERSION &&
            config->Startup.Size == WIT_ABI_STARTUP_SIZE &&
            config->Startup.ImageInfo == WIT_USER_INFO + WIT_USER_IMAGE_INFO_OFFSET,
        "Image handoff missing");
    info = (WitUserImageInfo *)wit_user_space_physical(&process.Space, config->Startup.ImageInfo, 0, 0);
    require(info &&
            info->Version == WIT_IMAGE_INFO_VERSION &&
            info->Size == sizeof(*info) &&
            info->Base == base &&
            info->Entry == process.ImageEntry &&
            info->ImageSize == process.ImageSize &&
            info->RangeCount == plan.SectionCount &&
            info->UnwindRva == plan.UnwindRva &&
            info->UnwindSize == plan.UnwindSize &&
            !wit_user_space_physical(&process.Space, config->Startup.ImageInfo, 1, 0) &&
            !wit_user_space_physical(&process.Space, config->Startup.ImageInfo, 0, 1),
        "Image descriptor is incorrect or mutable");
    for (WitU32 i = 0; i < info->RangeCount; ++i) {
        require(info->Ranges[i].Rva == plan.Sections[i].Rva &&
                info->Ranges[i].Flags == plan.Sections[i].Flags &&
                info->Ranges[i].Size == plan.Sections[i].VirtualSize &&
                info->Ranges[i].InitializedSize <= plan.Sections[i].RawSize,
            "Image range descriptor mismatch");
    }
    owned = process.Space.OwnedCount;
    wit_user_run(&process);
    if (mode == WIT_BOOTSTRAP_TEST_WRITE_INFO || mode == WIT_BOOTSTRAP_TEST_INIT_FAULT) {
        require(process.State == WitUserFaulted &&
                process.FaultVector == 14 &&
                process.FaultError == 7 &&
                process.FaultCs == WIT_USER_CS &&
                process.FaultSs == WIT_USER_SS &&
                process.FaultAddress == WIT_USER_INFO + WIT_USER_IMAGE_INFO_OFFSET,
            "Bootstrap fault was not contained");
        if (mode == WIT_BOOTSTRAP_TEST_INIT_FAULT) {
            require(process.Space.OwnedCount > owned, "Fault injection did not hold constructor resources");
        }
    } else {
        if (process.State != WitUserExited || process.ExitCode != WIT_TEST_EXIT_CODE) {
            wit_console_write("Native bootstrap state/code: ");
            wit_console_write_u64(process.State);
            wit_console_write("/");
            wit_console_write_u64(process.ExitCode);
            wit_console_write("\n");
            wit_panic("Native bootstrap execution failed");
        }
        require(process.Space.OwnedCount == owned, "Native initializer/cleanup leaked committed memory");
        for (WitU32 i = 0; i < WIT_USER_RESERVATION_CAPACITY; ++i) {
            require(!process.Space.Reservations[i].Size, "Native startup left a reservation");
        }
        require(process.Writes ==
                (mode == WIT_BOOTSTRAP_TEST_INIT_FAIL ||
                            (mode >= WIT_BOOTSTRAP_TEST_BAD_INIT && mode <= WIT_BOOTSTRAP_TEST_BAD_IMAGE)
                        ? 0U
                        : 1U),
            "Invalid bootstrap reached application entry");
        if (mode == WIT_BOOTSTRAP_TEST_CONCURRENT) {
            require(process.ThreadCreates == 3 && process.ThreadJoins == 2 && process.ThreadReaps == 2,
                "Concurrent native startup did not run both threads");
        }
    }
    require(!process.Handles.Count && !process.Events.Count, "Bootstrap teardown left handles");
    wit_user_destroy(&process);
    require(wit_pages_free_count(pages) == before, "Bootstrap component teardown leaked");
}

void wit_user_bootstrap_self_test(WitPageAllocator *pages)
{
    metadata_tests(pages);
    run(pages, WIT_BOOTSTRAP_TEST_NORMAL, WIT_USER_IMAGE_BASE);
    run(pages, WIT_BOOTSTRAP_TEST_NORMAL, WIT_USER_IMAGE_ALTERNATE);
    wit_console_write("[TEST-PASS] User.BootstrapNativeEntry\n[TEST-PASS] User.BootstrapImageDescriptor\n");
    run(pages, WIT_BOOTSTRAP_TEST_CONCURRENT, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.BootstrapOrderAndRunOnce\n");
    run(pages, WIT_BOOTSTRAP_TEST_INIT_FAIL, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.BootstrapRollback\n");
    run(pages, WIT_BOOTSTRAP_TEST_MAIN_FAIL, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.BootstrapMainFailure\n");
    for (WitU64 mode = WIT_BOOTSTRAP_TEST_BAD_INIT; mode <= WIT_BOOTSTRAP_TEST_BAD_IMAGE; ++mode) {
        run(pages, mode, WIT_USER_IMAGE_BASE);
    }
    wit_console_write("[TEST-PASS] User.BootstrapValidation\n");
    run(pages, WIT_BOOTSTRAP_TEST_EMPTY, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.BootstrapEmptyList\n");
    run(pages, WIT_BOOTSTRAP_TEST_WRITE_INFO, WIT_USER_IMAGE_BASE);
    run(pages, WIT_BOOTSTRAP_TEST_NORMAL, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.BootstrapDescriptorProtection\n");
    run(pages, WIT_BOOTSTRAP_TEST_INIT_FAULT, WIT_USER_IMAGE_BASE);
    run(pages, WIT_BOOTSTRAP_TEST_NORMAL, WIT_USER_IMAGE_BASE);
    wit_console_write("[TEST-PASS] User.BootstrapInitializerFault\n");
}
