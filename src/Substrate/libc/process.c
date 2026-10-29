#define _GNU_SOURCE
#include "witos_libc.h"
#include "witos/channels.h"
#include "witos/manager.h"
#include "witos/memory_object.h"
#include "witos/process.h"
#include "witos/start.h"
#include "witos/terminal.h"
#include "witos/wait_objects.h"
#include <errno.h>
#include <limits.h>
#include <signal.h>
#include <spawn.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/prctl.h>
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
 * that stream, a dup2 among standard output and error binds one like the other, a dup2 of a pipe end onto a standard
 * descriptor gives the child that end (N4.2), and closing any other descriptor does nothing, since a child inherits
 * none. A file as a stream (open, dup2 of a file), process groups, sessions and scheduling attributes are not there
 * and fail with ENOSYS; a process without a manager — the root task — gets ENOSYS too.
 *
 * N4.2: a pipe end given to a child shares the pipe (pipe.c): the child receives the ring's memory object and the
 * end's wakeup endpoint in a WitStreamMessage (witos/start.h) on a channel the caller makes for the start, whose other
 * end is the child's grant. SIGCHLD is the reaper thread's: started at the first posix_spawn, it waits on the children
 * not yet reported (up to three at a time with the event a new child sets, looking again every 50 ms beyond them) and
 * raises SIGCHLD on itself when one ended, once per child, so that a handler runs as Linux runs it on some thread that
 * does not block the signal; a wait that reaps a child the reaper has not reported raises it itself. waitid reports a
 * child with WNOWAIT without reaping it, which .NET's wait state uses; kill of a child is SIGKILL through PROCESS_KILL,
 * 0 for whether the child exists, and EPERM for any other signal, since no signal crosses a process (RFC 0011 §9.3). */

#define CHILDREN 64
#define FIRST_PID 2
#define WAIT_HANDLES 4U /* WIT_OBJECT_WAIT_CAPACITY: the handles of one OBJECT_WAIT */
#define POLL_NANOSECONDS 10000000ULL /* a wait for more children than one OBJECT_WAIT holds looks again after 10 ms */
#define REAPER_NANOSECONDS 50000000ULL /* the reaper looks again after 50 ms when more children run than it waits on */
#define PAGE 4096ULL
#define REQUEST_LIMIT (WIT_MEMORY_OBJECT_PAGES * PAGE)
#define CHILD_RIGHTS (WIT_RIGHT_WAIT | WIT_RIGHT_QUERY | WIT_RIGHT_KILL | WIT_RIGHT_DUPLICATE | WIT_RIGHT_TRANSFER)
#define STREAMS 3 /* the standard descriptors */

typedef struct Child {
    int Pid;
    int Signaled; /* SIGCHLD was raised for its end (N4.2) */
    WitU64 Handle;
} Child;

static Child children[CHILDREN];
static int next_pid = FIRST_PID;
static volatile int lock;
static volatile int reaper_lock; /* the reaper's start, apart from the table's lock */
static int reaper_started;
static WitU64 reaper_event; /* set when a child is recorded: the reaper waits on the new set */

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
#define STANDARD(fd) ((fd) >= 0 && (fd) < STREAMS)

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

