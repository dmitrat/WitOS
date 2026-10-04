#include "file_view.h"

typedef struct ViewRecord {
    WitNativeFileView View;
    WitU64 Reserved;
    WitU32 Busy;
} ViewRecord;

static ViewRecord views[WIT_FILE_VIEW_CAPACITY];
static volatile WitU32 gate;
static WitU64 next_token = 1;

static void lock(void)
{
    wit_native_lock(&gate);
}

static void unlock(void)
{
    wit_native_unlock(&gate);
}

static void discard(WitU32 slot, WitU64 address)
{
    if (address && wit_native_call(WIT_CALL_MEMORY_RELEASE, address, 0, 0, 0) != WIT_STATUS_OK) {
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    lock();
    const ViewRecord empty = {0};
    views[slot] = empty;
    unlock();
}

WitU64 wit_native_file_view(WitU64 handle, WitU32 mode, WitNativeFileView *output)
{
    if (!output) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (mode != WIT_FILE_VIEW_READONLY && mode != WIT_FILE_VIEW_PRIVATE) {
        return WIT_STATUS_UNSUPPORTED;
    }
    WitU64 length = 0;
    WitU64 status = wit_native_file_length(handle, &length);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    if (!length) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (length > ~0ULL - 4095) {
        return WIT_STATUS_TOO_LARGE;
    }
    const WitU64 reserved = (length + 4095) & ~4095ULL;
    lock();
    WitU32 slot = WIT_FILE_VIEW_CAPACITY;
    if (next_token) {
        for (WitU32 i = 0; i < WIT_FILE_VIEW_CAPACITY; ++i) {
            if (!views[i].Busy) {
                slot = i;
                views[i].Busy = 1;
                break;
            }
        }
    }
    unlock();
    if (slot == WIT_FILE_VIEW_CAPACITY) {
        return WIT_STATUS_NO_MEMORY;
    }
    WitU64 address = 0;
    status = wit_native_call(WIT_CALL_MEMORY_RESERVE, reserved, 4096, 0, &address);
    if (status == WIT_STATUS_OK) {
        status = wit_native_call(WIT_CALL_MEMORY_COMMIT, address, reserved, WIT_MEMORY_READ | WIT_MEMORY_WRITE, 0);
    }
    if (status != WIT_STATUS_OK) {
        discard(slot, address);
        return status;
    }
    for (WitU64 offset = 0; offset < length;) {
        const WitU32 bytes = (WitU32)(length - offset > WIT_FILE_MAX_READ ? WIT_FILE_MAX_READ : length - offset);
        WitU64 copied = 0;
        status = wit_native_file_read_at(handle, (void *)(address + offset), bytes, offset, &copied);
        if (status != WIT_STATUS_OK || copied != bytes) {
            discard(slot, address);
            return status == WIT_STATUS_OK ? WIT_STATUS_INVALID_ARGUMENT : status;
        }
        offset += bytes;
    }
    if (mode == WIT_FILE_VIEW_READONLY) {
        status = wit_native_call(WIT_CALL_MEMORY_PROTECT, address, reserved, WIT_MEMORY_READ, 0);
        if (status != WIT_STATUS_OK) {
            discard(slot, address);
            return status;
        }
    }
    lock();
    if (!next_token) {
        unlock();
        discard(slot, address);
        return WIT_STATUS_NO_MEMORY;
    }
    const WitNativeFileView value = {next_token++, (void *)address, length};
    views[slot].View = value;
    views[slot].Reserved = reserved;
    *output = value;
    unlock();
    return WIT_STATUS_OK;
}

WitU64 wit_native_file_unview(const WitNativeFileView *input)
{
    if (!input) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    const WitNativeFileView value = *input;
    lock();
    for (WitU32 i = 0; i < WIT_FILE_VIEW_CAPACITY; ++i) {
        ViewRecord *record = &views[i];
        if (!record->Busy || !record->View.Token || record->View.Token != value.Token) {
            continue;
        }
        if (record->View.Address != value.Address || record->View.Length != value.Length) {
            unlock();
            return WIT_STATUS_INVALID_ARGUMENT;
        }
        const WitU64 status = wit_native_call(WIT_CALL_MEMORY_RELEASE, (WitU64)value.Address, 0, 0, 0);
        if (status == WIT_STATUS_OK) {
            const ViewRecord empty = {0};
            *record = empty;
        }
        unlock();
        return status;
    }
    unlock();
    return WIT_STATUS_BAD_HANDLE;
}

WitU64 wit_native_map_file(const char *path, WitU32 bytes, WitU32 mode, WitNativeFileView *output)
{
    if (!output) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    if (mode != WIT_FILE_VIEW_READONLY && mode != WIT_FILE_VIEW_PRIVATE) {
        return WIT_STATUS_UNSUPPORTED;
    }
    WitU64 handle = 0;
    WitU64 status = wit_native_file_open(path, bytes, &handle);
    if (status != WIT_STATUS_OK) {
        return status;
    }
    WitNativeFileView view = {0};
    status = wit_native_file_view(handle, mode, &view);
    const WitU64 closed = wit_native_file_close(handle);
    if (closed != WIT_STATUS_OK) {
        if (status == WIT_STATUS_OK) {
            (void)wit_native_file_unview(&view);
        }
        wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);
    }
    if (status == WIT_STATUS_OK) {
        *output = view;
    }
    return status;
}

WitU64 wit_native_unmap_file(void *address, WitU64 length)
{
    WitNativeFileView value = {0};
    lock();
    for (WitU32 i = 0; i < WIT_FILE_VIEW_CAPACITY; ++i) {
        if (views[i].Busy && views[i].View.Token && views[i].View.Address == address) {
            value = views[i].View;
            break;
        }
    }
    unlock();
    if (!value.Token) {
        return WIT_STATUS_BAD_HANDLE;
    }
    if (value.Length != length) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    return wit_native_file_unview(&value);
}
