/* epoll over pipe ends (plan step N4.2) and sockets (W2.1b): what System.IO.Pipes' asynchronous reads and writes and
 * System.Net.Sockets' asynchronous operations need under CoreCLR, whose
 * Unix implementation wraps a pipe in a Socket, registers it edge-triggered with the socket engine's epoll port and
 * waits on the port from a thread of its own; Process.Start's redirected streams go that way. An instance is a
 * descriptor (files.c, KindEpoll) over a table here: an interest list of pipe ends, each with the user's data, its
 * interest and, for an edge-triggered entry, the edge last reported. A wait computes each entry's readiness as poll
 * does (pipe.c) with the edge token that changes whenever the end's state changed — the bytes the other side moved,
 * an end that went — reports an edge-triggered entry when the token moved since its last report (or never reported
 * since its registration, as Linux reports the state at the registration), a level-triggered one while it is ready,
 * and parks as poll parks: on the word every pipe's change bumps, looking again every 10 ms while a shared pipe lives.
 * A registered end whose descriptor went is dropped at the next look, as Linux drops a file's registration with its
 * last descriptor; an entry holds a reference of its pipe so that the pipe's slot is not reused meanwhile. A socket
 * (socket.c) registers by its record and its service identifier and holds no reference, so that closing its last
 * descriptor closes it at once, as on Linux: a look drops the entry once its descriptor no longer names that socket.
 * Its readiness is the network service's, asked outside the table lock (syscall.c: __wit_epoll_probes, the
 * questions, __wit_epoll_report), and its edge token the count of the session's readiness events, whatever their socket,
 * read before the question: the service drops an event its full queue cannot take and a client asks again after any. A file is EPERM, as Linux answers. Everything here
 * runs under the table lock but the park and the sockets' questions. */
#include <errno.h>
#include <poll.h>
#include <string.h>
/* musl's sys/types.h brings endian.h, whose byte swaps clang's parentheses warnings refuse under -Werror. */
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wbitwise-op-parentheses"
#pragma clang diagnostic ignored "-Wshift-op-parentheses"
#include <sys/epoll.h>
#pragma clang diagnostic pop
#include "witos_libc.h"

#define EPOLLS 8U /* instances: .NET's socket engine makes one per engine, at most half the processors */
#define EPOLL_ENTRIES \
    WIT_EPOLL_ENTRIES /* what an instance watches; the tables stay small, the root task's writable segment is bounded */
#define INTEREST (EPOLLIN | EPOLLOUT | EPOLLRDHUP | EPOLLPRI | EPOLLERR | EPOLLHUP | EPOLLET | EPOLLONESHOT)

typedef struct Entry {
    int Used;
    int Fd; /* the registered descriptor */
    WitU32 Pipe; /* the pipe end it named at the registration, referenced; a socket's record */
    int WriteEnd;
    int Socket; /* a socket's registration (W2.1b), which holds no reference */
    WitU32 SocketId; /* the socket's service identifier at the registration */
    WitU32 Events; /* the interest, with EPOLLET and EPOLLONESHOT */
    WitU64 Data;
    WitU64 Token; /* the edge last reported */
    int Reported; /* whether one was since the registration or the last EPOLL_CTL_MOD */
    int Disabled; /* EPOLLONESHOT fired; EPOLL_CTL_MOD arms it again */
} Entry;

typedef struct Instance {
    WitU32 References; /* descriptors; zero when free */
    Entry Entries[EPOLL_ENTRIES];
} Instance;

static Instance instances[EPOLLS];

static void drop(Entry *e)
{
    if (!e->Socket) {
        __wit_pipe_release(e->Pipe);
    }
    memset(e, 0, sizeof(*e));
}

/* Whether a socket's registration still names its socket: the descriptor names the same record with the same
 * identifier. */
static int socket_named(const Entry *e)
{
    WitU32 socket = 0;
    int nonblocking = 0;
    if (__wit_socket_of(e->Fd, &socket, &nonblocking) <= 0) {
        return 0;
    }
    __wit_socket_release(socket); /* the look's own reference; the descriptor holds one, so nothing closes */
    return socket == e->Pipe && __wit_socket_id(socket) == e->SocketId;
}

