#include "user.h"
#include "witos/platform.h"
#include "protocol.h"
#include "pe_image.h"

static WitUserProcess components[2];
static WitPageAllocator limited;
static WitU8 changed[65536];
static WitPeImage original;
static WitU32 nt, optional, section_headers, ro_index, data_index, reloc_index, reloc_raw;

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

static WitU16 u16(const WitU8 *p)
{
    return (WitU16)(p[0] | ((WitU16)p[1] << 8));
}

static WitU32 u32(const WitU8 *p)
{
    return (WitU32)u16(p) | ((WitU32)u16(p + 2) << 16);
}

static WitU64 u64(const WitU8 *p)
{
    return (WitU64)u32(p) | ((WitU64)u32(p + 4) << 32);
}

static void put16(WitU32 at, WitU16 value)
{
    changed[at] = (WitU8)value;
    changed[at + 1] = (WitU8)(value >> 8);
}

static void put32(WitU32 at, WitU32 value)
{
    for (WitU32 i = 0; i < 4; ++i) {
        changed[at + i] = (WitU8)(value >> (8 * i));
    }
}

static void put64(WitU32 at, WitU64 value)
{
    for (WitU32 i = 0; i < 8; ++i) {
        changed[at + i] = (WitU8)(value >> (8 * i));
    }
}

static void reset(void)
{
    for (WitU32 i = 0; i < sizeof(changed); ++i) {
        changed[i] = i < sizeof(wit_pe_test_image) ? wit_pe_test_image[i] : 0;
    }
}

static void reject(WitPageAllocator *pages, WitU32 size, WitU64 base, WitPeStatus expected)
{
    const WitU64 before = wit_pages_free_count(pages);
    const WitPeStatus actual = wit_user_create_pe(&components[0], pages, 0, changed, size, base);
    if (actual != expected) {
        wit_console_write("PE rejection actual/expected: ");
        wit_console_write_u64(actual);
        wit_console_write("/");
        wit_console_write_u64(expected);
        wit_console_write("\n");
        wit_panic("PE rejection status mismatch");
    }
    require(wit_pages_free_count(pages) == before &&
            components[0].Space.Root == 0 &&
            components[0].Space.OwnedCount == 0 &&
            components[0].Handles.Count == 0,
        "Rejected image allocated resources");
}

static void reject_current(WitPageAllocator *pages, WitPeStatus expected)
{
    reject(pages, sizeof(wit_pe_test_image), WIT_USER_IMAGE_BASE, expected);
}

static void create(WitPageAllocator *pages, WitU32 slot, const WitU8 *file, WitU32 size, WitU64 base, WitU64 mode)
{
    WitUserTestConfig *info;
    require(wit_user_create_pe(&components[slot], pages, slot, file, size, base) == WitPeOk,
        "Valid PE component creation failed");
    info = (WitUserTestConfig *)wit_user_space_physical(&components[slot].Space, WIT_USER_INFO, 0, 0);
    info->Mode = mode;
    info->InstanceId = components[slot].Id;
}

static void check_normal(WitUserProcess *process)
{
    const WitU64 data = wit_user_space_physical(&process->Space, process->ImageBase + WIT_PE_TEST_WRITTEN_RVA, 0, 0);
    if (process->State != WitUserExited ||
        process->ExitCode != WIT_TEST_EXIT_CODE ||
        process->Writes != 1 ||
        process->Handles.Count != 0 ||
        !data ||
        *(WitU64 *)data != process->Id) {
        wit_console_write("PE state/code/writes/data/id: ");
        wit_console_write_u64(process->State);
        wit_console_write("/");
        wit_console_write_u64(process->ExitCode);
        wit_console_write("/");
        wit_console_write_u64(process->Writes);
        wit_console_write("/");
        wit_console_write_u64(data ? *(WitU64 *)data : 0);
        wit_console_write("/");
        wit_console_write_u64(process->Id);
        wit_console_write("\n");
        wit_panic("PE execution, relocation or BSS test failed");
    }
}

static void gap(void)
{
    const WitPeSection *s = &original.Sections[reloc_index];
    reset();
    put32(section_headers + reloc_index * 40 + 12, s->Rva + 4096);
    put32(optional + 112 + 5 * 8, original.RelocRva + 4096);
    put32(optional + 56, original.ImageSize + 4096);
}

