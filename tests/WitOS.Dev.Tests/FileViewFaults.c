#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <string.h>
#include "file_view.h"
#ifdef HOST_LIBRARY_PAL_TEST
WitU64 host_library_call(WitU64, WitU64, WitU64, WitU64 *);
#endif
static void *allocation;
static WitU64 allocationSize;
static unsigned failure, reads, releases, opens, closes;
static int refuseRelease;
static WitU64 fileLength = 70001;
static char lastPath[1025];
static WitU32 lastPathBytes;

void file_view_model_length(WitU64 value)
{
    fileLength = value;
}

const char *file_view_model_path(void)
{
    return lastPath;
}

WitU32 file_view_model_path_bytes(void)
{
    return lastPathBytes;
}

unsigned file_view_model_handles(void)
{
    return opens - closes;
}

unsigned file_view_model_allocations(void)
{
    return allocation ? 1U : 0U;
}

int wit_native_try_lock(volatile WitU32 *value)
{
    return InterlockedCompareExchange((volatile LONG *)value, 1, 0) == 0;
}

void wit_native_unlock(volatile WitU32 *value)
{
    InterlockedExchange((volatile LONG *)value, 0);
}

WIT_NORETURN void wit_native_fail_fast(WitU64 code)
{
    puts("UNIT-FAILFAST: rollback release");
    fflush(stdout);
    ExitProcess(code == WIT_NATIVE_FAIL_FAST_EXIT ? 77 : 78);
}

