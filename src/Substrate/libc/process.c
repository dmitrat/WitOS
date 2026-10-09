#define _GNU_SOURCE
#include "witos_libc.h"
#include "witos/channels.h"
#include "witos/manager.h"
#include "witos/memory_object.h"
#include "witos/process.h"
#include "witos/wait_objects.h"
#include <errno.h>
#include <limits.h>
#include <signal.h>
#include <spawn.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/resource.h>
#include <sys/wait.h>
#include <unistd.h>

/* Child processes (plan step S6.1, RFC 0011 v3 section 9.3): posix_spawn asks the process manager, whose endpoint the
 * start message gave the process, to start a program of the boot package (witos/manager.h); there is no fork and no
 * exec. The request travels in a memory object with the end of a fresh channel for the reply, which moves the new
 * process's handle back. The library numbers its children itself, from 2 (the process's own id is 1), and wait4 waits
 * on their handles: OBJECT_WAIT for up to four at a time, a short deadline and another round beyond that, and
 * PROCESS_QUERY for how each ended. An exit code from 0 to 255 is an exit status; WIT_EXIT_SIGNAL(sig), which the
 * library's default signal actions exit with, is a death by that signal; a fault the process's handlers did not take
 * ends it as the kernel's fault report, which waitpid reports as SIGSEGV. A child starts in the caller's current
 * directory with the caller's standard streams (S6.2); the file actions change what it starts with, applied in order
 * as a child would apply them: a chdir or fchdir moves its directory, closing a standard descriptor leaves it without
 * that stream, a dup2 among standard output and error binds one like the other, and closing any other descriptor does
 * nothing, since a child inherits none. A file or a pipe as a stream (open, dup2 of another descriptor), process
 * groups, sessions and scheduling attributes are not there and fail with ENOSYS; a process without a manager — the
 * root task — gets ENOSYS too. */

#define CHILDREN 64
#define FIRST_PID 2
#define WAIT_HANDLES 4U /* WIT_OBJECT_WAIT_CAPACITY: the handles of one OBJECT_WAIT */
#define POLL_NANOSECONDS 10000000ULL /* a wait for more children than one OBJECT_WAIT holds looks again after 10 ms */
#define PAGE 4096ULL
#define REQUEST_LIMIT (WIT_MEMORY_OBJECT_PAGES * PAGE)
#define CHILD_RIGHTS (WIT_RIGHT_WAIT | WIT_RIGHT_QUERY | WIT_RIGHT_KILL | WIT_RIGHT_DUPLICATE | WIT_RIGHT_TRANSFER)

typedef struct Child {
    int Pid;
    WitU64 Handle;
} Child;

static Child children[CHILDREN];
static int next_pid = FIRST_PID;
static volatile int lock;

int __execvpe(const char *, char *const[], char *const[]); /* posix_spawnp's marker in the attributes (musl) */

/* musl's record of a file action (src/process/fdop.h): posix_spawn_file_actions_add* put the newest first. */
struct fdop {
    struct fdop *next, *prev;
    int cmd, fd, srcfd, oflag;
    mode_t mode;
    char path[];
};

#define FDOP_CLOSE 1
#define FDOP_DUP2 2
#define FDOP_OPEN 3
#define FDOP_CHDIR 4
#define FDOP_FCHDIR 5
#define STANDARD(fd) ((fd) >= 0 && (fd) <= 2)

static int errno_of(WitU64 status)
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

static WitU64 strings_bytes(char *const list[], WitU32 *count)
{
    WitU64 bytes = 0;
    *count = 0;
    for (; list && list[*count]; ++*count) {
        bytes += strlen(list[*count]) + 1;
    }
    return bytes;
}

static WitU64 put_strings(unsigned char *base, WitU64 offset, char *const list[], WitU32 count)
{
    for (WitU32 i = 0; i < count; ++i) {
        const WitU64 length = strlen(list[i]) + 1;
        memcpy(base + offset, list[i], length);
        offset += length;
    }
    return offset;
}