static WitU64 monotonic_after(WitU64 nanoseconds)
{
    WitU64 now = 0, frequency = 0;
    wit_syscall(WIT_CALL_CLOCK_READ, WIT_CLOCK_MONOTONIC, 0, 0, &now);
    wit_syscall(WIT_CALL_CLOCK_FREQUENCY, WIT_CLOCK_MONOTONIC, 0, 0, &frequency);
    return now + nanoseconds / 1000000000ULL * frequency + nanoseconds % 1000000000ULL * frequency / 1000000000ULL;
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

/* What a standard descriptor of the child holds: the log stream Value stands for (0 is the empty input, 1 and 2 the
 * log), the pipe end of the caller's descriptor Value (N4.2), the slave side of pseudo-terminal Value (E4.1), nothing,
 * or another descriptor of the caller's (a file, a directory, a device), which a child never takes: inherited, it is
 * closed; asked for by an action, it is ENOSYS. */
typedef struct View {
    int Kind;
    int Value;
    int Asked; /* an action put it there */
} View;

#define VIEW_CLOSED 0
#define VIEW_LOG 1
#define VIEW_PIPE 2
#define VIEW_OTHER 3
#define VIEW_TERMINAL 4

/* The caller's standard descriptor n as the child inherits it: the log stream itself — or the terminal, which is the
 * pseudo-terminal the caller's streams are when they are one (E4.1) — a pipe end (N4.2), or nothing (a duplicate of
 * another stream or another kind of descriptor there stays the caller's, S6.2). */
static View inherited(int n)
{
    const long source = __wit_source_of(n);
    if (source == n) {
        const long pseudo = __wit_terminal_present() ? __wit_terminal_pseudo() : -1;
        return pseudo >= 0 ? (View){VIEW_TERMINAL, (int)pseudo, 0} : (View){VIEW_LOG, n, 0};
    }
    if (source == WIT_SOURCE_PIPE) {
        return (View){VIEW_PIPE, n, 0};
    }
    return (View){VIEW_CLOSED, 0, 0};
}

/* What the child starts with: the caller's directory and streams, changed by the file actions in their order. On
 * return, bit n of *closed says the child starts without the log at standard descriptor n, sources[n] is the
 * caller's descriptor whose pipe end that descriptor becomes (N4.2), -1 for none, and terminals[n] the pseudo-terminal
 * whose slave side it is (E4.1), -1 for none. A child's terminal streams are one pseudo-terminal, and none of its
 * streams is the console's terminal beside them: ENOSYS otherwise. */
static int apply_actions(const posix_spawn_file_actions_t *actions, char *directory, long size, WitU32 *closed,
    int sources[STREAMS], int terminals[STREAMS])
{
    long length = __wit_directory_resolve(0, ".", directory, size);
    View view[STREAMS];
    if (length < 0) {
        return (int)-length;
    }
    for (int n = 0; n < STREAMS; ++n) {
        view[n] = inherited(n);
    }
    const struct fdop *op = actions && actions->__actions ? actions->__actions : 0;
    while (op && op->next) {
        op = op->next;
    }
    for (; op; op = op->prev) {
        switch (op->cmd) {
        case FDOP_CLOSE:
            if (STANDARD(op->fd)) {
                view[op->fd] = (View){VIEW_CLOSED, 0, 1};
            }
            break;
        case FDOP_DUP2: {
            if (!STANDARD(op->fd)) {
                return ENOSYS; /* a child inherits no descriptor but the standard ones */
            }
            View source;
            const long slave = STANDARD(op->srcfd) ? -1 : __wit_pseudo_slave(op->srcfd);
            if (STANDARD(op->srcfd)) {
                source = view[op->srcfd]; /* as the child sees it after the actions so far */
            } else if (slave >= 0) {
                source = (View){VIEW_TERMINAL, (int)slave, 0}; /* a pseudo-terminal's slave descriptor (E4.1) */
            } else {
                const long kind = __wit_source_of(op->srcfd);
                if (kind < 0) {
                    return EBADF;
                }
                if (kind == WIT_SOURCE_PIPE) {
                    source = (View){VIEW_PIPE, op->srcfd, 0};
                } else if (kind == WIT_SOURCE_OTHER) {
                    source = (View){VIEW_OTHER, 0, 0};
                } else {
                    source = (View){VIEW_LOG, (int)kind, 0};
                }
            }
            if (source.Kind == VIEW_CLOSED) {
                return EBADF;
            }
            source.Asked = 1;
            view[op->fd] = source;
            break;
        }
        case FDOP_OPEN: {
            /* A pseudo-terminal's slave at a standard descriptor (E4.1): what a terminal emulator gives its child. */
            const long number = __wit_pseudo_path_number(op->path);
            if (!STANDARD(op->fd) || number < 0 || number >= WIT_TERMINAL_PSEUDO_MAXIMUM) {
                return ENOSYS; /* a file as a stream */
            }
            view[op->fd] = (View){VIEW_TERMINAL, (int)number, 1};
            break;
        }
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
    *closed = 0;
    int terminal = -1, console = 0;
    for (int n = 0; n < STREAMS; ++n) {
        if (view[n].Kind == VIEW_TERMINAL) {
            if (terminal >= 0 && terminal != view[n].Value) {
                return ENOSYS; /* two pseudo-terminals */
            }
            terminal = view[n].Value;
        }
        console |= view[n].Kind == VIEW_LOG && __wit_terminal_present();
    }
    if (terminal >= 0 && console) {
        return ENOSYS; /* the console's terminal beside a pseudo-terminal */
    }
    for (int n = 0; n < STREAMS; ++n) {
        sources[n] = -1;
        terminals[n] = -1;
        switch (view[n].Kind) {
        case VIEW_TERMINAL:
            terminals[n] = view[n].Value; /* the standard stream stays, and the terminal is that pseudo-terminal */
            break;
        case VIEW_LOG:
            if (view[n].Value != n && (view[n].Value == 0 || n == 0)) {
                return ENOSYS; /* the empty input as an output, or the log as the input */
            }
            break;
        case VIEW_PIPE:
            sources[n] = view[n].Value;
            *closed |= 1U << n;
            break;
        case VIEW_OTHER:
            if (view[n].Asked) {
                return ENOSYS; /* a file as a stream */
            }
            *closed |= 1U << n;
            break;
        default:
            *closed |= 1U << n;
            break;
        }
    }
    return 0;
}

/* The streams the actions bound pipe ends to (N4.2): each pipe shared and its handles duplicated, a WitStreamMessage
 * per stream queued on a fresh channel, whose other end becomes the child's grant; the caller's end is closed at once,
 * since the queue stays with the child's. A terminal stream (E4.1) is a message without handles naming its
 * pseudo-terminal. Nothing remains of a failure. */
static int send_streams(const int sources[STREAMS], const int terminals[STREAMS], WitU64 *grant, WitU32 *count)
{
    WitU64 ends[2] = {0, 0}, result = 0;
    *grant = 0;
    *count = 0;
    for (int n = 0; n < STREAMS; ++n) {
        *count += sources[n] >= 0 || terminals[n] >= 0;
    }
    if (!*count) {
        return 0;
    }
    WitU64 status = wit_syscall(WIT_CALL_CHANNEL_CREATE, (WitU64)ends, 0, 0, &result);
    if (status != WIT_STATUS_OK) {
        return errno_of(status);
    }
    for (int n = 0; n < STREAMS; ++n) {
        WitU64 handles[2] = {0, 0};
        int write_end = 0, nonblocking = 0;
        WitStreamMessage stream;
        WitChannelMessage message;
        if (terminals[n] >= 0) {
            memset(&stream, 0, sizeof(stream));
            stream.Version = WIT_START_VERSION;
            stream.Size = sizeof(stream);
            stream.Descriptor = (WitU32)n;
            stream.End = WIT_STREAM_TERMINAL;
            stream.Terminal = (WitU32)terminals[n];
            memset(&message, 0, sizeof(message));
            message.Version = WIT_CHANNEL_MESSAGE_VERSION;
            message.Size = sizeof(message);
            message.Data = (WitU64)&stream;
            message.Bytes = sizeof(stream);
            status = wit_syscall(WIT_CALL_CHANNEL_SEND, ends[0], (WitU64)&message, sizeof(message), &result);
            if (status != WIT_STATUS_OK) {
                close_handle(ends[0]);
                close_handle(ends[1]);
                return errno_of(status);
            }
            continue;
        }
        if (sources[n] < 0) {
            continue;
        }
        const long taken = __wit_stream_handles(sources[n], &handles[0], &handles[1], &write_end, &nonblocking);
        if (taken < 0) {
            close_handle(ends[0]);
            close_handle(ends[1]);
            return (int)-taken;
        }
        memset(&stream, 0, sizeof(stream));
        stream.Version = WIT_START_VERSION;
        stream.Size = sizeof(stream);
        stream.Descriptor = (WitU32)n;
        stream.End = write_end ? WIT_STREAM_WRITE_END : WIT_STREAM_READ_END;
        stream.Flags = nonblocking ? WIT_STREAM_NONBLOCKING : 0;
        memset(&message, 0, sizeof(message));
        message.Version = WIT_CHANNEL_MESSAGE_VERSION;
        message.Size = sizeof(message);
        message.Data = (WitU64)&stream;
        message.Handles = (WitU64)handles;
        message.Bytes = sizeof(stream);
        message.HandleCount = 2;
        status = wit_syscall(WIT_CALL_CHANNEL_SEND, ends[0], (WitU64)&message, sizeof(message), &result);
        if (status != WIT_STATUS_OK) {
            close_handle(handles[0]);
            close_handle(handles[1]);
            close_handle(ends[0]);
            close_handle(ends[1]);
            return errno_of(status);
        }
    }
    close_handle(ends[0]);
    *grant = ends[1];
    return 0;
}

/* The request in a fresh memory object: the header, the path, the arguments, the environment and the directory. */
static int build_request(const char *path, char *const argv[], char *const envp[], const char *directory, WitU32 closed,
    WitU32 streams, WitU64 *object, WitU64 *bytes)
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
    header.Streams = streams;
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

/* Sends the request with the object, the reply end and the grant (zero for none), and waits for the reply on the
 * other end. The object and the grant are moved or closed. */
static int ask_manager(WitU64 object, WitU64 bytes, WitU64 grant, WitU64 *process)
{
    WitU64 ends[2] = {0, 0}, result = 0, status;
    WitU64 handles[3];
    WitManagerRequest request = {WIT_MANAGER_VERSION, sizeof(request), WIT_MANAGER_SPAWN, 0, bytes};
    WitManagerReply reply;
    WitChannelMessage message;
    WitUserWaitRequest wait;
    status = wit_syscall(WIT_CALL_CHANNEL_CREATE, (WitU64)ends, 0, 0, &result);
    if (status != WIT_STATUS_OK) {
        close_handle(object);
        close_handle(grant);
        return errno_of(status);
    }
    handles[0] = object;
    handles[1] = ends[1];
    handles[2] = grant;
    memset(&message, 0, sizeof(message));
    message.Version = WIT_CHANNEL_MESSAGE_VERSION;
    message.Size = sizeof(message);
    message.Data = (WitU64)&request;
    message.Handles = (WitU64)handles;
    message.Bytes = sizeof(request);
    message.HandleCount = grant ? 3 : 2;
    /* The manager's queue holds four messages of every process; a full queue empties as the manager works. */
    while ((status = wit_syscall(WIT_CALL_CHANNEL_SEND, __wit_process.Manager, (WitU64)&message, sizeof(message),
                &result)) == WIT_STATUS_BUSY) {
        wit_syscall(WIT_CALL_THREAD_YIELD, 0, 0, 0, &result);
    }
    if (status != WIT_STATUS_OK) {
        close_handle(object);
        close_handle(grant);
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
    } else if (info.State == WIT_PROCESS_STATE_BUDGET) {
        *status = SIGXCPU; /* past its processor time, as Linux ends a process past RLIMIT_CPU (R7.2b) */
    } else if (info.ExitCode & WIT_EXIT_SIGNALED) {
        *status = (int)(info.ExitCode & 0x7F);
    } else {
        *status = (int)((info.ExitCode & 0xFF) << 8);
    }
    return 1;
}

static void raise_sigchld(void)
{
    __wit_tkill((int)__wit_gettid(), SIGCHLD);
}

/* The reaper (N4.2): raises SIGCHLD once for each child that ended. It waits on the children not yet reported, three
 * at a time beside the event a new child sets, and looks again when a wait ends for whatever reason — a child's end,
 * a handle a wait closed, the event, an activation — or after 50 ms while more children run than it waits on. */
static void *reaper(void *argument)
{
    const unsigned long sigchld = 1UL << (SIGCHLD - 1);
    (void)argument;
    __wit_rt_sigprocmask(SIG_UNBLOCK, &sigchld, 0, 8); /* the signal is raised here: a handler runs here */
    __wit_prctl(PR_SET_NAME, (unsigned long)"wit-reaper");
    for (;;) {
        WitU64 handles[WAIT_HANDLES], result = 0;
        WitUserWaitRequest wait;
        unsigned count = 0, beyond = 0;
        int raise = 0;
        __wit_lock(&lock);
        for (unsigned i = 0; i < CHILDREN; ++i) {
            Child *child = &children[i];
            int code = 0;
            if (!child->Pid || child->Signaled) {
                continue;
            }
            if (ended(child->Handle, &code)) {
                child->Signaled = 1;
                raise = 1;
            } else if (count < WAIT_HANDLES - 1) {
                handles[count++] = child->Handle;
            } else {
                ++beyond;
            }
        }
        __wit_unlock(&lock);
        if (raise) {
            raise_sigchld();
        }
        handles[count++] = reaper_event;
        memset(&wait, 0, sizeof(wait));
        wait.Version = WIT_WAIT_OBJECTS_VERSION;
        wait.Size = sizeof(wait);
        wait.Handles = (WitU64)handles;
        wait.Count = count;
        wait.Deadline = beyond ? monotonic_after(REAPER_NANOSECONDS) : WIT_WAIT_INFINITE;
        const WitU64 status = wit_syscall(WIT_CALL_OBJECT_WAIT, (WitU64)&wait, sizeof(wait), 0, &result);
        if (status != WIT_STATUS_OK &&
            status != WIT_STATUS_TIMED_OUT &&
            status != WIT_STATUS_INTERRUPTED &&
            status != WIT_STATUS_CLOSED &&
            status != WIT_STATUS_BAD_HANDLE) {
            wit_syscall(WIT_CALL_SLEEP_UNTIL, monotonic_after(REAPER_NANOSECONDS), 0, 0, &result); /* never spin */
        }
    }
    return 0;
}

/* Starts the reaper once, before the first child: 0 or an errno value. */
static int start_reaper(void)
{
    WitU64 event = 0;
    int error = 0;
    __wit_lock(&reaper_lock);
    if (reaper_started) {
        __wit_unlock(&reaper_lock);
        return 0;
    }
    if (wit_syscall(WIT_CALL_EVENT_CREATE, 0, 0, 0, &event) != WIT_STATUS_OK) {
        __wit_unlock(&reaper_lock);
        return EAGAIN;
    }
    reaper_event = event;
    error = __wit_thread_start_detached(reaper, 0);
    if (error) {
        reaper_event = 0;
        close_handle(event);
    } else {
        reaper_started = 1;
    }
    __wit_unlock(&reaper_lock);
    return error;
}

static int record(WitU64 process)
{
    int pid = 0;
    WitU64 result = 0;
    __wit_lock(&lock);
    for (unsigned i = 0; i < CHILDREN && !pid; ++i) {
        if (!children[i].Pid) {
            children[i].Pid = pid = next_pid++;
            children[i].Handle = process;
            children[i].Signaled = 0;
        }
    }
    __wit_unlock(&lock);
    if (pid) {
        wit_syscall(WIT_CALL_EVENT_SET, reaper_event, 0, 0, &result); /* the reaper waits on the new child too */
    }
    return pid;
}

int posix_spawn(pid_t *restrict result, const char *restrict path, const posix_spawn_file_actions_t *actions,
    const posix_spawnattr_t *restrict attributes, char *const argv[restrict], char *const envp[restrict])
{
    /* Attributes a new process satisfies by itself — default dispositions, an empty mask, the one user — pass; the
     * rest has no meaning here yet. */
    const int satisfied = POSIX_SPAWN_SETSIGDEF | POSIX_SPAWN_SETSIGMASK | POSIX_SPAWN_RESETIDS | POSIX_SPAWN_USEVFORK;
    char found[PATH_MAX], directory[PATH_MAX];
    WitU32 closed = 0, streams = 0;
    WitU64 object = 0, bytes = 0, process = 0, killed = 0, grant = 0;
    int sources[STREAMS], terminals[STREAMS];
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
    int error = apply_actions(actions, directory, sizeof(directory), &closed, sources, terminals);
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
        error = start_reaper();
    }
    if (!error) {
        error = send_streams(sources, terminals, &grant, &streams);
    }
    if (!error) {
        error = build_request(path, argv, envp, directory, closed, streams, &object, &bytes);
    }
    if (error) {
        close_handle(grant);
        return error;
    }
    error = ask_manager(object, bytes, grant, &process);
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

/* One child that ended, for wait4 and waitid: its pid with its wait status in *code, the child reaped unless WNOWAIT
 * asked to leave it; 0 with WNOHANG while every child runs; -ECHILD without a child to wait for; -EINTR when a signal
 * handler ran. pid -1 is any child, a positive one that child. */
static long wait_children(long pid, long options, int *code)
{
    WitU64 handles[CHILDREN], result = 0;
    int pids[CHILDREN];
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
            int status = 0, found = 0, raise = 0;
            if (!ended(handles[i], &status)) {
                continue;
            }
            __wit_lock(&lock);
            for (unsigned j = 0; j < CHILDREN; ++j) {
                if (children[j].Pid == pids[i] && children[j].Handle == handles[i]) {
                    found = 1;
                    if (!(options & WNOWAIT)) {
                        raise = !children[j].Signaled; /* reaped before the reaper reported it */
                        children[j].Pid = 0;
                        children[j].Handle = 0;
                        children[j].Signaled = 0;
                    }
                }
            }
            __wit_unlock(&lock);
            if (!found) {
                continue; /* another thread reaped it first */
            }
            if (!(options & WNOWAIT)) {
                close_handle(handles[i]);
                if (raise) {
                    raise_sigchld();
                }
            }
            *code = status;
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
        wait.Deadline = count > WAIT_HANDLES ? monotonic_after(POLL_NANOSECONDS) : WIT_WAIT_INFINITE;
        if (__wit_cancel_requested()) {
            return -EINTR; /* a cancellation point about to park with its cancel word set (thread.c) */
        }
        const WitU64 waited = wit_syscall(WIT_CALL_OBJECT_WAIT, (WitU64)&wait, sizeof(wait), 0, &result);
        if (waited == WIT_STATUS_INTERRUPTED) {
            return -EINTR; /* a signal handler ran */
        }
        if (waited != WIT_STATUS_OK &&
            waited != WIT_STATUS_TIMED_OUT &&
            waited != WIT_STATUS_CLOSED &&
            waited != WIT_STATUS_BAD_HANDLE) {
            return __wit_errno(waited);
        }
    }
}

long __wit_wait4(long pid, int *status, long options, struct rusage *usage)
{
    int code = 0;
    if (options & ~(long)(WNOHANG | WUNTRACED | WCONTINUED) || pid < -1) {
        return options & ~(long)(WNOHANG | WUNTRACED | WCONTINUED) ? -EINVAL : -ECHILD; /* no process groups */
    }
    const long reaped = wait_children(pid, options & WNOHANG, &code);
    if (reaped > 0) {
        if (status) {
            *status = code;
        }
        if (usage) {
            memset(usage, 0, sizeof(*usage));
        }
    }
    return reaped;
}

/* waitid (N4.2): P_ALL or P_PID with WEXITED, WNOHANG and WNOWAIT as Linux serves them; nothing stops or continues, so
 * a wait for that alone is EINVAL rather than one that never ends, and no process group has a child (P_PGID: ECHILD).
 * With WNOHANG and no child ended, the record's si_pid and si_signo are zero, as Linux leaves them. */
long __wit_waitid(long idtype, long id, void *info, long options)
{
    const long known = WEXITED | WSTOPPED | WCONTINUED | WNOHANG | WNOWAIT;
    siginfo_t *record = (siginfo_t *)info;
    long pid;
    int code = 0;
    if ((options & ~known) || !(options & WEXITED)) {
        return -EINVAL;
    }
    if (idtype == P_ALL) {
        pid = -1;
    } else if (idtype == P_PID) {
        if (id <= 0) {
            return -EINVAL;
        }
        pid = id;
    } else if (idtype == P_PGID) {
        return -ECHILD;
    } else {
        return -EINVAL;
    }
    const long reported = wait_children(pid, options & (WNOHANG | WNOWAIT), &code);
    if (reported < 0) {
        return reported;
    }
    if (record) {
        memset(record, 0, sizeof(*record));
        if (reported > 0) {
            record->si_signo = SIGCHLD;
            record->si_code = WIFEXITED(code) ? CLD_EXITED : CLD_KILLED;
            record->si_pid = (pid_t)reported;
            record->si_uid = 0;
            record->si_status = WIFEXITED(code) ? WEXITSTATUS(code) : WTERMSIG(code);
        }
    }
    return 0;
}

/* kill of a child (N4.2): 0 asks whether it exists; SIGKILL ends it through PROCESS_KILL as a death by that signal,
 * which wait reports; any other signal is EPERM, since no signal reaches another process; ESRCH for no such child. */
long __wit_kill_child(int pid, int sig)
{
    WitU64 handle = 0, result = 0;
    long r = -ESRCH;
    int code = 0;
    __wit_lock(&lock);
    for (unsigned i = 0; i < CHILDREN && !handle; ++i) {
        if (children[i].Pid == pid) {
            handle = children[i].Handle;
        }
    }
    if (handle) {
        if (sig == 0) {
            r = 0;
        } else if (sig != SIGKILL) {
            r = -EPERM;
        } else {
            const WitU64 status = wit_syscall(WIT_CALL_PROCESS_KILL, handle, WIT_EXIT_SIGNAL(SIGKILL), 0, &result);
            r = status == WIT_STATUS_OK || ended(handle, &code) ? 0 : __wit_errno(status);
        }
    }
    __wit_unlock(&lock);
    return r;
}