static void rebase_file(WitU64 preferred)
{
    WitU32 consumed = 0;
    reset();
    put64(optional + 24, preferred);
    while (consumed < original.RelocSize) {
        const WitU32 page = u32(wit_pe_test_image + reloc_raw + consumed);
        const WitU32 length = u32(wit_pe_test_image + reloc_raw + consumed + 4);
        for (WitU32 p = 8; p < length; p += 2) {
            const WitU16 entry = u16(wit_pe_test_image + reloc_raw + consumed + p);
            WitU32 raw;
            if ((entry >> 12) == 0) {
                continue;
            }
            require(wit_pe_file_range(&original, page + (entry & 4095), 8, &raw), "Rebase fixture target missing");
            put64(raw, preferred + (u64(wit_pe_test_image + raw) - original.PreferredBase));
        }
        consumed += length;
    }
}

static void malformed_tests(WitPageAllocator *pages)
{
    const WitU32 sizes[] = {0, 1, 63, 64, 128, sizeof(wit_pe_test_image) - 1};
    const WitU32 data_header = section_headers + data_index * 40;
    const WitU32 ro_header = section_headers + ro_index * 40;
    WitU32 pointer_raw;
    reset();
    for (WitU32 i = 0; i < sizeof(sizes) / sizeof(sizes[0]); ++i) {
        reject(pages, sizes[i], WIT_USER_IMAGE_BASE, WitPeInvalidImage);
    }
    reject(pages, WIT_PE_MAX_FILE_SIZE + 1, WIT_USER_IMAGE_BASE, WitPeTooLarge);
    changed[0] = 0;
    reject_current(pages, WitPeInvalidImage);
    reset();
    put32(60, 0xFFFFFFF0U);
    reject_current(pages, WitPeInvalidImage);
    reset();
    put32(nt, 0);
    reject_current(pages, WitPeInvalidImage);
    reset();
    put32(optional + 60, 0);
    reject_current(pages, WitPeInvalidImage);
    reset();
    put32(optional + 56, original.ImageSize + 1);
    reject_current(pages, WitPeInvalidImage);
    reset();
    put32(optional + 56, WIT_PE_MAX_IMAGE_SIZE + 4096);
    reject_current(pages, WitPeTooLarge);
    reset();
    put64(optional + 24, 0xFFFFFFFFFFFF0000ULL);
    reject_current(pages, WitPeInvalidImage);
    reset();
    put16(nt + 6, WIT_PE_MAX_SECTIONS + 1);
    reject_current(pages, WitPeTooLarge);
    reset();
    reject(pages, sizeof(wit_pe_test_image), WIT_USER_CODE, WitPeBadBase);
    reject(pages, sizeof(wit_pe_test_image), WIT_USER_IMAGE_BASE + 1, WitPeBadBase);
    reject(pages, sizeof(wit_pe_test_image), WIT_USER_LIMIT, WitPeBadBase);
    wit_console_write("[TEST-PASS] User.ImageHeadersAndBounds\n");

    reset();
    put16(nt + 4, 0xAA64);
    reject_current(pages, WitPeUnsupportedImage);
    reset();
    put16(nt + 22, (WitU16)(u16(changed + nt + 22) | 0x2000));
    reject_current(pages, WitPeUnsupportedImage);
    reset();
    put16(optional, 0x10B);
    reject_current(pages, WitPeUnsupportedImage);
    reset();
    put16(optional + 68, 3);
    reject_current(pages, WitPeUnsupportedImage);
    reset();
    put32(optional + 32, 8192);
    reject_current(pages, WitPeUnsupportedImage);
    reset();
    put32(optional + 36, 3);
    reject_current(pages, WitPeUnsupportedImage);
    for (WitU32 i = 0; i < 16; ++i) {
        if (i == 5 || i == 6) {
            continue;
        }
        reset();
        put32(optional + 112 + i * 8, 0x1000);
        put32(optional + 116 + i * 8, 16);
        reject_current(pages, (i == 3 || i == 9) ? WitPeInvalidImage : WitPeUnsupportedImage);
    }
    reset();
    put32(optional + 112 + 5 * 8, 0);
    put32(optional + 116 + 5 * 8, 0);
    reject_current(pages, WitPeUnsupportedImage); /* relocation required at this base */
    wit_console_write("[TEST-PASS] User.ImageUnsupportedFeatures\n");

    reset();
    put32(optional + 16, original.Sections[ro_index].Rva);
    reject_current(pages, WitPeInvalidImage);
    reset();
    put32(optional + 16, original.ImageSize);
    reject_current(pages, WitPeInvalidImage);
    reset();
    put32(ro_header + 12, original.Sections[0].Rva);
    reject_current(pages, WitPeInvalidImage);
    reset();
    put32(ro_header + 20, original.Sections[0].RawOffset);
    reject_current(pages, WitPeInvalidImage);
    reset();
    put32(data_header + 20, 0xFFFFFE00U);
    reject_current(pages, WitPeInvalidImage);
    reset();
    put32(data_header + 8, 0xFFFFFFFFU);
    reject_current(pages, WitPeInvalidImage);
    reset();
    put32(section_headers + 36, u32(changed + section_headers + 36) | 0x80000000U);
    reject_current(pages, WitPeUnsupportedImage);
    reset();
    put32(ro_header + 36, u32(changed + ro_header + 36) & ~0x40000000U);
    reject_current(pages, WitPeUnsupportedImage);
    wit_console_write("[TEST-PASS] User.ImageSectionsAndEntry\n");

    reset();
    put32(reloc_raw + 4, 6);
    reject_current(pages, WitPeInvalidImage);
    reset();
    put32(reloc_raw + 4, 0xFFFFFFFCU);
    reject_current(pages, WitPeInvalidImage);
    reset();
    put32(reloc_raw, original.ImageSize);
    reject_current(pages, WitPeInvalidImage);
    reset();
    put16(reloc_raw + 8, (WitU16)((u16(changed + reloc_raw + 8) & 4095) | 0x3000));
    reject_current(pages, WitPeUnsupportedImage);
    reset();
    put16(reloc_raw + 10, u16(changed + reloc_raw + 8));
    reject_current(pages, WitPeInvalidImage);
    reset();
    put32(reloc_raw, original.RelocRva);
    put16(reloc_raw + 8, 0xA000);
    reject_current(pages, WitPeInvalidImage); /* relocation cannot rewrite its table */
    reset();
    require(
        wit_pe_file_range(&original, u32(changed + reloc_raw) + (u16(changed + reloc_raw + 8) & 4095), 8, &pointer_raw),
        "Test pointer missing");
    put64(pointer_raw, ~0ULL);
    reject_current(pages, WitPeInvalidImage);
    reset();
    put32(optional + 116 + 5 * 8, original.RelocSize - 1);
    reject_current(pages, WitPeInvalidImage);
    reset();
    put32(section_headers + reloc_index * 40 + 8, 8192);
    put32(section_headers + reloc_index * 40 + 16, 8192);
    put32(optional + 56, original.RelocRva + 8192);
    put32(optional + 116 + 5 * 8, 8192);
    put32(reloc_raw + 4, 8192);
    reject(pages, original.Sections[reloc_index].RawOffset + 8192, WIT_USER_IMAGE_BASE, WitPeTooLarge);
    wit_console_write("[TEST-PASS] User.ImageRelocationValidation\n");
}

