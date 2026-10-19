#define _GNU_SOURCE
#include "witos/spawn.h"
#include "witos/channels.h"
#include "witos/libc_context.h"
#include "witos/manager.h"
#include "witos/memory_object.h"
#include "witos/start.h"
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
 * offset and string of the spawn request inside the request's bytes, the initial directory and the closed standard
 * streams the requester chose (S6.2) among them. The new process is started with the loader
 * (witos_spawn_ex) and the manager's endpoint, and its handle moves back over the requester's reply endpoint without
 * MANAGE; the manager keeps no handle of it, so the requester alone waits for it. The manager knows no requester: a
 * request is the capabilities it carries, the grant of the new process among them (D1.1). Before its first process
 * the manager may start services (witos_manager_run_services): each one in turn, serving requests meanwhile, until it
 * answers on its granted channel or ends, so that a service may start processes of its own before it is ready. */

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

/* The namespace's clients' endpoint a service offered with its status (D5.1), which every process the manager starts
 * afterwards receives in its start message, and through which the manager's own libc reads a program on a mounted
 * volume (D5.3); zero until then. */
static WitU64 offered_namespace;

/* The terminal's clients' endpoint a service offered with its status (N4.3, witos/terminal.h), given every process the
 * manager starts afterwards; zero until then. */
static WitU64 offered_terminal;

/* The network service's clients' endpoint a service offered with its status (W2.1a, witos/socket.h), given every
 * process the manager starts afterwards; zero until then. */
static WitU64 offered_network;

/* Starts the program a mapped spawn request names, after checking the request whole; the grant is moved into the new
 * process's start message or closed. */
static int spawn_request(const char *base, WitU64 bytes, WitU64 manager, WitU64 grant, WitU64 *process)
{
    const WitSpawnRequest *request = (const WitSpawnRequest *)base;
    char **argv = 0, **envp = 0, **path = 0, **directory = 0;
    if (bytes < sizeof(*request) ||
        request->Version != WIT_MANAGER_VERSION ||
        request->Size != sizeof(*request) ||
        request->Bytes != bytes ||
        request->PathOffset != sizeof(*request) ||
        request->ArgumentsOffset <= request->PathOffset ||
        request->EnvironmentOffset < request->ArgumentsOffset ||
        request->DirectoryOffset < request->EnvironmentOffset ||
        request->Bytes <= request->DirectoryOffset ||
        request->ArgumentCount == 0 ||
        (request->ClosedStreams & ~7U) ||
        request->Streams > WIT_START_STREAMS_MAXIMUM ||
        (request->Streams && !grant)) { /* the streams travel on the grant (N4.2) */
        close_handle(grant);
        return EINVAL;
    }
    int error = strings(base, request->PathOffset, request->ArgumentsOffset, 1, &path);
    if (!error) {
        error = strings(base, request->ArgumentsOffset, request->EnvironmentOffset, request->ArgumentCount, &argv);
    }
    if (!error) {
        error = strings(base, request->EnvironmentOffset, request->DirectoryOffset, request->EnvironmentCount, &envp);
    }
    if (!error) {
        error = strings(base, request->DirectoryOffset, request->Bytes, 1, &directory);
    }
    if (!error && directory[0][0] != '/') {
        error = EINVAL;
    }
    if (!error) {
        const witos_spawn_options options = {manager, directory[0], request->ClosedStreams, request->Streams, grant,
            offered_namespace, offered_terminal, offered_network};
        error = witos_spawn_ex(process, path[0], argv, envp, &options);
    } else {
        close_handle(grant);
    }
    free(path);
    free(argv);
    free(envp);
    free(directory);
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
    WitU64 handles[3] = {0, 0, 0}, result = 0, address = 0, process = 0;
    WitChannelMessage message;
    memset(&message, 0, sizeof(message));
    message.Version = WIT_CHANNEL_MESSAGE_VERSION;
    message.Size = sizeof(message);
    message.Data = (WitU64)&request;
    message.Handles = (WitU64)handles;
    message.Bytes = sizeof(request);
    message.HandleCount = 3;
    if (wit_syscall(WIT_CALL_CHANNEL_RECEIVE, queue, (WitU64)&message, sizeof(message), &result) != WIT_STATUS_OK) {
        return;
    }
    int error = 0;
    if ((result & 0xFFFFFFFFULL) != sizeof(request) ||
        (result >> 32) < 2 ||
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
        error = spawn_request((const char *)address, request.Bytes, manager, handles[2], &process);
        handles[2] = 0; /* moved or closed */
        wit_syscall(WIT_CALL_MEMORY_RELEASE, address, 0, 0, &result);
    }
    close_handle(handles[0]);
    close_handle(handles[2]);
    if (handles[1]) {
        reply_to(handles[1], error, process);
    } else {
        close_handle(process);
    }
}