/* What the child starts with: the caller's directory and streams, changed by the file actions in their order. */
static int apply_actions(const posix_spawn_file_actions_t *actions, char *directory, long size, WitU32 *closed)
{
    long length = __wit_directory_resolve(0, ".", directory, size);
    *closed = __wit_process.ClosedStreams;
    if (length < 0) {
        return (int)-length;
    }
    if (!actions || !actions->__actions) {
        return 0;
    }
    const struct fdop *op = actions->__actions;
    while (op->next) {
        op = op->next;
    }
    for (; op; op = op->prev) {
        switch (op->cmd) {
        case FDOP_CLOSE:
            if (STANDARD(op->fd)) {
                *closed |= 1U << op->fd;
            }
            break;
        case FDOP_DUP2:
            if (!STANDARD(op->srcfd) || !STANDARD(op->fd) || (op->srcfd != op->fd && (op->srcfd == 0 || op->fd == 0))) {
                return ENOSYS; /* a file, a pipe or the empty input as another stream */
            }
            if (*closed & (1U << op->srcfd)) {
                return EBADF;
            }
            *closed &= ~(1U << op->fd);
            break;
        case FDOP_CHDIR:
            if ((length = __wit_directory_resolve(directory, op->path, directory, size)) < 0) {
                return (int)-length;
            }
            break;
        case FDOP_FCHDIR:
            if ((length = __wit_descriptor_directory(op->fd, directory, size)) < 0) {
                return (int)-length;
            }
            break;
        default:
            return ENOSYS;
        }
    }
    return 0;
}

/* The request in a fresh memory object: the header, the path, the arguments, the environment and the directory. */
static int build_request(const char *path, char *const argv[], char *const envp[], const char *directory, WitU32 closed,
    WitU64 *object, WitU64 *bytes)
{
    WitSpawnRequest header;
    WitU64 address = 0, result = 0;
    memset(&header, 0, sizeof(header));
    header.Version = WIT_MANAGER_VERSION;
    header.Size = sizeof(header);
    header.PathOffset = sizeof(header);
    header.ArgumentsOffset = header.PathOffset + strlen(path) + 1;
    header.EnvironmentOffset = header.ArgumentsOffset + strings_bytes(argv, &header.ArgumentCount);
    header.DirectoryOffset = header.EnvironmentOffset + strings_bytes(envp, &header.EnvironmentCount);
    header.Bytes = header.DirectoryOffset + strlen(directory) + 1;
    header.ClosedStreams = closed;
    if (header.Bytes > REQUEST_LIMIT) {
        return E2BIG;
    }
    const WitU64 size = (header.Bytes + PAGE - 1) & ~(PAGE - 1);
    WitU64 status = wit_syscall(WIT_CALL_MEMORY_OBJECT_CREATE, size, 0, 0, object);
    if (status != WIT_STATUS_OK) {
        return errno_of(status);
    }
    WitMemoryMapRequest map;
    memset(&map, 0, sizeof(map));
    map.Version = WIT_MEMORY_MAP_VERSION;
    map.Size = sizeof(map);
    map.Object = *object;
    map.Bytes = size;
    map.Protection = WIT_MEMORY_READ | WIT_MEMORY_WRITE;
    map.Target = WIT_PROCESS_SELF;
    status = wit_syscall(WIT_CALL_MEMORY_OBJECT_MAP, (WitU64)&map, sizeof(map), 0, &address);
    if (status != WIT_STATUS_OK) {
        close_handle(*object);
        return errno_of(status);
    }
    unsigned char *base = (unsigned char *)address;
    memcpy(base, &header, sizeof(header));
    memcpy(base + header.PathOffset, path, strlen(path) + 1);
    put_strings(base, header.ArgumentsOffset, argv, header.ArgumentCount);
    put_strings(base, header.EnvironmentOffset, envp, header.EnvironmentCount);
    memcpy(base + header.DirectoryOffset, directory, strlen(directory) + 1);
    wit_syscall(WIT_CALL_MEMORY_RELEASE, address, 0, 0, &result);
    *bytes = header.Bytes;
    return 0;
}

