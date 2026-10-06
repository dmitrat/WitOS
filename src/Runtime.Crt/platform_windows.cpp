#include <windows.h>
#include <errno.h>
#include <share.h>
#include "crt.h"

/* The UCRT subset's platform functions on Windows, for the hosted builds (P6.4.h, P6.4.i2): SRW locks, the standard
 * handles, files through CreateFileW, the process heap, the system clock and threads. Windows errors map to errno as
 * UCRT maps them. Without UCRT, the per-thread errno is here as well. */
namespace WitCrt::Platform {

namespace {

bool Fail()
{
    errno = ErrnoFromOs(GetLastError());
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

unsigned long long CurrentThread()
{
    return GetCurrentThreadId();
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

void *AllocateZeroed(size_t size)
{
    return HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, size);
}

void *Reallocate(void *block, size_t size)
{
    return HeapReAlloc(GetProcessHeap(), 0, block, size);
}

void Free(void *block)
{
    HeapFree(GetProcessHeap(), 0, block);
}

char *NarrowEnvironment()
{
    wchar_t *block = GetEnvironmentStringsW();
    if (!block) {
        return nullptr;
    }
    size_t units = 0;
    while (block[units]) {
        while (block[units]) {
            ++units;
        }
        ++units;
    }
    ++units; // the block's final terminator
    const int bytes = WideCharToMultiByte(CP_ACP, 0, block, int(units), nullptr, 0, nullptr, nullptr);
    char *narrow = bytes > 0 ? static_cast<char *>(Allocate(size_t(bytes))) : nullptr;
    if (narrow && WideCharToMultiByte(CP_ACP, 0, block, int(units), narrow, bytes, nullptr, nullptr) != bytes) {
        Free(narrow);
        narrow = nullptr;
    }
    FreeEnvironmentStringsW(block);
    return narrow;
}

unsigned short CharacterType(wchar_t value)
{
    WORD type = 0;
    if (!GetStringTypeW(CT_CTYPE1, &value, 1, &type)) {
        Fatal();
    }
    return type;
}

bool UtcNow(__time64_t &seconds)
{
    FILETIME now;
    GetSystemTimeAsFileTime(&now);
    const unsigned long long ticks = (unsigned long long)now.dwHighDateTime << 32 | now.dwLowDateTime;
    seconds = __time64_t((ticks - 116444736000000000ULL) / 10000000ULL);
    return true;
}

void *CreateThread(
    void *security, unsigned stack, _beginthreadex_proc_type start, void *argument, unsigned flags, unsigned *id)
{
    DWORD thread = 0;
    // The procedure's signature is the start routine's on x64.
    HANDLE handle = ::CreateThread(static_cast<LPSECURITY_ATTRIBUTES>(security), stack,
        reinterpret_cast<LPTHREAD_START_ROUTINE>(start), argument, flags, &thread);
    if (!handle) {
        Fail();
        return nullptr;
    }
    if (id) {
        *id = thread;
    }
    return handle;
}

void ExitThread(unsigned code)
{
    ::ExitThread(code);
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