static void allocation_failures(WitPageAllocator *pages)
{
    WitU64 borrowed[40];
    WitMemoryRegion regions[40];
    WitU32 required;
    const WitU64 before = wit_pages_free_count(pages);
    create(pages, 0, wit_pe_test_image, sizeof(wit_pe_test_image), WIT_USER_IMAGE_BASE, 0);
    required = components[0].Space.OwnedCount;
    wit_user_destroy(&components[0]);
    require(required < 40, "Image fixture exceeds OOM test capacity");
    for (WitU32 i = 0; i < required; ++i) {
        require(wit_page_allocate(pages, &borrowed[i]), "Cannot borrow PE test frame");
        regions[i].Base = borrowed[i];
        regions[i].Length = 4096;
        regions[i].Kind = WIT_MEMORY_USABLE;
        regions[i].Reserved = 0;
    }
    for (WitU32 count = 0; count <= required; ++count) {
        if (!count) {
            regions[0].Kind = WIT_MEMORY_RESERVED;
        }
        require(wit_pages_initialize(&limited, regions, count ? count : 1), "PE limited allocator setup failed");
        require(wit_user_create_pe(&components[0], &limited, 0, wit_pe_test_image, sizeof(wit_pe_test_image),
                    WIT_USER_IMAGE_BASE) == (count == required ? WitPeOk : WitPeNoMemory),
            "PE physical exhaustion did not return correct status");
        if (count == required) {
            wit_user_destroy(&components[0]);
        }
        require(components[0].Space.Root == 0 &&
                components[0].Space.OwnedCount == 0 &&
                components[0].Handles.Count == 0 &&
                wit_pages_free_count(&limited) == count,
            "Partial PE creation leaked");
        regions[0].Kind = WIT_MEMORY_USABLE;
    }
    for (WitU32 i = 0; i < required; ++i) {
        require(wit_page_free(pages, borrowed[i]), "PE borrowed frame return failed");
    }
    require(wit_pages_free_count(pages) == before, "PE OOM suite leaked");
    wit_console_write("[TEST-PASS] User.ImageAllocationRollback\n");
}