/* Sends the request with the object and the reply end, and waits for the reply on the other end. */
static int ask_manager(WitU64 object, WitU64 bytes, WitU64 *process)
{
    WitU64 ends[2] = {0, 0}, result = 0, status;
    WitU64 handles[2];
    WitManagerRequest request = {WIT_MANAGER_VERSION, sizeof(request), WIT_MANAGER_SPAWN, 0, bytes};
    WitManagerReply reply;
    WitChannelMessage message;
    WitUserWaitRequest wait;
    status = wit_syscall(WIT_CALL_CHANNEL_CREATE, (WitU64)ends, 0, 0, &result);
    if (status != WIT_STATUS_OK) {
        close_handle(object);
        return errno_of(status);
    }
    handles[0] = object;
    handles[1] = ends[1];
    memset(&message, 0, sizeof(message));
    message.Version = WIT_CHANNEL_MESSAGE_VERSION;
    message.Size = sizeof(message);
    message.Data = (WitU64)&request;
    message.Handles = (WitU64)handles;
    message.Bytes = sizeof(request);
    message.HandleCount = 2;
    /* The manager's queue holds four messages of every process; a full queue empties as the manager works. */
    while ((status = wit_syscall(WIT_CALL_CHANNEL_SEND, __wit_process.Manager, (WitU64)&message, sizeof(message),
                &result)) == WIT_STATUS_BUSY) {
        wit_syscall(WIT_CALL_THREAD_YIELD, 0, 0, 0, &result);
    }
    if (status != WIT_STATUS_OK) {
        close_handle(object);
        close_handle(ends[0]);
        close_handle(ends[1]);
        return status == WIT_STATUS_PEER_CLOSED ? EAGAIN : errno_of(status);
    }
    memset(&wait, 0, sizeof(wait));
    wait.Version = WIT_WAIT_OBJECTS_VERSION;
    wait.Size = sizeof(wait);
    wait.Handles = (WitU64)&ends[0];
    wait.Count = 1;
    wait.Deadline = WIT_WAIT_INFINITE;
    handles[0] = 0;
    message.Data = (WitU64)&reply;
    message.Bytes = sizeof(reply);
    message.HandleCount = 1;
    while ((status = wit_syscall(WIT_CALL_CHANNEL_RECEIVE, ends[0], (WitU64)&message, sizeof(message), &result)) ==
        WIT_STATUS_TIMED_OUT) {
        status = wit_syscall(WIT_CALL_OBJECT_WAIT, (WitU64)&wait, sizeof(wait), 0, &result);
        if (status != WIT_STATUS_OK && status != WIT_STATUS_INTERRUPTED) {
            break;
        }
    }
    close_handle(ends[0]);
    if (status != WIT_STATUS_OK ||
        (result & 0xFFFFFFFFULL) != sizeof(reply) ||
        reply.Version != WIT_MANAGER_VERSION ||
        reply.Size != sizeof(reply) ||
        reply.Operation != WIT_MANAGER_SPAWN ||
        (reply.Error == 0) != ((result >> 32) == 1)) {
        close_handle(handles[0]);
        return EIO; /* the manager went away or broke the protocol */
    }
    if (reply.Error) {
        return (int)reply.Error;
    }
    *process = handles[0];
    return 0;
}

/* posix_spawnp: the first directory of PATH, the caller's or "/usr/local/bin:/bin:/usr/bin", that holds the file. */
static int search(const char *file, char *buffer, size_t size)
{
    const char *path = getenv("PATH");
    if (!path) {
        path = "/usr/local/bin:/bin:/usr/bin";
    }
    const size_t length = strlen(file);
    while (*path) {
        const char *end = strchr(path, ':');
        const size_t directory = end ? (size_t)(end - path) : strlen(path);
        if (directory + 1 + length + 1 <= size) {
            memcpy(buffer, path, directory);
            buffer[directory] = '/';
            memcpy(buffer + directory + 1, file, length + 1);
            if (access(buffer, F_OK) == 0) {
                return 0;
            }
        }
        if (!end) {
            break;
        }
        path = end + 1;
    }
    return ENOENT;
}

static int record(WitU64 process)
{
    int pid = 0;
    __wit_lock(&lock);
    for (unsigned i = 0; i < CHILDREN && !pid; ++i) {
        if (!children[i].Pid) {
            children[i].Pid = pid = next_pid++;
            children[i].Handle = process;
        }
    }
    __wit_unlock(&lock);
    return pid;
}

int posix_spawn(pid_t *restrict result, const char *restrict path, const posix_spawn_file_actions_t *actions,
    const posix_spawnattr_t *restrict attributes, char *const argv[restrict], char *const envp[restrict])
{
    /* Attributes a new process satisfies by itself — default dispositions, an empty mask, the one user — pass; the
     * rest has no meaning here yet. */
    const int satisfied = POSIX_SPAWN_SETSIGDEF | POSIX_SPAWN_SETSIGMASK | POSIX_SPAWN_RESETIDS | POSIX_SPAWN_USEVFORK;
    char found[PATH_MAX], directory[PATH_MAX];
    WitU32 closed = 0;
    WitU64 object = 0, bytes = 0, process = 0, killed = 0;
    if (!__wit_process.Manager) {
        return ENOSYS;
    }
    if (attributes &&
        ((attributes->__flags & ~satisfied) ||
            ((attributes->__flags & POSIX_SPAWN_SETSIGMASK) && !sigisemptyset(&attributes->__mask)))) {
        return ENOSYS;
    }
    if (attributes && attributes->__fn == (void *)__execvpe && !strchr(path, '/')) {
        const int error = search(path, found, sizeof(found));
        if (error) {
            return error;
        }
        path = found;
    }
    int error = apply_actions(actions, directory, sizeof(directory), &closed);
    char program[PATH_MAX];
    if (!error && path[0] != '/') {
        /* A relative path names the program from the directory the child starts in, as exec after the actions would. */
        const int length = snprintf(program, sizeof(program), "%s/%s", directory, path);
        if (length < 0 || (size_t)length >= sizeof(program)) {
            error = ENAMETOOLONG;
        }
        path = program;
    }
    if (!error) {
        error = build_request(path, argv, envp, directory, closed, &object, &bytes);
    }
    if (!error) {
        error = ask_manager(object, bytes, &process);
    }
    if (error) {
        return error;
    }
    const int pid = record(process);
    if (!pid) {
        wit_syscall(WIT_CALL_PROCESS_KILL, process, 127, 0, &killed); /* no room to wait for it */
        close_handle(process);
        return EAGAIN;
    }
    if (result) {
        *result = pid;
    }
    return 0;
}

