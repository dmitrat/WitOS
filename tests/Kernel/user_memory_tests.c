#include "user.h"
#include "witos/platform.h"

static WitUserSpace spaces[2];
static WitPageAllocator limited_pages;

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

static void expect(WitU64 actual, WitU64 expected)
{
    require(actual == expected, "User memory returned unexpected status");
}

/* Fixed reservations and partial releases (S5.1): what a loader and a runtime's munmap need. */
static void fixed_and_partial(WitPageAllocator *pages)
{
    const WitU64 before = wit_pages_free_count(pages);
    const WitU64 page = 4096, fixed = WIT_USER_MEMORY_BASE + 0x400000ULL;
    WitU64 result = 0, held[WIT_USER_RESERVATION_CAPACITY];
    require(wit_user_space_create(&spaces[0], pages), "Fixed reservation space creation failed");
    expect(wit_user_memory_reserve(&spaces[0], 16 * page, page, fixed, &result), WIT_STATUS_OK);
    require(result == fixed, "Fixed reservation moved");
    expect(wit_user_memory_reserve(&spaces[0], page, page, fixed + page, &result), WIT_STATUS_BUSY);
    expect(wit_user_memory_reserve(&spaces[0], 2 * page, page, fixed - page, &result), WIT_STATUS_BUSY);
    expect(wit_user_memory_reserve(&spaces[0], page, 0x10000, fixed + 0x11000, &result), WIT_STATUS_INVALID_ARGUMENT);
    expect(wit_user_memory_reserve(&spaces[0], page, page, fixed + 1, &result), WIT_STATUS_INVALID_ARGUMENT);
    expect(wit_user_memory_reserve(&spaces[0], page, page, WIT_USER_DATA, &result), WIT_STATUS_BAD_ADDRESS);
    expect(wit_user_memory_commit(&spaces[0], fixed, 16 * page, 3), WIT_STATUS_OK);
    const WitU64 owned = spaces[0].OwnedCount;
    /* A middle part splits the reservation, and its pages go. */
    expect(wit_user_memory_release(&spaces[0], fixed + 4 * page, 4 * page), WIT_STATUS_OK);
    require(spaces[0].OwnedCount == owned - 4, "Partial release kept its pages");
    expect(wit_user_memory_commit(&spaces[0], fixed + 4 * page, page, 3), WIT_STATUS_NOT_RESERVED);
    expect(wit_user_memory_commit(&spaces[0], fixed + 3 * page, 2 * page, 3), WIT_STATUS_NOT_RESERVED);
    expect(wit_user_memory_commit(&spaces[0], fixed, 4 * page, 3), WIT_STATUS_OK);
    expect(wit_user_memory_release(&spaces[0], fixed + 9 * page, 0), WIT_STATUS_NOT_RESERVED);
    /* The ends shrink; the remains release whole or by their full size. */
    expect(wit_user_memory_release(&spaces[0], fixed, page), WIT_STATUS_OK);
    expect(wit_user_memory_release(&spaces[0], fixed + 15 * page, page), WIT_STATUS_OK);
    expect(wit_user_memory_release(&spaces[0], fixed + page, 4 * page), WIT_STATUS_INVALID_ARGUMENT);
    expect(wit_user_memory_release(&spaces[0], fixed + page, page + 1), WIT_STATUS_INVALID_ARGUMENT);
    expect(wit_user_memory_release(&spaces[0], fixed + page, 3 * page), WIT_STATUS_OK);
    expect(wit_user_memory_release(&spaces[0], fixed + 8 * page, 0), WIT_STATUS_OK);
    expect(wit_user_memory_release(&spaces[0], fixed + 8 * page, page), WIT_STATUS_NOT_RESERVED);
    require(spaces[0].OwnedCount <= owned - 16, "Released reservations kept their pages"); /* and empty tables */
    /* The range is free again; a split needs a free slot. */
    expect(wit_user_memory_reserve(&spaces[0], 16 * page, page, fixed, &result), WIT_STATUS_OK);
    for (WitU32 i = 1; i < WIT_USER_RESERVATION_CAPACITY; ++i) {
        expect(wit_user_memory_reserve(&spaces[0], page, page, 0, &held[i]), WIT_STATUS_OK);
    }
    expect(wit_user_memory_release(&spaces[0], fixed + 4 * page, page), WIT_STATUS_NO_MEMORY);
    expect(wit_user_memory_release(&spaces[0], fixed, page), WIT_STATUS_OK); /* a shrink needs no slot */
    expect(wit_user_memory_release(&spaces[0], fixed + 4 * page, page), WIT_STATUS_NO_MEMORY);
    expect(wit_user_memory_release(&spaces[0], held[1], 0), WIT_STATUS_OK);
    expect(wit_user_memory_release(&spaces[0], fixed + 4 * page, page), WIT_STATUS_OK);
    wit_user_space_destroy(&spaces[0]);
    require(wit_pages_free_count(pages) == before, "Fixed reservation tests leaked");
    wit_console_write("[TEST-PASS] User.MemoryFixedAndPartial\n");
}