static WitU32 got_of(short polled)
{
    return ((polled & POLLIN) ? EPOLLIN : 0) |
        ((polled & POLLOUT) ? EPOLLOUT : 0) |
        ((polled & POLLHUP) ? EPOLLHUP : 0) |
        ((polled & POLLERR) ? EPOLLERR : 0);
}

/* A free instance for a new descriptor: its index, or -ENFILE. */
long __wit_epoll_allocate(WitU32 *index)
{
    for (WitU32 i = 0; i < EPOLLS; ++i) {
        if (!instances[i].References) {
            memset(&instances[i], 0, sizeof(instances[i]));
            instances[i].References = 1;
            *index = i;
            return 0;
        }
    }
    return -ENFILE;
}

void __wit_epoll_reference(WitU32 index)
{
    ++instances[index].References;
}

/* The last descriptor frees the instance with its entries' pipe references. */
void __wit_epoll_release(WitU32 index)
{
    Instance *in = &instances[index];
    if (--in->References != 0) {
        return;
    }
    for (WitU32 i = 0; i < EPOLL_ENTRIES; ++i) {
        if (in->Entries[i].Used) {
            drop(&in->Entries[i]);
        }
    }
    memset(in, 0, sizeof(*in));
}

/* EPOLL_CTL_ADD, MOD and DEL of descriptor fd in instance index, which files.c resolved; fd is checked here: a pipe end
 * registers, a closed descriptor is EBADF, anything else EPERM. */
long __wit_epoll_ctl(WitU32 index, int op, long fd, const struct epoll_event *event)
{
    Instance *in = &instances[index];
    Entry *found = 0, *free_entry = 0;
    for (WitU32 i = 0; i < EPOLL_ENTRIES; ++i) {
        Entry *e = &in->Entries[i];
        if (e->Used && e->Fd == fd) {
            found = e;
        } else if (!e->Used && !free_entry) {
            free_entry = e;
        }
    }
    if (op == EPOLL_CTL_DEL) {
        if (!found) {
            return __wit_descriptor_source(fd) == -EBADF ? -EBADF : -ENOENT;
        }
        drop(found);
        return 0;
    }
    if (op != EPOLL_CTL_ADD && op != EPOLL_CTL_MOD) {
        return -EINVAL;
    }
    if (!event || (event->events & ~(WitU32)INTEREST) != 0) {
        return -EINVAL;
    }
    WitU32 pipe_index = 0;
    int write_end = 0, nonblocking = 0;
    if (!__wit_pipe_of(fd, &pipe_index, &write_end, &nonblocking)) {
        WitU32 socket = 0;
        const long named = __wit_socket_of(fd, &socket, &nonblocking);
        if (named <= 0) {
            return named < 0 ? named : -EPERM;
        }
        __wit_socket_release(socket); /* a registration holds no reference: the descriptor's keeps the record */
        if (op == EPOLL_CTL_ADD) {
            if (found) {
                return -EEXIST;
            }
            if (!free_entry) {
                return -ENOSPC;
            }
            *free_entry =
                (Entry){1, (int)fd, socket, 0, 1, __wit_socket_id(socket), event->events, event->data.u64, 0, 0, 0};
            return 0;
        }
        if (!found) {
            return -ENOENT;
        }
        found->Events = event->events;
        found->Data = event->data.u64;
        found->Reported = 0;
        found->Disabled = 0;
        return 0;
    }
    /* __wit_pipe_of took a reference for the caller: an added entry keeps it, any other outcome gives it back. */
    if (op == EPOLL_CTL_ADD) {
        if (found) {
            __wit_pipe_release(pipe_index);
            return -EEXIST;
        }
        if (!free_entry) {
            __wit_pipe_release(pipe_index);
            return -ENOSPC;
        }
        *free_entry = (Entry){1, (int)fd, pipe_index, write_end, 0, 0, event->events, event->data.u64, 0, 0, 0};
        return 0;
    }
    __wit_pipe_release(pipe_index);
    if (!found) {
        return -ENOENT;
    }
    found->Events = event->events;
    found->Data = event->data.u64;
    found->Reported = 0; /* the state at the change is reported again, as Linux reports it */
    found->Disabled = 0;
    return 0;
}

