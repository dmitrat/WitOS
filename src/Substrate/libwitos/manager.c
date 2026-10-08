#define _GNU_SOURCE
#include "witos/spawn.h"
#include "witos/channels.h"
#include "witos/libc_context.h"
#include "witos/manager.h"
#include "witos/memory_object.h"
#include "witos/syscall.h"
#include "witos/user_abi.h"
#include "witos/wait_objects.h"
#include <errno.h>
#include <stdlib.h>
#include <string.h>

/* The process manager of the root task (plan step S6.1, RFC 0011 v3 section 9.3; witos/manager.h). It owns one
 * channel: it receives on one end and every process it starts gets a duplicate of the other end with SEND alone, so
 * that all of them share one queue the manager waits on beside its first process, without a wait for each process.
 * A request is checked whole before anything starts: the message, the memory object it maps read-only, and every
 * offset and string of the spawn request inside the request's bytes. The new process is started with the loader
 * (witos_spawn_ex) and the manager's endpoint, and its handle moves back over the requester's reply endpoint without
 * MANAGE; the manager keeps no handle of it, so the requester alone waits for it. The manager knows no requester: a
 * request is the capabilities it carries. */

#define PAGE 4096ULL
#define REQUEST_LIMIT (WIT_MEMORY_OBJECT_PAGES * PAGE)
#define CHILD_RIGHTS (WIT_RIGHT_WAIT | WIT_RIGHT_QUERY | WIT_RIGHT_KILL | WIT_RIGHT_DUPLICATE | WIT_RIGHT_TRANSFER)

static void close_handle(WitU64 handle)
{
    WitU64 result = 0;
    if (handle) {
        wit_syscall(WIT_CALL_HANDLE_CLOSE, handle, 0, 0, &result);
    }
}

/* A run of count zero-terminated strings that fills [begin, end) exactly, as an array of pointers into it. */
static int strings(const char *base, WitU64 begin, WitU64 end, WitU32 count, char ***list)
{
    if (count > (end - begin)) {
        return EINVAL;
    }
    char **result = malloc(((size_t)count + 1) * sizeof(char *));
    if (!result) {
        return ENOMEM;
    }
    WitU64 cursor = begin;
    for (WitU32 i = 0; i < count; ++i) {
        const char *terminator = memchr(base + cursor, 0, end - cursor);
        if (!terminator) {
            free(result);
            return EINVAL;
        }
        result[i] = (char *)(base + cursor);
        cursor = (WitU64)(terminator - base) + 1;
    }
    if (cursor != end) {
        free(result);
        return EINVAL;
    }
    result[count] = 0;
    *list = result;
    return 0;
}

/* Starts the program a mapped spawn request names, after checking the request whole. */
static int spawn_request(const char *base, WitU64 bytes, WitU64 manager, WitU64 *process)
{
    const WitSpawnRequest *request = (const WitSpawnRequest *)base;
    char **argv = 0, **envp = 0, **path = 0;
    if (bytes < sizeof(*request) ||
        request->Version != WIT_MANAGER_VERSION ||
        request->Size != sizeof(*request) ||
        request->Bytes != bytes ||
        request->PathOffset != sizeof(*request) ||
        request->ArgumentsOffset <= request->PathOffset ||
        request->EnvironmentOffset < request->ArgumentsOffset ||
        request->Bytes < request->EnvironmentOffset ||
        request->ArgumentCount == 0) {
        return EINVAL;
    }
    int error = strings(base, request->PathOffset, request->ArgumentsOffset, 1, &path);
    if (!error) {
        error = strings(base, request->ArgumentsOffset, request->EnvironmentOffset, request->ArgumentCount, &argv);
    }
    if (!error) {
        error = strings(base, request->EnvironmentOffset, request->Bytes, request->EnvironmentCount, &envp);
    }
    if (!error) {
        const witos_spawn_options options = {manager};
        error = witos_spawn_ex(process, path[0], argv, envp, &options);
    }
    free(path);
    free(argv);
    free(envp);
    return error;
}

