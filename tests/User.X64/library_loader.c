#include "library.h"
#include "protocol.h"
#include "../../src/Kernel.Arch.X64/user_layout.h"
#pragma optimize("", off)
#define CHECK(value, code) \
    do { \
        if (!(value)) return code; \
    } while (0)
static const char path[] = "/native/lib.dll";

static int snapshot(WitUserMemoryInfo *value)
{
    return wit_native_call(WIT_CALL_MEMORY_QUERY, (WitU64)value, sizeof(*value), WIT_MEMORY_INFO_VERSION, 0) ==
        WIT_STATUS_OK;
}

static int same(const WitUserMemoryInfo *a, const WitUserMemoryInfo *b)
{
    return a->OwnedBytes == b->OwnedBytes &&
        a->PhysicalAvailableBytes == b->PhysicalAvailableBytes &&
        a->ReservedBytes == b->ReservedBytes &&
        a->DynamicCommittedBytes == b->DynamicCommittedBytes &&
        a->ReservationCount == b->ReservationCount &&
        a->PrivatePageTableBytes == b->PrivatePageTableBytes;
}

WitU64 wit_native_library_test(WitU64 mode)
{
    WitUserMemoryInfo before, after;
    CHECK(snapshot(&before), 2601);
    WitU64 library = 99, address = 0;
    if (!mode) {
        WitU64 absent = 99;
        CHECK(wit_native_library_find("lib.dll", 7, 1, &absent) == WIT_STATUS_NOT_FOUND && absent == 99, 2640);
    }
    const WitU64 loaded = wit_native_library_load(path, sizeof(path) - 1, &library);
    if (mode == 7) {
        if (loaded == WIT_STATUS_OK) {
            CHECK(wit_native_library_unload(library) == WIT_STATUS_OK, 2602);
        } else {
            CHECK(loaded == WIT_STATUS_NO_MEMORY && library == 99, 2603);
        }
        CHECK(snapshot(&after) && same(&before, &after), 2604);
        return loaded == WIT_STATUS_OK ? 42 : 43;
    }
    CHECK(loaded == WIT_STATUS_OK, 2605);
    WitLibraryInfo info;
    CHECK(wit_native_library_info(library, &info) == WIT_STATUS_OK &&
            info.Version == WIT_LIBRARY_VERSION &&
            info.Size == sizeof(info) &&
            info.References == 1 &&
            info.Base &&
            info.ImageBytes &&
            !info.EntryRva,
        2606);
    CHECK(wit_native_library_symbol(library, "LibraryAdd", 10, 0, &address) == WIT_STATUS_OK &&
            ((int (*)(int, int))address)(731, 11) == 742,
        2607);
    if (mode == 8) {
        ((volatile WitU64 *)WIT_GC_INFO_REPORT)[4] = address;
        *(volatile WitU8 *)address = 0;
        return 2699;
    }
    WitU64 alias = 0;
    CHECK(wit_native_library_symbol(library, "AliasAdd", 8, 0, &alias) == WIT_STATUS_OK && alias == address, 2608);
    CHECK(
        wit_native_library_symbol(library, 0, 0, 12, &alias) == WIT_STATUS_OK && ((int (*)(void))alias)() == 731, 2609);
    WitU64 data = 0, pointer = 0;
    CHECK(
        wit_native_library_symbol(library, "LibraryData", 11, 0, &data) == WIT_STATUS_OK && *(int *)data == 731, 2610);
    CHECK(wit_native_library_symbol(library, "LibraryPointer", 14, 0, &pointer) == WIT_STATUS_OK &&
            *(WitU64 *)pointer == data,
        2611);
    *(int *)data = 19;
    CHECK(((int (*)(int, int))address)(731, 11) == 30, 2612);
    *(int *)data = 731;
    alias = 99;
    CHECK(wit_native_library_symbol(library, "libraryadd", 10, 0, &alias) == WIT_STATUS_NOT_FOUND && alias == 99, 2613);
    CHECK(wit_native_library_symbol(library, 0, 0, 11, &alias) == WIT_STATUS_NOT_FOUND && alias == 99, 2614);
    CHECK(wit_native_call(WIT_CALL_CLOSE, library, 0, 0, 0) == WIT_STATUS_WRONG_TYPE, 2615);
    CHECK(wit_native_call(WIT_CALL_MEMORY_PROTECT, info.Base, 4096, 3, 0) == WIT_STATUS_DENIED &&
            wit_native_call(WIT_CALL_MEMORY_DECOMMIT, info.Base, 4096, 0, 0) == WIT_STATUS_DENIED &&
            wit_native_call(WIT_CALL_MEMORY_COMMIT, info.Base, 4096, 3, 0) == WIT_STATUS_DENIED &&
            wit_native_call(WIT_CALL_MEMORY_RESET, info.Base, 4096, 0, 0) == WIT_STATUS_DENIED &&
            wit_native_call(WIT_CALL_MEMORY_RELEASE, info.Base, 0, 0, 0) == WIT_STATUS_DENIED,
        2616);
    // A writable alias must never bypass final image protections, in either direction.
    WitU64 scratch = 0;
    CHECK(wit_native_call(WIT_CALL_MEMORY_RESERVE, 4096, 4096, 0, &scratch) == WIT_STATUS_OK, 2627);
    CHECK(wit_native_call(WIT_CALL_MEMORY_COMMIT, scratch, 4096, 3, 0) == WIT_STATUS_OK, 2628);
    WitCodeMemoryRequest request = {
        WIT_CODE_MEMORY_VERSION, sizeof(request), WIT_CODE_ALIAS, 3, scratch, address & ~4095ULL, 4096, 0, 0, 0};
    CHECK(wit_native_call(WIT_CALL_CODE_MEMORY, (WitU64)&request, sizeof(request), 0, 0) == WIT_STATUS_DENIED, 2629);
    request.Operation = WIT_CODE_MAP_SPARSE;
    CHECK(wit_native_call(WIT_CALL_CODE_MEMORY, (WitU64)&request, sizeof(request), 0, 0) == WIT_STATUS_DENIED, 2630);
    request.Source = scratch;
    request.Address = info.Base;
    CHECK(wit_native_call(WIT_CALL_CODE_MEMORY, (WitU64)&request, sizeof(request), 0, 0) == WIT_STATUS_DENIED, 2631);
    request.Operation = WIT_CODE_ALIAS;
    CHECK(wit_native_call(WIT_CALL_CODE_MEMORY, (WitU64)&request, sizeof(request), 0, 0) == WIT_STATUS_DENIED, 2632);
    request.Source = 0;
    request.Operation = WIT_CODE_PROTECT;
    CHECK(wit_native_call(WIT_CALL_CODE_MEMORY, (WitU64)&request, sizeof(request), 0, 0) == WIT_STATUS_DENIED, 2633);
    request.Operation = WIT_CODE_RESET_SPARSE;
    request.Protection = 0;
    CHECK(wit_native_call(WIT_CALL_CODE_MEMORY, (WitU64)&request, sizeof(request), 0, 0) == WIT_STATUS_DENIED, 2634);
    CHECK(wit_native_call(WIT_CALL_MEMORY_RELEASE, scratch, 0, 0, 0) == WIT_STATUS_OK, 2635);
    CHECK(((int (*)(int, int))address)(731, 11) == 742 && *(int *)data == 731, 2636);
    WitU64 second = 0;
    CHECK(wit_native_library_load(path, sizeof(path) - 1, &second) == WIT_STATUS_OK && second == library, 2617);
    CHECK(wit_native_library_info(library, &info) == WIT_STATUS_OK && info.References == 2, 2618);
    CHECK(wit_native_library_unload(second) == WIT_STATUS_OK && ((int (*)(int, int))address)(731, 11) == 742, 2619);
    CHECK(wit_native_library_info(library, &info) == WIT_STATUS_OK && info.References == 1, 2620);
    CHECK(wit_native_library_info(library, (WitLibraryInfo *)(WIT_USER_DATA_END - 8)) == WIT_STATUS_BAD_ADDRESS, 2621);
    WitLibraryPath origin;
    CHECK(wit_native_library_path(library, &origin) == WIT_STATUS_OK &&
            origin.Version == WIT_LIBRARY_VERSION &&
            origin.Size == sizeof(origin) &&
            origin.NameBytes == sizeof(path) - 2 &&
            !origin.Reserved,
        2641);
    for (WitU32 i = 0; i < origin.NameBytes; ++i) {
        CHECK(origin.Name[i] == (WitU8)path[i + 1], 2642);
    }
    for (WitU32 i = origin.NameBytes; i < sizeof(origin.Name); ++i) {
        CHECK(!origin.Name[i], 2643);
    }
    WitU64 found = 99;
    CHECK(wit_native_library_find("lib.dll", 7, 1, &found) == WIT_STATUS_OK && found == library, 2644);
    CHECK(wit_native_library_info(library, &info) == WIT_STATUS_OK && info.References == 2, 2645);
    CHECK(wit_native_library_unload(found) == WIT_STATUS_OK, 2646);
    const WitU64 findStatus = wit_native_library_find("/native/./lib.dll", sizeof("/native/./lib.dll") - 1, 0, &found);
    if (findStatus != WIT_STATUS_OK) {
        return 2700 + findStatus;
    }
    CHECK(found == library, 2647);
    CHECK(wit_native_library_unload(found) == WIT_STATUS_OK, 2648);
    found = 99;
    CHECK(wit_native_library_find("LIB.dll", 7, 1, &found) == WIT_STATUS_NOT_FOUND && found == 99, 2649);
    CHECK(wit_native_library_find("native/lib.dll", 14, 1, &found) == WIT_STATUS_INVALID_ARGUMENT && found == 99, 2650);
    CHECK(wit_native_library_find((const char *)WIT_USER_DATA_END - 1, 7, 1, &found) == WIT_STATUS_BAD_ADDRESS &&
            found == 99,
        2651);
    volatile WitU8 *tail = (volatile WitU8 *)(WIT_USER_DATA_END - 16);
    for (WitU32 i = 0; i < 16; ++i) {
        tail[i] = 0xA5;
    }
    CHECK(wit_native_library_path(library, (WitLibraryPath *)tail) == WIT_STATUS_BAD_ADDRESS, 2652);
    for (WitU32 i = 0; i < 16; ++i) {
        CHECK(tail[i] == 0xA5, 2653);
    }
    WitLibraryRequest bad = {
        WIT_LIBRARY_VERSION, sizeof(bad), WIT_LIBRARY_PATH, 0, library, 0, 0, 0, (WitU64)&origin, sizeof(origin) - 1};
    origin.NameBytes = 77;
    CHECK(wit_native_call(WIT_CALL_LIBRARY, (WitU64)&bad, sizeof(bad), 0, 0) == WIT_STATUS_INVALID_ARGUMENT &&
            origin.NameBytes == 77,
        2654);
    WitU64 duplicate = 0;
    CHECK(wit_native_library_load("/test/lib.dll", 13, &duplicate) == WIT_STATUS_OK && duplicate != library, 2655);
    CHECK(wit_native_library_find("lib.dll", 7, 1, &found) == WIT_STATUS_BUSY && found == 99, 2656);
    CHECK(wit_native_library_info(library, &info) == WIT_STATUS_OK && info.References == 1, 2657);
    CHECK(wit_native_library_info(duplicate, &info) == WIT_STATUS_OK && info.References == 1, 2658);
    CHECK(wit_native_library_find(path, sizeof(path) - 1, 0, &found) == WIT_STATUS_OK && found == library, 2659);
    CHECK(wit_native_library_unload(found) == WIT_STATUS_OK && wit_native_library_unload(duplicate) == WIT_STATUS_OK,
        2660);
    CHECK(wit_native_library_unload(library) == WIT_STATUS_OK, 2622);
    origin.NameBytes = 77;
    found = 99;
    CHECK(wit_native_library_path(library, &origin) == WIT_STATUS_BAD_HANDLE && origin.NameBytes == 77, 2661);
    CHECK(wit_native_library_find("lib.dll", 7, 1, &found) == WIT_STATUS_NOT_FOUND && found == 99, 2662);
    CHECK(wit_native_library_symbol(library, "LibraryAdd", 10, 0, &address) == WIT_STATUS_BAD_HANDLE &&
            wit_native_library_unload(library) == WIT_STATUS_BAD_HANDLE,
        2623);
    CHECK(snapshot(&after) && same(&before, &after), 2624);
    CHECK(wit_native_library_load(path, sizeof(path) - 1, &second) == WIT_STATUS_OK && second != library, 2625);
    CHECK(wit_native_library_unload(second) == WIT_STATUS_OK && snapshot(&after) && same(&before, &after), 2626);
    return 42;
}
