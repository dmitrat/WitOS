#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <errno.h>
#include <string.h>
#include "crt.h"
#include "../Runtime.NativeAot/native_heap.witos.h"
#include "tls.h"
extern "C" {
#include "image.h"
}

/* The UCRT subset's platform functions in the guest (P6.4.h, P6.4.i2): the native lock and fail-fast, the process
 * console for standard output and standard error, the C family of the native heap, no UTC clock and threads through
 * the guest's CreateThread. Guest storage is read-only: opening a file for writing, removing and renaming report
 * EACCES. */
namespace WitCrt::Platform {

void Acquire(Lock &lock)
{
    wit_native_lock(reinterpret_cast<volatile WitU32 *>(&lock.Storage));
}

void Release(Lock &lock)
{
    wit_native_unlock(reinterpret_cast<volatile WitU32 *>(&lock.Storage));
}

void Fatal()
{
    wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
}

void *StandardHandle(unsigned index)
{
    return index == 1 || index == 2 ? reinterpret_cast<void *>(wit_native_process_console()) : nullptr;
}

bool Write(void *handle, const char *bytes, size_t count)
{
    while (count) {
        const size_t chunk = count > WIT_ABI_MAX_WRITE ? WIT_ABI_MAX_WRITE : count;
        WitU64 written = 0;
        if (wit_native_call(WIT_CALL_WRITE, reinterpret_cast<WitU64>(handle), reinterpret_cast<WitU64>(bytes), chunk,
                &written) != WIT_STATUS_OK ||
            written != chunk) {
            errno = EIO;
            return false;
        }
        bytes += chunk;
        count -= chunk;
    }
    return true;
}

void *Open(const wchar_t *, bool, int)
{
    errno = EACCES;
    return nullptr;
}

bool Close(void *)
{
    Fatal(); // no file is ever open
}

bool Remove(const wchar_t *)
{
    errno = EACCES;
    return false;
}

bool Rename(const wchar_t *, const wchar_t *)
{
    errno = EACCES;
    return false;
}

void *Allocate(size_t size)
{
    return wit_native_c_allocate(size);
}

void *AllocateZeroed(size_t size)
{
    void *block = wit_native_c_allocate(size);
    if (block) {
        memset(block, 0, size);
    }
    return block;
}

void *Reallocate(void *block, size_t size)
{
    const size_t old = wit_native_c_size(block);
    if (!old) {
        Fatal(); // not a block of malloc
    }
    void *moved = wit_native_c_allocate(size);
    if (moved) {
        memcpy(moved, block, old < size ? old : size);
        Free(block);
    }
    return moved;
}

void Free(void *block)
{
    if (!wit_native_c_release(block)) {
        Fatal(); // not a block of malloc
    }
}

bool UtcNow(__time64_t &)
{
    return false; // the guest has a monotonic clock only
}

void *CreateThread(
    void *security, unsigned stack, _beginthreadex_proc_type start, void *argument, unsigned flags, unsigned *id)
{
    DWORD thread = 0;
    // The procedure's signature is the start routine's on x64.
    HANDLE handle = ::CreateThread(static_cast<LPSECURITY_ATTRIBUTES>(security), stack,
        reinterpret_cast<LPTHREAD_START_ROUTINE>(start), argument, flags, &thread);
    if (!handle) {
        errno = ErrnoFromOs(GetLastError());
        return nullptr;
    }
    if (id) {
        *id = thread;
    }
    return handle;
}

void ExitThread(unsigned code)
{
    wit_native_thread_exit(code); // TLS, runtime and library notifications, as when the start function returns
}

} // namespace WitCrt::Platform