/* Answers on the reply endpoint, moving the process's handle with the requester's rights, and closes the endpoint. */
static void reply_to(WitU64 endpoint, int error, WitU64 process)
{
    WitManagerReply reply = {WIT_MANAGER_VERSION, sizeof(reply), WIT_MANAGER_SPAWN, (WitU32)error};
    WitU64 handle = 0, result = 0;
    WitChannelMessage message;
    if (!error &&
        wit_syscall(WIT_CALL_HANDLE_DUPLICATE, process, (WitU64)&handle, CHILD_RIGHTS, &result) != WIT_STATUS_OK) {
        reply.Error = ENOMEM;
        wit_syscall(WIT_CALL_PROCESS_KILL, process, 127, 0, &result); /* nobody could wait for it */
    }
    memset(&message, 0, sizeof(message));
    message.Version = WIT_CHANNEL_MESSAGE_VERSION;
    message.Size = sizeof(message);
    message.Data = (WitU64)&reply;
    message.Handles = (WitU64)&handle;
    message.Bytes = sizeof(reply);
    message.HandleCount = handle ? 1 : 0;
    if (wit_syscall(WIT_CALL_CHANNEL_SEND, endpoint, (WitU64)&message, sizeof(message), &result) != WIT_STATUS_OK) {
        close_handle(handle); /* the requester went away; the process runs on without a waiter */
    }
    close_handle(process);
    close_handle(endpoint);
}

/* One request from the queue: its object mapped read-only for the time of the start, then closed. */
static void serve(WitU64 queue, WitU64 manager)
{
    WitManagerRequest request;
    WitU64 handles[2] = {0, 0}, result = 0, address = 0, process = 0;
    WitChannelMessage message;
    memset(&message, 0, sizeof(message));
    message.Version = WIT_CHANNEL_MESSAGE_VERSION;
    message.Size = sizeof(message);
    message.Data = (WitU64)&request;
    message.Handles = (WitU64)handles;
    message.Bytes = sizeof(request);
    message.HandleCount = 2;
    if (wit_syscall(WIT_CALL_CHANNEL_RECEIVE, queue, (WitU64)&message, sizeof(message), &result) != WIT_STATUS_OK) {
        return;
    }
    int error = 0;
    if ((result & 0xFFFFFFFFULL) != sizeof(request) ||
        (result >> 32) != 2 ||
        request.Version != WIT_MANAGER_VERSION ||
        request.Size != sizeof(request) ||
        request.Operation != WIT_MANAGER_SPAWN ||
        request.Reserved ||
        !request.Bytes ||
        request.Bytes > REQUEST_LIMIT) {
        error = EINVAL;
    }
    if (!error) {
        WitMemoryMapRequest map;
        memset(&map, 0, sizeof(map));
        map.Version = WIT_MEMORY_MAP_VERSION;
        map.Size = sizeof(map);
        map.Object = handles[0];
        map.Bytes = (request.Bytes + PAGE - 1) & ~(PAGE - 1);
        map.Protection = WIT_MEMORY_READ;
        map.Target = WIT_PROCESS_SELF;
        const WitU64 status = wit_syscall(WIT_CALL_MEMORY_OBJECT_MAP, (WitU64)&map, sizeof(map), 0, &address);
        error = status == WIT_STATUS_OK ? 0 : EINVAL;
    }
    if (!error) {
        error = spawn_request((const char *)address, request.Bytes, manager, &process);
        wit_syscall(WIT_CALL_MEMORY_RELEASE, address, 0, 0, &result);
    }
    close_handle(handles[0]);
    if (handles[1]) {
        reply_to(handles[1], error, process);
    } else {
        close_handle(process);
    }
}

int witos_manager_run(const char *path, char *const argv[], char *const envp[], WitProcessInfo *info)
{
    WitU64 ends[2] = {0, 0}, first = 0, result = 0;
    WitU64 status = wit_syscall(WIT_CALL_CHANNEL_CREATE, (WitU64)ends, 0, 0, &result);
    if (status != WIT_STATUS_OK) {
        return (int)-__wit_errno(status);
    }
    const witos_spawn_options options = {ends[1]};
    int error = witos_spawn_ex(&first, path, argv, envp, &options);
    if (!error) {
        WitU64 waited[2] = {ends[0], first};
        WitUserWaitRequest wait;
        memset(&wait, 0, sizeof(wait));
        wait.Version = WIT_WAIT_OBJECTS_VERSION;
        wait.Size = sizeof(wait);
        wait.Handles = (WitU64)waited;
        wait.Count = 2;
        wait.Deadline = WIT_WAIT_INFINITE;
        for (;;) {
            status = wit_syscall(WIT_CALL_OBJECT_WAIT, (WitU64)&wait, sizeof(wait), 0, &result);
            if (status == WIT_STATUS_INTERRUPTED) {
                continue;
            }
            if (status != WIT_STATUS_OK || result == 1) {
                break; /* the first process ended */
            }
            serve(ends[0], ends[1]);
        }
        error = witos_wait(first, info);
        close_handle(first);
    }
    close_handle(ends[0]);
    close_handle(ends[1]);
    return error;
}
