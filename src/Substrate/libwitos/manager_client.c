#define _GNU_SOURCE
#include "witos/capabilities.h"
#include "witos/channels.h"
#include "witos/libc_context.h"
#include "witos/manager.h"
#include "witos/memory_object.h"
#include "witos/spawn.h"
#include "witos/syscall.h"
#include "witos/user_abi.h"
#include "witos/wait_objects.h"
#include <errno.h>
#include <string.h>

/* The process manager's client in libwitos (plan step D1.2, witos/manager.h): a spawn request that grants the new
 * process a capability, which the libc's posix_spawn never does. A service starts its helpers so, the device manager
 * its drivers. The request is the libc's: its bytes in a memory object, the end of a reply channel, and the grant as
 * the third handle. */

#define PAGE 4096ULL
#define REQUEST_LIMIT (WIT_MEMORY_OBJECT_PAGES * PAGE)

static int error_of(WitU64 status)
{
    return (int)-__wit_errno(status);
}

static void close_handle(WitU64 handle)
{
    WitU64 result = 0;
    if (handle) {
        wit_syscall(WIT_CALL_HANDLE_CLOSE, handle, 0, 0, &result);
    }
}

/* The bytes of a run of zero-terminated strings, and their count. */
static WitU64 strings_bytes(char *const list[], WitU32 *count)
{
    WitU64 bytes = 0;
    *count = 0;
    for (WitU32 i = 0; list && list[i]; ++i) {
        bytes += strlen(list[i]) + 1;
        ++*count;
    }
    return bytes;
}

static void put_strings(unsigned char *base, WitU64 offset, char *const list[], WitU32 count)
{
    for (WitU32 i = 0; i < count; ++i) {
        const size_t length = strlen(list[i]) + 1;
        memcpy(base + offset, list[i], length);
        offset += length;
    }
}

/* The request in a fresh memory object, the new process starting in "/" with every standard stream. */
static int build_request(const char *path, char *const argv[], char *const envp[], WitU64 *object, WitU64 *bytes)
{
    static const char directory[] = "/";
    WitSpawnRequest header;
    void *address = 0;
    memset(&header, 0, sizeof(header));
    header.Version = WIT_MANAGER_VERSION;
    header.Size = sizeof(header);
    header.PathOffset = sizeof(header);
    header.ArgumentsOffset = header.PathOffset + strlen(path) + 1;
    header.EnvironmentOffset = header.ArgumentsOffset + strings_bytes(argv, &header.ArgumentCount);
    header.DirectoryOffset = header.EnvironmentOffset + strings_bytes(envp, &header.EnvironmentCount);
    header.Bytes = header.DirectoryOffset + sizeof(directory);
    if (header.ArgumentCount == 0) {
        return EINVAL;
    }
    if (header.Bytes > REQUEST_LIMIT) {
        return E2BIG;
    }
    const WitU64 size = (header.Bytes + PAGE - 1) & ~(PAGE - 1);
    const WitU64 status = wit_syscall(WIT_CALL_MEMORY_OBJECT_CREATE, size, 0, 0, object);
    if (status != WIT_STATUS_OK) {
        return error_of(status);
    }
    const int error = witos_memory_map(*object, 0, size, WIT_MEMORY_READ | WIT_MEMORY_WRITE, &address);
    if (error) {
        close_handle(*object);
        return error;
    }
    unsigned char *base = (unsigned char *)address;
    memcpy(base, &header, sizeof(header));
    memcpy(base + header.PathOffset, path, strlen(path) + 1);
    put_strings(base, header.ArgumentsOffset, argv, header.ArgumentCount);
    put_strings(base, header.EnvironmentOffset, envp, header.EnvironmentCount);
    memcpy(base + header.DirectoryOffset, directory, sizeof(directory));
    witos_memory_unmap(address);
    *bytes = header.Bytes;
    return 0;
}

int witos_manager_spawn(const char *path, char *const argv[], char *const envp[], WitU64 grant, WitU64 *process)
{
    WitManagerRequest request = {WIT_MANAGER_VERSION, sizeof(request), WIT_MANAGER_SPAWN, 0, 0};
    WitManagerReply reply;
    WitU64 object = 0, mine = 0, theirs = 0, handles[3] = {0, 0, 0};
    WitU32 count = 0, handle_count = 0;
    if (!path || !argv || !process) {
        close_handle(grant);
        return EINVAL;
    }
    *process = 0;
    if (!__wit_process.Manager) {
        close_handle(grant);
        return ENOSYS; /* a process no process manager started */
    }
    int error = build_request(path, argv, envp, &object, &request.Bytes);
    if (!error) {
        error = witos_channel_create(&mine, &theirs);
    }
    if (!error) {
        handles[0] = object;
        handles[1] = theirs;
        handles[2] = grant;
        /* The manager's queue holds four messages of every process; a full queue empties as the manager works. */
        while ((error = witos_channel_send(__wit_process.Manager, &request, sizeof(request), handles, grant ? 3 : 2)) ==
            EAGAIN) {
            WitU64 result = 0;
            wit_syscall(WIT_CALL_THREAD_YIELD, 0, 0, 0, &result);
        }
        if (!error) {
            object = theirs = grant = 0; /* moved */
        }
    }
    if (!error) {
        handles[0] = 0;
        error = witos_channel_receive(mine, &reply, sizeof(reply), &count, handles, 1, &handle_count);
        if (!error &&
            (count != sizeof(reply) ||
                reply.Version != WIT_MANAGER_VERSION ||
                reply.Size != sizeof(reply) ||
                reply.Operation != WIT_MANAGER_SPAWN ||
                (reply.Error == 0) != (handle_count == 1))) {
            error = EIO; /* the manager broke the protocol */
        } else if (!error && reply.Error) {
            error = (int)reply.Error;
        } else if (!error) {
            *process = handles[0];
            handles[0] = 0;
        }
        close_handle(handles[0]);
    }
    close_handle(object);
    close_handle(mine);
    close_handle(theirs);
    close_handle(grant);
    return error;
}