/* The events ready now in instance index, at most count of them: an entry whose descriptor no longer names its pipe
 * end is dropped; an edge-triggered entry reports when its token moved since its last report; EPOLLHUP and EPOLLERR
 * are reported whether asked or not, as Linux reports them. */
long __wit_epoll_collect(WitU32 index, struct epoll_event *events, int count)
{
    Instance *in = &instances[index];
    long ready = 0;
    for (WitU32 i = 0; i < EPOLL_ENTRIES && ready < count; ++i) {
        Entry *e = &in->Entries[i];
        if (!e->Used || e->Socket) {
            continue; /* a socket's readiness is asked outside the table lock (__wit_epoll_probes) */
        }
        WitU32 pipe_index = 0;
        int write_end = 0, nonblocking = 0;
        if (!__wit_pipe_of(e->Fd, &pipe_index, &write_end, &nonblocking)) {
            drop(e);
            continue;
        }
        __wit_pipe_release(pipe_index); /* the look's own reference */
        if (pipe_index != e->Pipe || write_end != e->WriteEnd) {
            drop(e); /* the descriptor names another pipe end now */
            continue;
        }
        if (e->Disabled) {
            continue;
        }
        WitU64 token = 0;
        const short asked = (short)(((e->Events & EPOLLIN) ? POLLIN : 0) | ((e->Events & EPOLLOUT) ? POLLOUT : 0));
        const short polled = __wit_pipe_edge(e->Pipe, e->WriteEnd, asked, &token);
        const WitU32 got = got_of(polled);
        if (!got) {
            continue;
        }
        if ((e->Events & EPOLLET) && e->Reported && token == e->Token) {
            continue; /* the edge was reported */
        }
        e->Reported = 1;
        e->Token = token;
        if (e->Events & EPOLLONESHOT) {
            e->Disabled = 1;
        }
        events[ready].events = got;
        events[ready].data.u64 = e->Data;
        ++ready;
    }
    return ready;
}

/* The sockets instance index watches, for the questions epoll_wait asks the network service outside the table lock:
 * a registration whose descriptor no longer names its socket is dropped, a disabled one skipped; each probe holds a
 * reference to its socket's record until the answer. The probes' count. */
int __wit_epoll_probes(WitU32 index, WitEpollProbe *probes)
{
    Instance *in = &instances[index];
    int probed = 0;
    for (WitU32 i = 0; i < EPOLL_ENTRIES; ++i) {
        Entry *e = &in->Entries[i];
        if (!e->Used || !e->Socket) {
            continue;
        }
        if (!socket_named(e)) {
            drop(e);
            continue;
        }
        if (e->Disabled) {
            continue;
        }
        __wit_socket_reference(e->Pipe);
        probes[probed].Entry = i;
        probes[probed].Socket = e->Pipe;
        probes[probed].Id = e->SocketId;
        probes[probed].Asked = (short)(((e->Events & EPOLLIN) ? POLLIN : 0) | ((e->Events & EPOLLOUT) ? POLLOUT : 0));
        probes[probed].Token = __wit_socket_events();
        ++probed;
    }
    return probed;
}

/* The sockets' answers as events of instance index after the ones already collected, at most count of them, as
 * __wit_epoll_collect reports a pipe end's; a registration that changed meanwhile is left for the next look. */
long __wit_epoll_report(WitU32 index, const WitEpollProbe *probes, const short *polled, int probed,
    struct epoll_event *events, long first, int count)
{
    Instance *in = &instances[index];
    long ready = first;
    for (int i = 0; i < probed && ready < count; ++i) {
        Entry *e = &in->Entries[probes[i].Entry];
        if (!e->Used || !e->Socket || e->Pipe != probes[i].Socket || e->SocketId != probes[i].Id || e->Disabled) {
            continue;
        }
        const WitU32 got = got_of(polled[i]);
        if (!got) {
            continue;
        }
        const WitU64 token = (WitU64)(WitU32)probes[i].Token;
        if ((e->Events & EPOLLET) && e->Reported && token == e->Token) {
            continue; /* the edge was reported */
        }
        e->Reported = 1;
        e->Token = token;
        if (e->Events & EPOLLONESHOT) {
            e->Disabled = 1;
        }
        events[ready].events = got;
        events[ready].data.u64 = e->Data;
        ++ready;
    }
    return ready - first;
}