WitU64 wit_native_call(WitU64 call, WitU64 a, WitU64 b, WitU64 c, WitU64 *result)
{
    if (result) {
        *result = 0;
    }
#ifdef HOST_LIBRARY_PAL_TEST
    if (call == WIT_CALL_LIBRARY) {
        return host_library_call(a, b, c, result);
    }
#endif
    if (call == WIT_CALL_FILE) {
        const WitFileRequest *r = (const WitFileRequest *)a;
        if (b != sizeof(*r) || c || r->Version != WIT_FILE_IO_VERSION || r->Size != sizeof(*r)) {
            return WIT_STATUS_BAD_HANDLE;
        }
        if (r->Operation == WIT_FILE_OPEN) {
            if (!r->Bytes || r->Bytes > 1024) {
                return WIT_STATUS_INVALID_ARGUMENT;
            }
            lastPathBytes = (WitU32)r->Bytes;
            memcpy(lastPath, (void *)r->Address, lastPathBytes);
            lastPath[lastPathBytes] = 0;
            for (WitU32 i = 0; i < lastPathBytes; ++i) {
                if (!lastPath[i]) {
                    return WIT_STATUS_INVALID_ARGUMENT;
                }
            }
            if (!strcmp(lastPath, "missing")) {
                return WIT_STATUS_NOT_FOUND;
            }
            ++opens;
            if (result) {
                *result = 17;
            }
            return WIT_STATUS_OK;
        }
        if (r->Handle != 17) {
            return WIT_STATUS_BAD_HANDLE;
        }
        if (r->Operation == WIT_FILE_LENGTH) {
            if (result) {
                *result = fileLength;
            }
            return WIT_STATUS_OK;
        }
        if (r->Operation != WIT_FILE_READ_AT ||
            r->Bytes > WIT_FILE_MAX_READ ||
            r->Offset > fileLength ||
            r->Bytes > fileLength - r->Offset ||
            r->Address < (WitU64)allocation ||
            r->Address - (WitU64)allocation > allocationSize ||
            r->Bytes > allocationSize - (r->Address - (WitU64)allocation)) {
            return WIT_STATUS_INVALID_ARGUMENT;
        }
        ++reads;
        if (failure == 3 || (failure == 4 && reads == 2)) {
            return WIT_STATUS_CLOSED;
        }
        for (WitU64 i = 0; i < r->Bytes; ++i) {
            ((WitU8 *)r->Address)[i] = (WitU8)((r->Offset + i) % 251);
        }
        if (result) {
            *result = r->Bytes;
        }
        return WIT_STATUS_OK;
    }
    if (call == WIT_CALL_STORAGE_QUERY) {
        const WitStorageQuery *r = (const WitStorageQuery *)a;
        if (b != sizeof(*r) ||
            r->Operation > WIT_STORAGE_LIST ||
            r->PathBytes > 1024 ||
            r->BufferBytes != sizeof(WitStorageInfo)) {
            return WIT_STATUS_INVALID_ARGUMENT;
        }
        lastPathBytes = (WitU32)r->PathBytes;
        memcpy(lastPath, (void *)r->Path, lastPathBytes);
        lastPath[lastPathBytes] = 0;
        if (!strcmp(lastPath, "missing")) {
            return WIT_STATUS_NOT_FOUND;
        }
        WitStorageInfo *info = (WitStorageInfo *)r->Buffer;
        if (r->Operation == WIT_STORAGE_LIST) {
            static const char *rootNames[] = {"file", "dir", "alpha.deps.json", "beta.json", "\xCE\xBB.txt"};
            static const char *childNames[] = {"sub", "note"};
            const char *const *names = rootNames;
            WitU32 count = 5;
            if (!strcmp(lastPath, "dir")) {
                names = childNames;
                count = 2;
            } else if (!strcmp(lastPath, "dir/sub")) {
                count = 0;
            } else if (r->PathBytes) {
                return WIT_STATUS_WRONG_TYPE;
            }
            if (r->Cursor > count) {
                return WIT_STATUS_INVALID_ARGUMENT;
            }
            if (r->Cursor == count) {
                return WIT_STATUS_OK;
            }
            const char *name = names[(WitU32)r->Cursor];
            memset(info, 0, sizeof(*info));
            info->Version = WIT_STORAGE_QUERY_VERSION;
            info->Size = sizeof(*info);
            info->Kind = (!strcmp(name, "dir") || !strcmp(name, "sub")) ? WIT_STORAGE_DIRECTORY : WIT_STORAGE_FILE;
            info->NameBytes = (WitU32)strlen(name);
            memcpy(info->Name, name, info->NameBytes);
            info->NextCursor = r->Cursor + 1;
        } else {
            memset(info, 0, sizeof(*info));
            info->Kind =
                (!r->PathBytes || !strcmp(lastPath, "app") || !strcmp(lastPath, "dir") || !strcmp(lastPath, "dir/sub"))
                ? WIT_STORAGE_DIRECTORY
                : WIT_STORAGE_FILE;
        }
        if (result) {
            *result = sizeof(*info);
        }
        return WIT_STATUS_OK;
    }
    if (call == WIT_CALL_CLOSE) {
        if (a != 17 || b || c) {
            return WIT_STATUS_BAD_HANDLE;
        }
        ++closes;
        return WIT_STATUS_OK;
    }
    if (call == WIT_CALL_MEMORY_RESERVE) {
        if (failure == 1) {
            return WIT_STATUS_NO_MEMORY;
        }
        if (allocation || !a || (a & 4095) || b != 4096 || c) {
            return WIT_STATUS_INVALID_ARGUMENT;
        }
        allocation = VirtualAlloc(0, (SIZE_T)a, MEM_RESERVE, PAGE_NOACCESS);
        if (!allocation) {
            return WIT_STATUS_NO_MEMORY;
        }
        allocationSize = a;
        if (result) {
            *result = (WitU64)allocation;
        }
        return WIT_STATUS_OK;
    }
    if (call == WIT_CALL_MEMORY_COMMIT) {
        if (a != (WitU64)allocation || b != allocationSize || c != 3) {
            return WIT_STATUS_INVALID_ARGUMENT;
        }
        if (failure == 2) {
            (void)VirtualAlloc(allocation, 4096, MEM_COMMIT, PAGE_READWRITE);
            return WIT_STATUS_NO_MEMORY;
        }
        return VirtualAlloc(allocation, (SIZE_T)b, MEM_COMMIT, PAGE_READWRITE) ? WIT_STATUS_OK : WIT_STATUS_NO_MEMORY;
    }
    if (call == WIT_CALL_MEMORY_PROTECT) {
        if (failure == 5) {
            return WIT_STATUS_DENIED;
        }
        DWORD old;
        if (a != (WitU64)allocation || b != allocationSize || c != 1) {
            return WIT_STATUS_INVALID_ARGUMENT;
        }
        return VirtualProtect(allocation, (SIZE_T)b, PAGE_READONLY, &old) ? WIT_STATUS_OK : WIT_STATUS_DENIED;
    }
    if (call == WIT_CALL_MEMORY_RELEASE) {
        if (refuseRelease) {
            return WIT_STATUS_BUSY;
        }
        if (a != (WitU64)allocation || !allocation || b || c || !VirtualFree(allocation, 0, MEM_RELEASE)) {
            return WIT_STATUS_INVALID_ARGUMENT;
        }
        allocation = 0;
        allocationSize = 0;
        ++releases;
        return WIT_STATUS_OK;
    }
    return WIT_STATUS_UNSUPPORTED;
}
#ifndef FILE_VIEW_PAL_TEST
int main(int argc, char **argv)
{
    if (argc == 2 && !strcmp(argv[1], "fatal")) {
        failure = 3;
        refuseRelease = 1;
        WitNativeFileView view;
        wit_native_file_view(17, 0, &view);
        return 1;
    }
    for (failure = 1; failure <= 5; ++failure) {
        reads = releases = opens = closes = 0;
        WitNativeFileView view = {99, (void *)77, 55};
        const WitU64 expected = failure <= 2 ? WIT_STATUS_NO_MEMORY
            : failure <= 4                   ? WIT_STATUS_CLOSED
                                             : WIT_STATUS_DENIED;
        const WitU64 status = wit_native_map_file("file", 4, 0, &view);
        if (status != expected ||
            view.Token != 99 ||
            view.Address != (void *)77 ||
            view.Length != 55 ||
            allocation ||
            opens != 1 ||
            closes != 1 ||
            releases != (failure == 1 ? 0U : 1U)) {
            printf("FAIL stage=%u status=%llu releases=%u\n", failure, status, releases);
            return 2;
        }
    }
    failure = 0;
    reads = releases = 0;
    WitNativeFileView view = {0};
    if (wit_native_file_view(17, 0, &view) != WIT_STATUS_OK || reads != 2 || view.Length != fileLength) {
        return 3;
    }
    for (WitU64 i = 0; i < fileLength; ++i) {
        if (((WitU8 *)view.Address)[i] != (WitU8)(i % 251)) {
            return 4;
        }
    }
    for (WitU64 i = fileLength; i < allocationSize; ++i) {
        if (((WitU8 *)view.Address)[i]) {
            return 5;
        }
    }
    MEMORY_BASIC_INFORMATION info;
    if (!VirtualQuery(view.Address, &info, sizeof(info)) || info.Protect != PAGE_READONLY) {
        return 6;
    }
    refuseRelease = 1;
    if (wit_native_file_unview(&view) != WIT_STATUS_BUSY || !allocation) {
        return 7;
    }
    refuseRelease = 0;
    if (wit_native_file_unview(&view) != WIT_STATUS_OK ||
        allocation ||
        wit_native_file_unview(&view) != WIT_STATUS_BAD_HANDLE) {
        return 8;
    }
    fileLength = 0;
    view = (WitNativeFileView){99, (void *)77, 55};
    if (wit_native_file_view(17, 0, &view) != WIT_STATUS_INVALID_ARGUMENT || view.Token != 99) {
        return 9;
    }
    fileLength = ~0ULL;
    if (wit_native_file_view(17, 0, &view) != WIT_STATUS_TOO_LARGE || view.Token != 99) {
        return 10;
    }
    puts("PASS: native file-view reserve/commit/read/protect rollback, release retry, byte/protection checks and "
         "bounds");
    return 0;
}

#endif
