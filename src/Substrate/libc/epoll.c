/* epoll over pipe ends (plan step N4.2): what System.IO.Pipes' asynchronous reads and writes need under CoreCLR, whose
 * Unix implementation wraps a pipe in a Socket, registers it edge-triggered with the socket engine's epoll port and
 * waits on the port from a thread of its own; Process.Start's redirected streams go that way. An instance is a
 * descriptor (files.c, KindEpoll) over a table here: an interest list of pipe ends, each with the user's data, its
 * interest and, for an edge-triggered entry, the edge last reported. A wait computes each entry's readiness as poll
 * does (pipe.c) with the edge token that changes whenever the end's state changed — the bytes the other side moved,
 * an end that went — reports an edge-triggered entry when the token moved since its last report (or never reported
 * since its registration, as Linux reports the state at the registration), a level-triggered one while it is ready,
 * and parks as poll parks: on the word every pipe's change bumps, looking again every 10 ms while a shared pipe lives.
 * A registered end whose descriptor went is dropped at the next look, as Linux drops a file's registration with its
 * last descriptor; an entry holds a reference of its pipe so that the pipe's slot is not reused meanwhile. Only a pipe
 * end registers: a file is EPERM, as Linux answers; sockets wait for M7. Everything here runs under the table lock
 * but the park. */
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
    32U /* pipe ends an instance watches; the tables stay small, since the root task's writable segment is bounded (flat.h) */
#define INTEREST (EPOLLIN | EPOLLOUT | EPOLLRDHUP | EPOLLPRI | EPOLLERR | EPOLLHUP | EPOLLET | EPOLLONESHOT)

typedef struct Entry {
    int Used;
    int Fd; /* the registered descriptor */
    WitU32 Pipe; /* the pipe end it named at the registration, referenced */
    int WriteEnd;
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
    __wit_pipe_release(e->Pipe);
    memset(e, 0, sizeof(*e));
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
        return __wit_descriptor_source(fd) == -EBADF ? -EBADF : -EPERM;
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
        *free_entry = (Entry){1, (int)fd, pipe_index, write_end, event->events, event->data.u64, 0, 0, 0};
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
        if (!e->Used) {
            continue;
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
        const WitU32 got = ((polled & POLLIN) ? EPOLLIN : 0) |
            ((polled & POLLOUT) ? EPOLLOUT : 0) |
            ((polled & POLLHUP) ? EPOLLHUP : 0) |
            ((polled & POLLERR) ? EPOLLERR : 0);
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
