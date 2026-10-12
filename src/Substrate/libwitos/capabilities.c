#define _GNU_SOURCE
#include "witos/capabilities.h"
#include "witos/channels.h"
#include "witos/libc_context.h"
#include "witos/memory_object.h"
#include "witos/syscall.h"
#include "witos/user_abi.h"
#include "witos/wait_objects.h"
#include <errno.h>
#include <string.h>

/* libwitos's capability API (plan step D1.1, witos/capabilities.h): each function a checked call of ABI-1 whose status
 * becomes an errno value. */

#define PAGE 4096ULL

/* A status as an errno value; a peer that closed its end is EPIPE, as a pipe without a reader is. */
static int error_of(WitU64 status)
{
    return status == WIT_STATUS_PEER_CLOSED ? EPIPE : (int)-__wit_errno(status);
}

int witos_take_grant(WitU64 *handle)
{
    if (!handle) {
        return EINVAL;
    }
    *handle = __wit_process.Grant;
    __wit_process.Grant = 0;
    return *handle ? 0 : ENOENT;
}

int witos_handle_close(WitU64 handle)
{
    WitU64 result = 0;
    return error_of(wit_syscall(WIT_CALL_HANDLE_CLOSE, handle, 0, 0, &result));
}

int witos_handle_duplicate(WitU64 handle, WitU64 rights, WitU64 *duplicate)
{
    WitU64 result = 0;
    if (!duplicate) {
        return EINVAL;
    }
    *duplicate = 0;
    return error_of(wit_syscall(WIT_CALL_HANDLE_DUPLICATE, handle, (WitU64)duplicate, rights, &result));
}

int witos_channel_create(WitU64 *first, WitU64 *second)
{
    WitU64 ends[2] = {0, 0}, result = 0;
    if (!first || !second) {
        return EINVAL;
    }
    const int error = error_of(wit_syscall(WIT_CALL_CHANNEL_CREATE, (WitU64)ends, 0, 0, &result));
    *first = ends[0];
    *second = ends[1];
    return error;
}

int witos_channel_send(WitU64 endpoint, const void *bytes, WitU32 count, const WitU64 *handles, WitU32 handle_count)
{
    WitChannelMessage message;
    WitU64 result = 0;
    if ((count && !bytes) ||
        (handle_count && !handles) ||
        count > WIT_CHANNEL_MESSAGE_BYTES ||
        handle_count > WIT_CHANNEL_MESSAGE_HANDLES) {
        return EINVAL;
    }
    memset(&message, 0, sizeof(message));
    message.Version = WIT_CHANNEL_MESSAGE_VERSION;
    message.Size = sizeof(message);
    message.Data = (WitU64)bytes;
    message.Handles = (WitU64)handles;
    message.Bytes = count;
    message.HandleCount = handle_count;
    const WitU64 status = wit_syscall(WIT_CALL_CHANNEL_SEND, endpoint, (WitU64)&message, sizeof(message), &result);
    return status == WIT_STATUS_BUSY ? EAGAIN : error_of(status);
}

int witos_channel_receive(WitU64 endpoint, void *bytes, WitU32 capacity, WitU32 *count, WitU64 *handles,
    WitU32 handle_capacity, WitU32 *handle_count)
{
    WitChannelMessage message;
    WitUserWaitRequest wait;
    WitU64 result = 0, status;
    if ((capacity && !bytes) ||
        (handle_capacity && !handles) ||
        !count ||
        !handle_count ||
        capacity > WIT_CHANNEL_MESSAGE_BYTES ||
        handle_capacity > WIT_CHANNEL_MESSAGE_HANDLES) {
        return EINVAL;
    }
    *count = 0;
    *handle_count = 0;
    memset(&message, 0, sizeof(message));
    message.Version = WIT_CHANNEL_MESSAGE_VERSION;
    message.Size = sizeof(message);
    message.Data = (WitU64)bytes;
    message.Handles = (WitU64)handles;
    message.Bytes = capacity;
    message.HandleCount = handle_capacity;
    memset(&wait, 0, sizeof(wait));
    wait.Version = WIT_WAIT_OBJECTS_VERSION;
    wait.Size = sizeof(wait);
    wait.Handles = (WitU64)&endpoint;
    wait.Count = 1;
    wait.Deadline = WIT_WAIT_INFINITE;
    /* An empty queue answers TIMED_OUT; the wait ends when a message arrives or the peer's end closes. */
    while ((status = wit_syscall(WIT_CALL_CHANNEL_RECEIVE, endpoint, (WitU64)&message, sizeof(message), &result)) ==
        WIT_STATUS_TIMED_OUT) {
        do {
            status = wit_syscall(WIT_CALL_OBJECT_WAIT, (WitU64)&wait, sizeof(wait), 0, &result);
        } while (status == WIT_STATUS_INTERRUPTED);
        if (status != WIT_STATUS_OK) {
            return error_of(status);
        }
    }
    if (status == WIT_STATUS_TOO_LARGE) {
        return EMSGSIZE;
    }
    if (status != WIT_STATUS_OK) {
        return error_of(status);
    }
    *count = (WitU32)(result & 0xFFFFFFFFULL);
    *handle_count = (WitU32)(result >> 32);
    return 0;
}

int witos_memory_map(WitU64 object, WitU64 offset, WitU64 bytes, WitU32 protection, void **address)
{
    WitMemoryMapRequest map;
    WitU64 result = 0;
    if (!address || !bytes || (offset & (PAGE - 1))) {
        return EINVAL;
    }
    *address = 0;
    memset(&map, 0, sizeof(map));
    map.Version = WIT_MEMORY_MAP_VERSION;
    map.Size = sizeof(map);
    map.Object = object;
    map.Offset = offset;
    map.Bytes = (bytes + PAGE - 1) & ~(PAGE - 1);
    map.Protection = protection;
    map.Target = WIT_PROCESS_SELF;
    const int error = error_of(wit_syscall(WIT_CALL_MEMORY_OBJECT_MAP, (WitU64)&map, sizeof(map), 0, &result));
    if (!error) {
        *address = (void *)result;
    }
    return error;
}

int witos_device_acquire(WitU64 table, WitU32 index, WitU64 *device)
{
    if (!device) {
        return EINVAL;
    }
    *device = 0;
    return error_of(wit_syscall(WIT_CALL_DEVICE_ACQUIRE, table, index, 0, device));
}

int witos_device_memory(WitU64 device, WitU32 region, WitU64 *object)
{
    if (!object) {
        return EINVAL;
    }
    *object = 0;
    return error_of(wit_syscall(WIT_CALL_DEVICE_MEMORY, device, region, 0, object));
}

int witos_memory_unmap(void *address)
{
    WitU64 result = 0;
    if (!address || ((WitU64)address & (PAGE - 1))) {
        return EINVAL;
    }
    return error_of(wit_syscall(WIT_CALL_MEMORY_RELEASE, (WitU64)address, 0, 0, &result));
}
