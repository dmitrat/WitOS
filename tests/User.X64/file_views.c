#include "file_view.h"
#include "../User/protocol.h"
#include "../../src/Kernel/include/witos/user_layout.h"
#pragma optimize("", off)
#define CHECK(value, code) \
    do { \
        if (!(value)) return (code); \
    } while (0)

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
        a->PrivatePageTableBytes == b->PrivatePageTableBytes &&
        a->ReservationCount == b->ReservationCount;
}

static const char path[] = "app/CoreClrProbe.dll";
static WitU8 bytes[4096];
static WitNativeFileView worker_view;

static WIT_NORETURN void view_worker(WitU64 argument)
{
    (void)argument;
    const WitU64 status = wit_native_map_file(path, sizeof(path) - 1, WIT_FILE_VIEW_READONLY, &worker_view);
    wit_native_call(WIT_CALL_THREAD_EXIT, status == WIT_STATUS_OK ? 42 : 2250, 0, 0, 0);
    wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}

WitU64 wit_file_views_test(WitU64 mode)
{
    WitU64 file = 0, position = 0, read = 0;
    WitUserMemoryInfo before, after;
    volatile WitU64 *report = (volatile WitU64 *)WIT_GC_INFO_REPORT;
    CHECK(snapshot(&before), 2201);
    CHECK(wit_native_file_open(path, sizeof(path) - 1, &file) == WIT_STATUS_OK, 2202);
    if (mode == 4 || mode == 5) {
        WitNativeFileView fault;
        CHECK(wit_native_file_view(file, mode == 4 ? WIT_FILE_VIEW_READONLY : WIT_FILE_VIEW_PRIVATE, &fault) ==
                WIT_STATUS_OK,
            2203);
        CHECK(wit_native_file_close(file) == WIT_STATUS_OK, 2204);
        report[4] = (WitU64)fault.Address;
        if (mode == 4) {
            *(volatile WitU8 *)fault.Address = 0;
        } else {
            WitU8 *code = (WitU8 *)fault.Address;
            code[0] = 0xB8;
            code[1] = 42;
            code[2] = code[3] = code[4] = 0;
            code[5] = 0xC3;
            return ((WitU64 (*)(void))code)();
        }
        return 2299;
    }
    if (mode == 6) {
        WitNativeFileView view = {99, (void *)77, 55};
        const WitU64 status = wit_native_file_view(file, WIT_FILE_VIEW_READONLY, &view);
        report[4] = status;
        if (status == WIT_STATUS_OK) {
            CHECK(wit_native_file_unview(&view) == WIT_STATUS_OK, 2205);
        } else {
            CHECK(status == WIT_STATUS_NO_MEMORY && view.Token == 99 && view.Address == (void *)77 && view.Length == 55,
                2206);
        }
        CHECK(wit_native_file_close(file) == WIT_STATUS_OK && snapshot(&after) && same(&before, &after), 2207);
        return status == WIT_STATUS_OK ? 42 : 43;
    }
    WitNativeFileView ro = {0}, copy = {0};
    CHECK(wit_native_file_seek(file, 3, WIT_FILE_SEEK_BEGIN, &position) == WIT_STATUS_OK, 2210);
    CHECK(wit_native_map_file(path, sizeof(path) - 1, WIT_FILE_VIEW_READONLY, &ro) == WIT_STATUS_OK &&
            wit_native_file_view(file, WIT_FILE_VIEW_PRIVATE, &copy) == WIT_STATUS_OK,
        2211);
    CHECK(ro.Length == copy.Length && ro.Address != copy.Address && ro.Token != copy.Token, 2212);
    CHECK(wit_native_file_seek(file, 0, WIT_FILE_SEEK_CURRENT, &position) == WIT_STATUS_OK && position == 3, 2213);
    for (WitU64 offset = 0; offset < ro.Length;) {
        WitU32 wanted = (WitU32)(ro.Length - offset);
        if (wanted > sizeof(bytes)) {
            wanted = sizeof(bytes);
        }
        CHECK(wit_native_file_read_at(file, bytes, wanted, offset, &read) == WIT_STATUS_OK && read == wanted, 2214);
        for (WitU32 i = 0; i < wanted; ++i) {
            CHECK(((const WitU8 *)ro.Address)[offset + i] == bytes[i] &&
                    ((const WitU8 *)copy.Address)[offset + i] == bytes[i],
                2215);
        }
        offset += wanted;
    }
    CHECK(wit_native_file_close(file) == WIT_STATUS_OK, 2216);
    CHECK(((const WitU8 *)ro.Address)[0] == 'M' && ((const WitU8 *)copy.Address)[1] == 'Z', 2217);
    ((WitU8 *)copy.Address)[0] = 'X';
    CHECK(((const WitU8 *)ro.Address)[0] == 'M', 2218);
    CHECK(wit_native_file_open(path, sizeof(path) - 1, &file) == WIT_STATUS_OK &&
            wit_native_file_read(file, bytes, 1, &read) == WIT_STATUS_OK &&
            read == 1 &&
            bytes[0] == 'M',
        2219);
    WitNativeFileView forged = ro;
    forged.Length++;
    CHECK(wit_native_file_unview(&forged) == WIT_STATUS_INVALID_ARGUMENT, 2220);
    const WitU64 rounded = (ro.Length + 4095) & ~4095ULL;
    WitU64 alias = 0;
    WitCodeMemoryRequest request = {
        WIT_CODE_MEMORY_VERSION, sizeof(request), WIT_CODE_RESERVE, 0, 0, 0, rounded, 4096, 0, ~0ULL};
    CHECK(wit_native_call(WIT_CALL_CODE_MEMORY, (WitU64)&request, sizeof(request), 0, &alias) == WIT_STATUS_OK, 2238);
    request = (WitCodeMemoryRequest){WIT_CODE_MEMORY_VERSION, sizeof(request), WIT_CODE_ALIAS, WIT_CODE_READ_EXECUTE,
        alias, (WitU64)ro.Address, rounded, 0, 0, 0};
    CHECK(wit_native_call(WIT_CALL_CODE_MEMORY, (WitU64)&request, sizeof(request), 0, 0) == WIT_STATUS_OK, 2239);
    CHECK(wit_native_file_unview(&ro) == WIT_STATUS_BUSY &&
            ((const WitU8 *)ro.Address)[0] == 'M' &&
            ((const WitU8 *)alias)[0] == 'M',
        2240);
    CHECK(wit_native_call(WIT_CALL_MEMORY_RELEASE, alias, 0, 0, 0) == WIT_STATUS_OK, 2241);
    // Unmap must copy its descriptor before releasing the view that holds it.
    *(WitNativeFileView *)copy.Address = copy;
    CHECK(wit_native_file_unview((const WitNativeFileView *)copy.Address) == WIT_STATUS_OK, 2242);
    CHECK(wit_native_file_unview(&ro) == WIT_STATUS_OK, 2221);
    WitNativeFileView reused = {0};
    CHECK(wit_native_file_view(file, WIT_FILE_VIEW_READONLY, &reused) == WIT_STATUS_OK &&
            reused.Address == ro.Address &&
            reused.Token != ro.Token,
        2222);
    CHECK(wit_native_file_unview(&ro) == WIT_STATUS_BAD_HANDLE && ((const WitU8 *)reused.Address)[0] == 'M', 2223);
    CHECK(wit_native_unmap_file(reused.Address, reused.Length + 1) == WIT_STATUS_INVALID_ARGUMENT, 2251);
    CHECK(wit_native_unmap_file(reused.Address, reused.Length) == WIT_STATUS_OK, 2224);
    CHECK(wit_native_unmap_file(reused.Address, reused.Length) == WIT_STATUS_BAD_HANDLE, 2252);
    WitNativeFileView slots[WIT_FILE_VIEW_CAPACITY], failed = {99, (void *)77, 55};
    for (WitU32 i = 0; i < WIT_FILE_VIEW_CAPACITY; ++i) {
        CHECK(wit_native_file_view(file, WIT_FILE_VIEW_READONLY, &slots[i]) == WIT_STATUS_OK, 2225);
    }
    CHECK(wit_native_file_view(file, WIT_FILE_VIEW_READONLY, &failed) == WIT_STATUS_NO_MEMORY &&
            failed.Token == 99 &&
            failed.Address == (void *)77 &&
            failed.Length == 55,
        2226);
    for (WitU32 i = 0; i < WIT_FILE_VIEW_CAPACITY; ++i) {
        CHECK(wit_native_file_unview(&slots[i]) == WIT_STATUS_OK, 2227);
    }
    CHECK(wit_native_file_view(file, 99, &failed) == WIT_STATUS_UNSUPPORTED && failed.Token == 99, 2228);
    // Exhaust backing while retaining room for a reservation, then release it
    // and require the same native view registry to recover successfully.
    WitU64 hog = 0;
    CHECK(wit_native_call(WIT_CALL_MEMORY_RESERVE, 1024 * 1024, 4096, 0, &hog) == WIT_STATUS_OK, 2229);
    WitU32 pages = 0;
    for (; pages < 256; ++pages) {
        WitU64 status = wit_native_call(WIT_CALL_MEMORY_COMMIT, hog + (WitU64)pages * 4096, 4096, 3, 0);
        if (status == WIT_STATUS_NO_MEMORY) {
            break;
        }
        CHECK(status == WIT_STATUS_OK, 2230);
    }
    CHECK(pages > 0 &&
            pages < 256 &&
            wit_native_file_view(file, WIT_FILE_VIEW_READONLY, &failed) == WIT_STATUS_NO_MEMORY &&
            failed.Token == 99,
        2231);
    CHECK(wit_native_call(WIT_CALL_MEMORY_RELEASE, hog, 0, 0, 0) == WIT_STATUS_OK, 2232);
    CHECK(wit_native_file_view(file, WIT_FILE_VIEW_READONLY, &reused) == WIT_STATUS_OK &&
            wit_native_file_unview(&reused) == WIT_STATUS_OK,
        2233);
    WitU64 worker = 0, workerResult = 0;
    CHECK(wit_native_call(WIT_CALL_THREAD_CREATE_SIMPLE, (WitU64)view_worker, 0, 0, &worker) == WIT_STATUS_OK, 2246);
    CHECK(wit_native_call(WIT_CALL_THREAD_JOIN, worker, 0, 0, &workerResult) == WIT_STATUS_OK && workerResult == 42,
        2247);
    CHECK(
        ((const WitU8 *)worker_view.Address)[0] == 'M' && wit_native_file_unview(&worker_view) == WIT_STATUS_OK, 2248);
    CHECK(wit_native_file_close(file) == WIT_STATUS_OK, 2234);
    CHECK(wit_native_file_view(file, WIT_FILE_VIEW_READONLY, &failed) == WIT_STATUS_BAD_HANDLE && failed.Token == 99,
        2235);
    CHECK(wit_native_file_open("test/empty", 10, &file) == WIT_STATUS_OK &&
            wit_native_file_view(file, WIT_FILE_VIEW_READONLY, &failed) == WIT_STATUS_INVALID_ARGUMENT &&
            failed.Token == 99,
        2236);
    CHECK(wit_native_file_close(file) == WIT_STATUS_OK && snapshot(&after) && same(&before, &after), 2237);
    CHECK(wit_native_map_file("missing", 7, WIT_FILE_VIEW_READONLY, &failed) == WIT_STATUS_NOT_FOUND &&
            failed.Token == 99,
        2243);
    CHECK(wit_native_map_file("test/empty", 10, WIT_FILE_VIEW_READONLY, &failed) == WIT_STATUS_INVALID_ARGUMENT &&
            failed.Token == 99,
        2244);
    CHECK(snapshot(&after) && same(&before, &after), 2245);
    return 42;
}