static void physical_failure(WitPageAllocator *pages)
{
    WitU64 borrowed[6], base;
    WitMemoryRegion regions[6];
    const WitU64 before = wit_pages_free_count(pages);
    for (WitU32 i = 0; i < 6; ++i) {
        require(wit_page_allocate(pages, &borrowed[i]), "Cannot borrow memory test frame");
        regions[i].Base = borrowed[i];
        regions[i].Length = 4096;
        regions[i].Kind = WIT_MEMORY_USABLE;
        regions[i].Reserved = 0;
    }
    /* Real, exclusively borrowed RAM: fail each intermediate table allocation,
     * the first data allocation and a later data allocation. No global OOM. */
    for (WitU32 count = 1; count <= 6; ++count) {
        require(wit_pages_initialize(&limited_pages, regions, count), "Limited allocator setup failed");
        require(wit_user_space_create(&spaces[0], &limited_pages), "Limited root allocation failed");
        expect(wit_user_memory_reserve(&spaces[0], 16384, 4096, 0, &base), WIT_STATUS_OK);
        expect(wit_user_memory_commit(&spaces[0], base, 8192, 3), count == 6 ? WIT_STATUS_OK : WIT_STATUS_NO_MEMORY);
        if (count < 6) {
            require(spaces[0].OwnedCount == 1 &&
                    wit_pages_free_count(&limited_pages) == count - 1 &&
                    !wit_user_space_physical(&spaces[0], base, 0, 0),
                "Physical OOM rollback leaked");
            if (count == 5) {
                expect(wit_user_memory_commit(&spaces[0], base, 4096, 3), WIT_STATUS_OK);
                *(WitU64 *)wit_user_space_physical(&spaces[0], base, 1, 0) = 0x1234;
                expect(wit_user_memory_commit(&spaces[0], base, 8192, 1), WIT_STATUS_NO_MEMORY);
                require(*(WitU64 *)wit_user_space_physical(&spaces[0], base, 1, 0) == 0x1234,
                    "Failed commit modified an existing page");
            }
        }
        wit_user_space_destroy(&spaces[0]);
        require(wit_pages_free_count(&limited_pages) == count, "Limited allocator teardown leaked");
    }
    for (WitU32 i = 0; i < 6; ++i) {
        require(wit_page_free(pages, borrowed[i]), "Borrowed frame return failed");
    }
    require(wit_pages_free_count(pages) == before, "Physical failure test leaked");
    wit_console_write("[TEST-PASS] User.MemoryPhysicalOom\n");
}