/* Waits on up to four handles, serving the queue (the first of them) whenever it has a request, until another one is
 * signalled; returns its index, or -1 when the wait fails. */
static int serve_until(WitU64 *handles, WitU32 count, WitU64 manager)
{
    WitUserWaitRequest wait;
    WitU64 status, result = 0;
    memset(&wait, 0, sizeof(wait));
    wait.Version = WIT_WAIT_OBJECTS_VERSION;
    wait.Size = sizeof(wait);
    wait.Handles = (WitU64)handles;
    wait.Count = count;
    wait.Deadline = WIT_WAIT_INFINITE;
    for (;;) {
        status = wit_syscall(WIT_CALL_OBJECT_WAIT, (WitU64)&wait, sizeof(wait), 0, &result);
        if (status == WIT_STATUS_INTERRUPTED) {
            continue;
        }
        if (status != WIT_STATUS_OK) {
            return -1;
        }
        if (result != 0) {
            return (int)result;
        }
        serve(handles[0], manager);
    }
}

/* A service's answer on its creator's end: zero or its errno value, ECHILD when it ended or closed its end first. A
 * ready answer may carry the endpoints its Capabilities name, the namespace's clients' endpoint (D5.1), the
 * terminal's (N4.3) and the network service's (W2.1a), which the manager keeps for the processes it starts afterwards;
 * the first of each kind counts. */
static int service_status(WitU64 ready)
{
    WitServiceStatus status;
    WitU64 result = 0, handles[3] = {0, 0, 0};
    WitChannelMessage message;
    memset(&message, 0, sizeof(message));
    message.Version = WIT_CHANNEL_MESSAGE_VERSION;
    message.Size = sizeof(message);
    message.Data = (WitU64)&status;
    message.Bytes = sizeof(status);
    message.Handles = (WitU64)handles;
    message.HandleCount = 3;
    if (wit_syscall(WIT_CALL_CHANNEL_RECEIVE, ready, (WitU64)&message, sizeof(message), &result) != WIT_STATUS_OK) {
        return ECHILD;
    }
    const WitU32 count = (WitU32)(result >> 32);
    const WitU32 named = (status.Capabilities & WIT_SERVICE_NAMESPACE ? 1U : 0U) +
        (status.Capabilities & WIT_SERVICE_TERMINAL ? 1U : 0U) +
        (status.Capabilities & WIT_SERVICE_NETWORK ? 1U : 0U);
    if ((result & 0xFFFFFFFFULL) != sizeof(status) ||
        status.Version != WIT_MANAGER_VERSION ||
        status.Size != sizeof(status) ||
        (status.Capabilities & ~(WIT_SERVICE_NAMESPACE | WIT_SERVICE_TERMINAL | WIT_SERVICE_NETWORK)) ||
        count != named ||
        (count && status.Error)) {
        close_handle(handles[0]);
        close_handle(handles[1]);
        close_handle(handles[2]);
        return EPROTO;
    }
    WitU32 next = 0;
    if (status.Capabilities & WIT_SERVICE_NAMESPACE) {
        if (!offered_namespace) {
            offered_namespace = handles[next];
            __wit_process.Namespace = handles[next]; /* the manager's own files: a program on a volume (D5.3) */
        } else {
            close_handle(handles[next]);
        }
        ++next;
    }
    if (status.Capabilities & WIT_SERVICE_TERMINAL) {
        if (!offered_terminal) {
            offered_terminal = handles[next];
        } else {
            close_handle(handles[next]);
        }
        ++next;
    }
    if (status.Capabilities & WIT_SERVICE_NETWORK) {
        if (!offered_network) {
            offered_network = handles[next];
        } else {
            close_handle(handles[next]);
        }
        ++next;
    }
    return (int)status.Error;
}