/* How a child ended, as wait reports it; zero while it runs. */
static int ended(WitU64 handle, int *status)
{
    WitProcessInfo info;
    WitU64 result = 0;
    memset(&info, 0, sizeof(info));
    info.Version = WIT_PROCESS_INFO_VERSION;
    info.Size = sizeof(info);
    if (wit_syscall(WIT_CALL_PROCESS_QUERY, handle, (WitU64)&info, sizeof(info), &result) != WIT_STATUS_OK ||
        info.State == WIT_PROCESS_STATE_LIVE) {
        return 0;
    }
    if (info.State == WIT_PROCESS_STATE_FAULTED) {
        *status = SIGSEGV;
    } else if (info.ExitCode & WIT_EXIT_SIGNALED) {
        *status = (int)(info.ExitCode & 0x7F);
    } else {
        *status = (int)((info.ExitCode & 0xFF) << 8);
    }
    return 1;
}

long __wit_wait4(long pid, int *status, long options, struct rusage *usage)
{
    WitU64 handles[CHILDREN], result = 0;
    int pids[CHILDREN];
    if (options & ~(long)(WNOHANG | WUNTRACED | WCONTINUED) || pid < -1) {
        return options & ~(long)(WNOHANG | WUNTRACED | WCONTINUED) ? -EINVAL : -ECHILD; /* no process groups */
    }
    for (;;) {
        unsigned count = 0;
        __wit_lock(&lock);
        for (unsigned i = 0; i < CHILDREN; ++i) {
            if (children[i].Pid && (pid <= 0 || children[i].Pid == pid)) {
                pids[count] = children[i].Pid;
                handles[count++] = children[i].Handle;
            }
        }
        __wit_unlock(&lock);
        if (!count) {
            return -ECHILD;
        }
        for (unsigned i = 0; i < count; ++i) {
            int code = 0;
            if (!ended(handles[i], &code)) {
                continue;
            }
            int reaped = 0;
            __wit_lock(&lock);
            for (unsigned j = 0; j < CHILDREN; ++j) {
                if (children[j].Pid == pids[i] && children[j].Handle == handles[i]) {
                    children[j].Pid = 0;
                    children[j].Handle = 0;
                    reaped = 1;
                }
            }
            __wit_unlock(&lock);
            if (!reaped) {
                continue; /* another thread reaped it first */
            }
            close_handle(handles[i]);
            if (status) {
                *status = code;
            }
            if (usage) {
                memset(usage, 0, sizeof(*usage));
            }
            return pids[i];
        }
        if (options & WNOHANG) {
            return 0;
        }
        WitUserWaitRequest wait;
        memset(&wait, 0, sizeof(wait));
        wait.Version = WIT_WAIT_OBJECTS_VERSION;
        wait.Size = sizeof(wait);
        wait.Handles = (WitU64)handles;
        wait.Count = count < WAIT_HANDLES ? count : WAIT_HANDLES;
        wait.Deadline = WIT_WAIT_INFINITE;
        if (count > WAIT_HANDLES) {
            WitU64 now = 0;
            WitU64 frequency = 0;
            wit_syscall(WIT_CALL_CLOCK_READ, WIT_CLOCK_MONOTONIC, 0, 0, &now);
            wit_syscall(WIT_CALL_CLOCK_FREQUENCY, WIT_CLOCK_MONOTONIC, 0, 0, &frequency);
            wait.Deadline = now + POLL_NANOSECONDS * frequency / 1000000000ULL;
        }
        if (__wit_cancel_requested()) {
            return -EINTR; /* a cancellation point about to park with its cancel word set (thread.c) */
        }
        const WitU64 waited = wit_syscall(WIT_CALL_OBJECT_WAIT, (WitU64)&wait, sizeof(wait), 0, &result);
        if (waited == WIT_STATUS_INTERRUPTED) {
            return -EINTR; /* a signal handler ran */
        }
        if (waited != WIT_STATUS_OK && waited != WIT_STATUS_TIMED_OUT) {
            return __wit_errno(waited);
        }
    }
}
