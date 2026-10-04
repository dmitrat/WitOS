#include <windows.h>
#include <errno.h>
#include <share.h>
#include "crt.h"

/* The UCRT subset's platform functions on Windows, for the hosted builds (P6.4.h): SRW locks, the standard handles,
 * files through CreateFileW, the process heap and the system clock. Windows errors map to errno as UCRT maps them.
 * Without UCRT, the per-thread errno is here as well. */
namespace WitCrt::Platform {

namespace {

int Errno(DWORD error)
{
    switch (error) {
    case ERROR_FILE_NOT_FOUND:
    case ERROR_PATH_NOT_FOUND:
    case ERROR_INVALID_DRIVE:
    case ERROR_NO_MORE_FILES:
    case ERROR_BAD_NETPATH:
    case ERROR_BAD_NET_NAME:
    case ERROR_INVALID_NAME:
    case ERROR_FILENAME_EXCED_RANGE:
        return ENOENT;
    case ERROR_TOO_MANY_OPEN_FILES:
        return EMFILE;
    case ERROR_ACCESS_DENIED:
    case ERROR_CURRENT_DIRECTORY:
    case ERROR_NETWORK_ACCESS_DENIED:
    case ERROR_CANNOT_MAKE:
    case ERROR_FAIL_I24:
        return EACCES;
    case ERROR_INVALID_HANDLE:
        return EBADF;
    case ERROR_NOT_ENOUGH_MEMORY:
    case ERROR_OUTOFMEMORY:
        return ENOMEM;
    case ERROR_NOT_SAME_DEVICE:
        return EXDEV;
    case ERROR_FILE_EXISTS:
    case ERROR_ALREADY_EXISTS:
        return EEXIST;
    case ERROR_DISK_FULL:
        return ENOSPC;
    case ERROR_BROKEN_PIPE:
        return EPIPE;
    case ERROR_DIR_NOT_EMPTY:
        return ENOTEMPTY;
    default:
        return error >= ERROR_WRITE_PROTECT && error <= ERROR_SHARING_BUFFER_EXCEEDED ? EACCES : EINVAL;
    }
}

bool Fail()
{
    errno = Errno(GetLastError());
    return false;
}

} // namespace

void Acquire(Lock &lock)
{
    AcquireSRWLockExclusive(reinterpret_cast<SRWLOCK *>(&lock.Storage));
}

void Release(Lock &lock)
{
    ReleaseSRWLockExclusive(reinterpret_cast<SRWLOCK *>(&lock.Storage));
}

void Fatal()
{
    __fastfail(FAST_FAIL_INVALID_ARG);
}

void *StandardHandle(unsigned index)
{
    if (index != 1 && index != 2) {
        return nullptr;
    }
    HANDLE handle = GetStdHandle(index == 1 ? STD_OUTPUT_HANDLE : STD_ERROR_HANDLE);
    return handle == INVALID_HANDLE_VALUE ? nullptr : handle;
}

bool Write(void *handle, const char *bytes, size_t count)
{
    while (count) {
        const DWORD chunk = count > 0x40000000 ? 0x40000000 : DWORD(count);
        DWORD written = 0;
        if (!WriteFile(handle, bytes, chunk, &written, nullptr)) {
            return Fail();
        }
        if (written != chunk) {
            errno = ENOSPC;
            return false;
        }
        bytes += chunk;
        count -= chunk;
    }
    return true;
}

void *Open(const wchar_t *path, bool append, int share)
{
    const DWORD sharing = share == _SH_DENYNO ? FILE_SHARE_READ | FILE_SHARE_WRITE
        : share == _SH_DENYWR                 ? FILE_SHARE_READ
        : share == _SH_DENYRD                 ? FILE_SHARE_WRITE
                                              : 0;
    HANDLE handle = CreateFileW(path, append ? FILE_APPEND_DATA : GENERIC_WRITE, sharing, nullptr,
        append ? OPEN_ALWAYS : CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (handle == INVALID_HANDLE_VALUE) {
        Fail();
        return nullptr;
    }
    return handle;
}

bool Close(void *handle)
{
    return CloseHandle(handle) || Fail();
}

bool Remove(const wchar_t *path)
{
    return DeleteFileW(path) || Fail();
}

bool Rename(const wchar_t *from, const wchar_t *to)
{
    return MoveFileExW(from, to, MOVEFILE_COPY_ALLOWED) || Fail();
}

void *Allocate(size_t size)
{
    return HeapAlloc(GetProcessHeap(), 0, size);
}

void *Reallocate(void *block, size_t size)
{
    return HeapReAlloc(GetProcessHeap(), 0, block, size);
}

void Free(void *block)
{
    HeapFree(GetProcessHeap(), 0, block);
}

bool UtcNow(__time64_t &seconds)
{
    FILETIME now;
    GetSystemTimeAsFileTime(&now);
    const unsigned long long ticks = (unsigned long long)now.dwHighDateTime << 32 | now.dwLowDateTime;
    seconds = __time64_t((ticks - 116444736000000000ULL) / 10000000ULL);
    return true;
}

} // namespace WitCrt::Platform

#ifndef WITCRT_REFERENCE
namespace {
thread_local int error;
} // namespace

extern "C" int *__cdecl _errno()
{
    return &error;
}
#endif