int witos_manager_run_services(witos_service *services, WitU32 count, const char *path, char *const argv[],
    char *const envp[], WitProcessInfo *info)
{
    WitU64 ends[2] = {0, 0}, first = 0, result = 0;
    WitU64 status = wit_syscall(WIT_CALL_CHANNEL_CREATE, (WitU64)ends, 0, 0, &result);
    if (status != WIT_STATUS_OK) {
        for (WitU32 i = 0; i < count; ++i) {
            close_handle(services[i].grant);
            services[i].grant = 0;
            services[i].status = (int)-__wit_errno(status);
        }
        return (int)-__wit_errno(status);
    }
    /* Each service in turn, until it answers or ends; the queue is served meanwhile. */
    for (WitU32 i = 0; i < count; ++i) {
        char *service_argv[] = {(char *)services[i].path, 0};
        const witos_spawn_options service_options = {ends[1], 0, 0, 0, services[i].grant, 0, 0, 0};
        services[i].grant = 0; /* moved into its start message or closed */
        services[i].process = 0;
        services[i].status =
            witos_spawn_ex(&services[i].process, services[i].path, service_argv, envp, &service_options);
        if (!services[i].status) {
            WitU64 waited[3] = {ends[0], services[i].ready, services[i].process};
            const int index = serve_until(waited, 3, ends[1]);
            services[i].status = index < 0 ? ECHILD : service_status(services[i].ready); /* it may answer and end */
        }
    }
    const witos_spawn_options options = {ends[1], 0, 0, 0, 0, offered_namespace, offered_terminal, offered_network};
    int error = witos_spawn_ex(&first, path, argv, envp, &options);
    if (!error) {
        WitU64 waited[2] = {ends[0], first};
        serve_until(waited, 2, ends[1]); /* until the first process ended */
        error = witos_wait(first, info);
        close_handle(first);
    }
    close_handle(ends[0]);
    close_handle(ends[1]);
    return error;
}

/* The monotonic deadline seconds from now, or none when the clock cannot be read. */
static WitU64 deadline_after(WitU32 seconds)
{
    WitU64 now = 0, frequency = 0;
    if (wit_syscall(WIT_CALL_CLOCK_READ, WIT_CLOCK_MONOTONIC, 0, 0, &now) != WIT_STATUS_OK ||
        wit_syscall(WIT_CALL_CLOCK_FREQUENCY, WIT_CLOCK_MONOTONIC, 0, 0, &frequency) != WIT_STATUS_OK) {
        return WIT_WAIT_INFINITE;
    }
    return now + frequency * seconds;
}

int witos_manager_stop_services(witos_service *services, WitU32 count, WitU32 seconds)
{
    const WitU64 deadline = deadline_after(seconds);
    int first = 0;
    for (WitU32 i = count; i-- > 0;) {
        witos_service *service = &services[i];
        if (service->status || !service->process || !service->ready) {
            continue;
        }
        WitServiceStop stop = {WIT_MANAGER_VERSION, sizeof(stop), WIT_SERVICE_STOP, 0, deadline};
        WitChannelMessage message;
        WitUserWaitRequest wait;
        WitU64 result = 0, waited[2] = {service->ready, service->process}, status;
        memset(&message, 0, sizeof(message));
        message.Version = WIT_CHANNEL_MESSAGE_VERSION;
        message.Size = sizeof(message);
        message.Data = (WitU64)&stop;
        message.Bytes = sizeof(stop);
        memset(&wait, 0, sizeof(wait));
        wait.Version = WIT_WAIT_OBJECTS_VERSION;
        wait.Size = sizeof(wait);
        wait.Handles = (WitU64)waited;
        wait.Count = 2;
        wait.Deadline = deadline;
        status = wit_syscall(WIT_CALL_CHANNEL_SEND, service->ready, (WitU64)&message, sizeof(message), &result);
        if (status == WIT_STATUS_OK) {
            while ((status = wit_syscall(WIT_CALL_OBJECT_WAIT, (WitU64)&wait, sizeof(wait), 0, &result)) ==
                WIT_STATUS_INTERRUPTED) {
            }
        }
        service->status = status == WIT_STATUS_OK ? service_status(service->ready)
            : status == WIT_STATUS_TIMED_OUT      ? ETIMEDOUT
                                                  : ECHILD;
        if (service->status && !first) {
            first = service->status;
        }
    }
    return first;
}

int witos_manager_run(const char *path, char *const argv[], char *const envp[], WitProcessInfo *info)
{
    return witos_manager_run_services(0, 0, path, argv, envp, info);
}