void wit_user_image_self_test(WitPageAllocator *pages)
{
    const WitU64 before = wit_pages_free_count(pages);
    static const char *fault_names[] = {"ImageWriteCode", "ImageWriteReadOnly", "ImageWriteHeaders", "ImageNxData",
        "ImageEndBoundary", "ImageGapFault"};
    WitU64 expected_address[6];
    WitU64 data_a, data_b;
    require(sizeof(wit_pe_test_image) < sizeof(changed) &&
            wit_pe_validate(wit_pe_test_image, sizeof(wit_pe_test_image), &original) == WitPeOk,
        "Linked PE fixture failed validation");
    nt = u32(wit_pe_test_image + 60);
    optional = nt + 24;
    section_headers = optional + 240;
    ro_index = data_index = reloc_index = WIT_PE_MAX_SECTIONS;
    for (WitU32 i = 0; i < original.SectionCount; ++i) {
        const WitPeSection *s = &original.Sections[i];
        if (s->Flags == WIT_PE_READ && s->Rva != original.RelocRva) {
            ro_index = i;
        }
        if (s->Flags & WIT_PE_WRITE) {
            data_index = i;
        }
        if (s->Rva == original.RelocRva) {
            reloc_index = i;
        }
    }
    require(ro_index < original.SectionCount &&
            data_index < original.SectionCount &&
            reloc_index < original.SectionCount &&
            wit_pe_file_range(&original, original.RelocRva, original.RelocSize, &reloc_raw),
        "PE test sections missing");
    malformed_tests(pages);

    create(pages, 0, wit_pe_test_image, sizeof(wit_pe_test_image), WIT_USER_IMAGE_BASE, 0);
    require(components[0].ImageEntry == WIT_USER_IMAGE_BASE + original.EntryRva &&
            !wit_user_space_physical(&components[0].Space, WIT_USER_CODE, 0, 0),
        "PE used legacy code mapping");
    wit_user_run(&components[0]);
    check_normal(&components[0]);
    wit_user_destroy(&components[0]);
    create(pages, 0, wit_pe_test_image, sizeof(wit_pe_test_image), WIT_USER_IMAGE_ALTERNATE, 0);
    wit_user_run(&components[0]);
    check_normal(&components[0]);
    wit_user_destroy(&components[0]);
    wit_console_write("[TEST-PASS] User.ImageRelocatedExecution\n");

    rebase_file(WIT_USER_IMAGE_ALTERNATE + 0x100000);
    create(pages, 0, changed, sizeof(wit_pe_test_image), WIT_USER_IMAGE_BASE, 0);
    wit_user_run(&components[0]);
    check_normal(&components[0]);
    wit_user_destroy(&components[0]);
    wit_console_write("[TEST-PASS] User.ImageRelocationDirections\n");
    create(pages, 0, wit_pe_fixed_image, sizeof(wit_pe_fixed_image), WIT_USER_IMAGE_BASE, 0);
    wit_user_run(&components[0]);
    check_normal(&components[0]);
    wit_user_destroy(&components[0]);
    wit_console_write("[TEST-PASS] User.ImagePreferredExecution\n");

    create(pages, 0, wit_pe_test_image, sizeof(wit_pe_test_image), WIT_USER_IMAGE_BASE, 0);
    create(pages, 1, wit_pe_test_image, sizeof(wit_pe_test_image), WIT_USER_IMAGE_BASE, 0);
    data_a = wit_user_space_physical(&components[0].Space, WIT_USER_IMAGE_BASE + WIT_PE_TEST_WRITTEN_RVA, 1, 0);
    data_b = wit_user_space_physical(&components[1].Space, WIT_USER_IMAGE_BASE + WIT_PE_TEST_WRITTEN_RVA, 1, 0);
    require(data_a != data_b && *(WitU64 *)data_a == 0 && *(WitU64 *)data_b == 0, "PE pages aliased");
    wit_user_run(&components[1]);
    check_normal(&components[1]);
    require(*(WitU64 *)data_a == 0, "Peer image data changed");
    wit_user_run(&components[0]);
    check_normal(&components[0]);
    wit_user_destroy(&components[1]);
    wit_user_destroy(&components[0]);
    require(wit_pages_free_count(pages) == before, "PE images leaked pages");
    wit_console_write("[TEST-PASS] User.ImageZeroFillAndPrivate\n");

    gap();
    create(pages, 0, changed, sizeof(wit_pe_test_image), WIT_USER_IMAGE_BASE, 0);
    require(!wit_user_space_physical(&components[0].Space, WIT_USER_IMAGE_BASE + original.RelocRva, 0, 0),
        "Image gap was mapped");
    require(wit_user_create_pe(&components[0], pages, 1, changed, sizeof(wit_pe_test_image), WIT_USER_IMAGE_BASE) ==
            WitPeBusy,
        "Live process object could be reused in another slot");
    wit_user_run(&components[0]);
    check_normal(&components[0]);
    wit_user_destroy(&components[0]);
    wit_console_write("[TEST-PASS] User.ImageGapMapping\n");
    allocation_failures(pages);

    expected_address[0] = WIT_USER_IMAGE_BASE + original.EntryRva;
    expected_address[1] = WIT_USER_IMAGE_BASE + WIT_PE_TEST_READONLY_POINTER_RVA;
    expected_address[2] = WIT_USER_IMAGE_BASE;
    expected_address[3] = WIT_USER_IMAGE_BASE + WIT_PE_TEST_DATA_POINTER_RVA;
    expected_address[4] = WIT_USER_IMAGE_BASE + original.ImageSize;
    expected_address[5] = WIT_USER_IMAGE_BASE + original.RelocRva;
    for (WitU32 i = 0; i < 6; ++i) {
        if (i == 5) {
            gap();
        } else {
            reset();
        }
        create(pages, 0, changed, sizeof(wit_pe_test_image), WIT_USER_IMAGE_BASE, i + 1);
        wit_user_run(&components[0]);
        require(components[0].State == WitUserFaulted &&
                components[0].FaultVector == 14 &&
                components[0].FaultError ==
                    (i < 3           ? 7ULL
                            : i == 3 ? 21ULL
                                     : 4ULL) &&
                components[0].FaultAddress == expected_address[i] &&
                components[0].FaultState.Cs == WIT_USER_CS &&
                components[0].FaultState.Ss == WIT_USER_SS &&
                components[0].Handles.Count == 0,
            "PE protection fault not contained");
        wit_user_destroy(&components[0]);
        require(wit_pages_free_count(pages) == before, "Faulted PE component leaked");
        create(pages, 0, wit_pe_test_image, sizeof(wit_pe_test_image), WIT_USER_IMAGE_BASE, 0);
        wit_user_run(&components[0]);
        check_normal(&components[0]);
        wit_user_destroy(&components[0]);
        require(wit_pages_free_count(pages) == before, "PE recovery leaked");
        wit_console_write("[TEST-PASS] User.");
        wit_console_write(fault_names[i]);
        wit_console_write("\n");
    }
}