void wit_user_memory_self_test(WitPageAllocator *pages)
{
    const WitU64 before = wit_pages_free_count(pages);
    const WitU64 large = 0x800000000ULL; /* 32 GiB, independent of guest RAM. */
    const WitU64 arena = WIT_USER_MEMORY_LIMIT - WIT_USER_MEMORY_BASE;
    WitU64 base[2], first[2], last[2], free_after_roots, address, result, held[WIT_USER_RESERVATION_CAPACITY];

    require(wit_user_space_create(&spaces[0], pages) && wit_user_space_create(&spaces[1], pages),
        "Memory test roots failed");
    free_after_roots = wit_pages_free_count(pages);
    for (WitU32 i = 0; i < 2; ++i) {
        expect(wit_user_memory_reserve(&spaces[i], large, 0x40000000, 0, &base[i]), WIT_STATUS_OK);
        require(base[i] == WIT_USER_MEMORY_BASE && spaces[i].OwnedCount == 1,
            "Reservation consumed frames or chose wrong address");
    }
    require(wit_pages_free_count(pages) == free_after_roots, "Sparse reserve consumed RAM");
    for (WitU32 i = 0; i < 2; ++i) {
        expect(wit_user_memory_commit(&spaces[i], base[i], 4096, 3), WIT_STATUS_OK);
        expect(wit_user_memory_commit(&spaces[i], base[i] + large - 4096, 4096, 3), WIT_STATUS_OK);
        first[i] = wit_user_space_physical(&spaces[i], base[i], 1, 0);
        last[i] = wit_user_space_physical(&spaces[i], base[i] + large - 4096, 1, 0);
        require(first[i] &&
                last[i] &&
                first[i] != last[i] &&
                spaces[i].OwnedCount == 8 &&
                !wit_user_space_physical(&spaces[i], base[i] + 4096, 0, 0),
            "Sparse commitment incorrect");
        *(WitU64 *)first[i] = 100 + i;
        *(WitU64 *)last[i] = 200 + i;
    }
    require(first[0] != first[1] && last[0] != last[1], "Dynamic pages shared between owners");
    expect(wit_user_memory_release(&spaces[0], base[0], 0), WIT_STATUS_OK);
    require(*(WitU64 *)first[1] == 101 && *(WitU64 *)last[1] == 201, "Release damaged peer memory");
    expect(wit_user_memory_commit(&spaces[0], base[1], 4096, 3), WIT_STATUS_NOT_RESERVED);
    expect(wit_user_memory_decommit(&spaces[1], base[1], large), WIT_STATUS_OK);
    require(wit_pages_free_count(pages) == free_after_roots, "Sparse decommit retained frames/tables");
    expect(wit_user_memory_release(&spaces[1], base[1], 0), WIT_STATUS_OK);
    wit_user_space_destroy(&spaces[1]);
    wit_console_write("[TEST-PASS] User.MemorySparseAndPrivate\n");

    expect(wit_user_memory_reserve(&spaces[0], arena, 4096, 0, &address), WIT_STATUS_OK);
    expect(wit_user_memory_reserve(&spaces[0], 4096, 4096, 0, &result), WIT_STATUS_NO_MEMORY);
    require(result == 0, "Failed reserve returned an address");
    base[0] = address + 0x40000000 - 4096; /* Cross both a PT and a PD boundary. */
    expect(wit_user_memory_commit(&spaces[0], base[0], 8192, 3), WIT_STATUS_OK);
    first[0] = wit_user_space_physical(&spaces[0], base[0], 1, 0);
    *(WitU64 *)first[0] = 0xABCDE;
    expect(wit_user_memory_decommit(&spaces[0], base[0] + 4096, 4096), WIT_STATUS_OK);
    expect(wit_user_memory_protect(&spaces[0], base[0], 8192, 0), WIT_STATUS_NOT_COMMITTED);
    require(wit_user_space_physical(&spaces[0], base[0], 1, 0) == first[0], "Protect failure was partial");
    free_after_roots = wit_pages_free_count(pages);
    expect(wit_user_memory_commit(&spaces[0], base[0], WIT_USER_PAGE_CAPACITY * 4096ULL, 1), WIT_STATUS_NO_MEMORY);
    require(wit_pages_free_count(pages) == free_after_roots &&
            *(WitU64 *)wit_user_space_physical(&spaces[0], base[0], 1, 0) == 0xABCDE &&
            !wit_user_space_physical(&spaces[0], base[0] + 4096, 0, 0),
        "Quota rollback changed memory");
    expect(wit_user_memory_commit(&spaces[0], base[0] + 4096, 4096, 3), WIT_STATUS_OK);
    expect(wit_user_memory_release(&spaces[0], address, 0), WIT_STATUS_OK);
    require(spaces[0].OwnedCount == 1, "Release retained empty tables");
    wit_console_write("[TEST-PASS] User.MemoryQuotaRollback\n");

    expect(wit_user_memory_reserve(&spaces[0], 0, 4096, 0, &result), WIT_STATUS_INVALID_ARGUMENT);
    expect(wit_user_memory_reserve(&spaces[0], 4097, 4096, 0, &result), WIT_STATUS_INVALID_ARGUMENT);
    expect(wit_user_memory_reserve(&spaces[0], 4096, 12288, 0, &result), WIT_STATUS_INVALID_ARGUMENT);
    expect(wit_user_memory_reserve(&spaces[0], ~4095ULL, 4096, 0, &result), WIT_STATUS_NO_MEMORY);
    for (WitU32 i = 0; i < WIT_USER_RESERVATION_CAPACITY; ++i) {
        expect(wit_user_memory_reserve(&spaces[0], 4096, 65536, 0, &held[i]), WIT_STATUS_OK);
        require(held[i] == WIT_USER_MEMORY_BASE + i * 65536ULL, "Reservation alignment/overlap error");
    }
    expect(wit_user_memory_reserve(&spaces[0], 4096, 4096, 0, &result), WIT_STATUS_NO_MEMORY);
    expect(wit_user_memory_release(&spaces[0], held[3], 0), WIT_STATUS_OK);
    expect(wit_user_memory_reserve(&spaces[0], 4096, 65536, 0, &result), WIT_STATUS_OK);
    require(result == held[3], "Reservation hole not reused");
    expect(wit_user_memory_commit(&spaces[0], held[0], 8192, 3), WIT_STATUS_NOT_RESERVED);
    expect(wit_user_memory_commit(&spaces[0], held[0] + 1, 4096, 3), WIT_STATUS_INVALID_ARGUMENT);
    expect(wit_user_memory_commit(&spaces[0], held[0], 0, 3), WIT_STATUS_INVALID_ARGUMENT);
    expect(wit_user_memory_commit(&spaces[0], held[0], ~4095ULL, 3), WIT_STATUS_BAD_ADDRESS);
    expect(wit_user_memory_commit(&spaces[0], held[0], 4096, 2), WIT_STATUS_INVALID_ARGUMENT);
    expect(wit_user_memory_commit(&spaces[0], held[0], 4096, 5), WIT_STATUS_INVALID_ARGUMENT);
    expect(wit_user_memory_decommit(&spaces[0], WIT_USER_DATA, 4096), WIT_STATUS_BAD_ADDRESS);
    expect(wit_user_memory_protect(&spaces[0], WIT_USER_CODE, 4096, 3), WIT_STATUS_BAD_ADDRESS);
    expect(wit_user_memory_release(&spaces[0], held[0] + 4096, 0), WIT_STATUS_NOT_RESERVED);
    require(spaces[0].OwnedCount == 1, "Rejected request consumed frames");
    for (WitU32 i = 0; i < WIT_USER_RESERVATION_CAPACITY; ++i) {
        expect(wit_user_memory_release(&spaces[0], held[i], 0), WIT_STATUS_OK);
    }
    /* Adjacent reservations still cannot be operated on as a single range. */
    expect(wit_user_memory_reserve(&spaces[0], 4096, 4096, 0, &address), WIT_STATUS_OK);
    expect(wit_user_memory_reserve(&spaces[0], 4096, 4096, 0, &result), WIT_STATUS_OK);
    require(result == address + 4096, "Adjacent reservation setup failed");
    expect(wit_user_memory_commit(&spaces[0], address, 8192, 3), WIT_STATUS_NOT_RESERVED);
    wit_user_space_destroy(&spaces[0]);
    require(wit_pages_free_count(pages) == before, "Reservation tests leaked");
    wit_console_write("[TEST-PASS] User.MemoryReservationErrors\n");
    fixed_and_partial(pages);
    physical_failure(pages);
}
