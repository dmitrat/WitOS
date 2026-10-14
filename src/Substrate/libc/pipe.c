#define _GNU_SOURCE
#include "witos_libc.h"
#include "witos/channels.h"
#include "witos/memory_object.h"
#include "witos/wait_objects.h"
#include <errno.h>
#include <poll.h>
#include <signal.h>
#include <string.h>
#include <sys/mman.h>

/* Pipes (plan step R2.3a; between processes since N4.2). A pipe is a ring buffer with its own lock, and a reader that
 * finds it empty or a writer that finds it full parks until a change. Within one process the ring is anonymous memory
 * and the park is a futex word that every change bumps. Once a posix_spawn file action gives an end of the pipe to a
 * child as a standard stream (process.c), the pipe is shared: the ring moves into a memory object every holder of an
 * end maps, and each end gets one endpoint of a channel for its wakeups. A holder sends an empty message on its end's
 * endpoint after each change, so that the other end's waiters look again, and a waiter drains its endpoint's queue,
 * looks at the ring and waits on the endpoint (OBJECT_WAIT) when there is nothing to do; a message the kernel refuses
 * because four are queued is nothing lost, since the waiter looks anyway. The end of the file and EPIPE are the
 * kernel's PEER_CLOSED: a reader meets it once every handle of the write end's endpoint in every process is closed,
 * a writer once the read end's is. The ring's head is the readers' word and its tail the writers', each advanced with
 * release and read with acquire, so one process reads an end and one writes it at a time, which is what a parent and
 * a child do. NativeAOT's System.Native needs a pipe for its signal handling thread before Console writes a byte.
 *
 * Two locks: the table lock of __wit_syscall guards the descriptors and each pipe's References (the descriptors of
 * its ends and the transfers in flight, so a pipe outlives a close that races a blocked read), and the pipe's own
 * lock guards its bytes and the counts of open ends. The table lock comes first; a transfer holds neither while it
 * parks. Linux's semantics: a read of an empty pipe whose writers are all closed is the end of the file, a write to a
 * pipe without a reader fails with EPIPE after raising SIGPIPE, writes of at most PIPE_BUF bytes are atomic, and an
 * end opened with O_NONBLOCK answers EAGAIN instead of parking. poll (R3.2) reads an end's readiness as Linux reports
 * it and parks on one word that every change of every pipe bumps; while a shared pipe lives, that park ends every
 * 10 ms, since a change in another process bumps no word of this one. */

#define PIPES 16U
#define PIPE_BYTES 16384U /* Linux's PIPE_BUF (4096) fits four times */
#define PIPE_ATOMIC 4096U
#define PAGE 4096U
#define RING_BYTES (PAGE + PIPE_BYTES) /* the shared ring's object: its header page and the bytes */
#define FUTEX_WAIT 0
#define FUTEX_WAKE 1
#define FUTEX_PRIVATE 128
#define SHARED_POLL_NANOSECONDS 10000000ULL
#define READ_END 0
#define WRITE_END 1

/* The header page of a shared ring: the readers' head and the writers' tail, free-running counts of bytes. */
typedef struct Ring {
    volatile WitU32 Head, Tail;
    WitU32 Capacity, Reserved;
} Ring;

typedef struct Pipe {
    volatile int Lock;
    volatile int Sequence; /* bumped and woken on every change of the bytes or the open ends */
    WitU32 Readers, Writers; /* open descriptors of each end in this process, under Lock */
    WitU32 References; /* descriptors and transfers in flight, under the table lock */
    WitU32 Head, Count; /* the ring within the process */
    unsigned char *Buffer; /* the ring within the process, or the mapped object of a shared one */
    int Shared; /* the ring is a memory object and the ends have endpoints (N4.2) */
    WitU64 Object; /* the shared ring's object */
    WitU64 Endpoints[2]; /* the read end's and the write end's wakeup endpoints; zero for an end this process lacks */
    int PeerClosed[2]; /* [READ_END]: every write end's endpoint is closed; [WRITE_END]: every read end's is */
} Pipe;

static Pipe pipes[PIPES];
static volatile int poll_sequence; /* bumped and woken on every change of any pipe */
static WitU32 shared_pipes; /* live shared pipes, which poll's park looks after every 10 ms */

static void close_handle(WitU64 handle)
{
    WitU64 result = 0;
    if (handle) {
        wit_syscall(WIT_CALL_HANDLE_CLOSE, handle, 0, 0, &result);
    }
}

/* An empty message on an end's endpoint: the other end's waiters look again. A full queue carries the wakeup already;
 * PEER_CLOSED says the other end is gone. */
static void wake(Pipe *p, int end)
{
    WitChannelMessage message;
    WitU64 result = 0;
    if (!p->Endpoints[end]) {
        return;
    }
    memset(&message, 0, sizeof(message));
    message.Version = WIT_CHANNEL_MESSAGE_VERSION;
    message.Size = sizeof(message);
    if (wit_syscall(WIT_CALL_CHANNEL_SEND, p->Endpoints[end], (WitU64)&message, sizeof(message), &result) ==
        WIT_STATUS_PEER_CLOSED) {
        p->PeerClosed[end] = 1;
    }
}

/* Takes every queued wakeup off an end's endpoint, noting the other end's close. */
static void drain(Pipe *p, int end)
{
    WitChannelMessage message;
    WitU64 result = 0, status;
    if (!p->Endpoints[end]) {
        return;
    }
    memset(&message, 0, sizeof(message));
    message.Version = WIT_CHANNEL_MESSAGE_VERSION;
    message.Size = sizeof(message);
    while ((status = wit_syscall(WIT_CALL_CHANNEL_RECEIVE, p->Endpoints[end], (WitU64)&message, sizeof(message),
                &result)) == WIT_STATUS_OK) {
    }
    if (status == WIT_STATUS_PEER_CLOSED) {
        p->PeerClosed[end] = 1;
    }
}

static void changed(Pipe *p)
{
    __atomic_add_fetch(&p->Sequence, 1, __ATOMIC_RELEASE);
    __wit_futex(&p->Sequence, FUTEX_WAKE | FUTEX_PRIVATE, 0x7FFFFFFF, 0, 0, 0);
    __atomic_add_fetch(&poll_sequence, 1, __ATOMIC_RELEASE);
    __wit_futex(&poll_sequence, FUTEX_WAKE | FUTEX_PRIVATE, 0x7FFFFFFF, 0, 0, 0);
}

/* Parks until the sequence moves on from what the caller saw under the lock: -EINTR when a signal ran a handler. */
static long park(Pipe *p, int seen)
{
    const long r = __wit_futex(&p->Sequence, FUTEX_WAIT | FUTEX_PRIVATE, seen, 0, 0, 0);
    return r == -EINTR ? -EINTR : 0;
}

/* Parks on an end's endpoint of a shared pipe until a wakeup or the other end's close: -EINTR after a handler. The
 * caller drained the queue before it looked at the ring, so a change since then has its message queued and the wait
 * ends at once. */
static long park_shared(Pipe *p, int end)
{
    WitUserWaitRequest wait;
    WitU64 result = 0;
    if (!p->Endpoints[end]) {
        return 0;
    }
    memset(&wait, 0, sizeof(wait));
    wait.Version = WIT_WAIT_OBJECTS_VERSION;
    wait.Size = sizeof(wait);
    wait.Handles = (WitU64)&p->Endpoints[end];
    wait.Count = 1;
    wait.Deadline = WIT_WAIT_INFINITE;
    const WitU64 status = wit_syscall(WIT_CALL_OBJECT_WAIT, (WitU64)&wait, sizeof(wait), 0, &result);
    if (status == WIT_STATUS_PEER_CLOSED) {
        p->PeerClosed[end] = 1;
    }
    return status == WIT_STATUS_INTERRUPTED ? -EINTR : 0;
}

static Ring *ring_of(const Pipe *p)
{
    return (Ring *)p->Buffer;
}

static unsigned char *ring_bytes(const Pipe *p)
{
    return p->Buffer + PAGE;
}

/* The bytes a shared ring holds. */
static WitU32 ring_count(const Pipe *p)
{
    const Ring *ring = ring_of(p);
    return __atomic_load_n(&ring->Tail, __ATOMIC_ACQUIRE) - __atomic_load_n(&ring->Head, __ATOMIC_ACQUIRE);
}

/* Maps a shared ring's object into this process. */
static long map_ring(Pipe *p)
{
    WitMemoryMapRequest request;
    WitU64 result = 0;
    memset(&request, 0, sizeof(request));
    request.Version = WIT_MEMORY_MAP_VERSION;
    request.Size = sizeof(request);
    request.Object = p->Object;
    request.Bytes = RING_BYTES;
    request.Protection = WIT_MEMORY_READ | WIT_MEMORY_WRITE;
    request.Target = WIT_PROCESS_SELF;
    const WitU64 status = wit_syscall(WIT_CALL_MEMORY_OBJECT_MAP, (WitU64)&request, sizeof(request), 0, &result);
    if (status != WIT_STATUS_OK) {
        return status == WIT_STATUS_NO_MEMORY ? -ENOMEM : __wit_errno(status);
    }
    p->Buffer = (unsigned char *)result;
    return 0;
}

/* A new pipe with one reader and one writer, both descriptors referencing it. Under the table lock. */
long __wit_pipe_create(WitU32 *index)
{
    for (WitU32 i = 0; i < PIPES; ++i) {
        Pipe *p = &pipes[i];
        if (p->References) {
            continue;
        }
        const long buffer = __wit_mmap(0, PIPE_BYTES, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
        if (buffer < 0) {
            return buffer;
        }
        memset(p, 0, sizeof(*p));
        p->Buffer = (unsigned char *)buffer;
        p->Readers = 1;
        p->Writers = 1;
        p->References = 2;
        *index = i;
        return 0;
    }
    return -ENFILE;
}

/* Shares the pipe (N4.2): its bytes move into a memory object, its ends get the two endpoints of a channel, and the
 * waiters parked on the futex look again and find the shared ring. Under the table lock; a pipe shared already stays
 * as it is. */
long __wit_pipe_share(WitU32 index)
{
    Pipe *p = &pipes[index];
    WitU64 ends[2] = {0, 0}, object = 0, result = 0;
    __wit_lock(&p->Lock);
    if (p->Shared) {
        __wit_unlock(&p->Lock);
        return 0;
    }
    WitU64 status = wit_syscall(WIT_CALL_MEMORY_OBJECT_CREATE, RING_BYTES, 0, 0, &object);
    if (status == WIT_STATUS_OK) {
        status = wit_syscall(WIT_CALL_CHANNEL_CREATE, (WitU64)ends, 0, 0, &result);
    }
    if (status != WIT_STATUS_OK) {
        close_handle(object);
        __wit_unlock(&p->Lock);
        return status == WIT_STATUS_NO_MEMORY ? -ENOMEM : __wit_errno(status);
    }
    unsigned char *buffer = p->Buffer;
    const WitU32 head = p->Head, count = p->Count;
    p->Object = object;
    const long mapped = map_ring(p);
    if (mapped < 0) {
        p->Object = 0;
        p->Buffer = buffer;
        close_handle(object);
        close_handle(ends[0]);
        close_handle(ends[1]);
        __wit_unlock(&p->Lock);
        return mapped;
    }
    Ring *ring = ring_of(p);
    ring->Capacity = PIPE_BYTES;
    for (WitU32 i = 0; i < count; ++i) {
        ring_bytes(p)[i] = buffer[(head + i) % PIPE_BYTES];
    }
    ring->Head = 0;
    __atomic_store_n(&ring->Tail, count, __ATOMIC_RELEASE);
    p->Endpoints[READ_END] = ends[0];
    p->Endpoints[WRITE_END] = ends[1];
    p->PeerClosed[READ_END] = p->PeerClosed[WRITE_END] = 0;
    p->Shared = 1;
    ++shared_pipes;
    __wit_munmap((long)buffer, PIPE_BYTES);
    changed(p); /* the parked waiters look again and find the shared ring */
    __wit_unlock(&p->Lock);
    return 0;
}

/* The handles a child takes for an end of a shared pipe (N4.2): duplicates of the ring's object and of the end's
 * endpoint, the caller's to move. Under the table lock. */
long __wit_pipe_handles(WitU32 index, int write_end, WitU64 *object, WitU64 *endpoint)
{
    Pipe *p = &pipes[index];
    WitU64 result = 0;
    *object = *endpoint = 0;
    if (!p->Shared || !p->Endpoints[write_end ? WRITE_END : READ_END]) {
        return -EBADF;
    }
    WitU64 status = wit_syscall(WIT_CALL_HANDLE_DUPLICATE, p->Object, (WitU64)object,
        WIT_RIGHT_MAP | WIT_RIGHT_WRITE | WIT_RIGHT_DUPLICATE | WIT_RIGHT_TRANSFER, &result);
    if (status == WIT_STATUS_OK) {
        status = wit_syscall(WIT_CALL_HANDLE_DUPLICATE, p->Endpoints[write_end ? WRITE_END : READ_END],
            (WitU64)endpoint,
            WIT_RIGHT_WAIT | WIT_RIGHT_SEND | WIT_RIGHT_RECEIVE | WIT_RIGHT_DUPLICATE | WIT_RIGHT_TRANSFER, &result);
    }
    if (status != WIT_STATUS_OK) {
        close_handle(*object);
        *object = *endpoint = 0;
        return status == WIT_STATUS_NO_MEMORY ? -ENOMEM : __wit_errno(status);
    }
    return 0;
}

/* An end of a shared pipe another process gave this one (N4.2): a pipe record over the object and the endpoint, both
 * now the record's, with one descriptor of that end. Under the table lock. */
long __wit_pipe_adopt(WitU64 object, WitU64 endpoint, int write_end, WitU32 *index)
{
    for (WitU32 i = 0; i < PIPES; ++i) {
        Pipe *p = &pipes[i];
        if (p->References) {
            continue;
        }
        memset(p, 0, sizeof(*p));
        p->Object = object;
        const long mapped = map_ring(p);
        if (mapped < 0) {
            p->Object = 0;
            return mapped;
        }
        if (ring_of(p)->Capacity != PIPE_BYTES) {
            WitU64 result = 0;
            wit_syscall(WIT_CALL_MEMORY_RELEASE, (WitU64)p->Buffer, 0, 0, &result);
            p->Buffer = 0;
            p->Object = 0;
            return -EPROTO;
        }
        p->Shared = 1;
        p->Endpoints[write_end ? WRITE_END : READ_END] = endpoint;
        if (write_end) {
            p->Writers = 1;
        } else {
            p->Readers = 1;
        }
        p->References = 1;
        ++shared_pipes;
        *index = i;
        return 0;
    }
    return -ENFILE;
}

/* A reference for a descriptor or a transfer in flight. Under the table lock. */
void __wit_pipe_reference(WitU32 index)
{
    ++pipes[index].References;
}

/* The last reference frees the ring: the buffer, or the shared object's mapping with the object and the endpoints
 * left. Under the table lock, which the unmapping needs. */
void __wit_pipe_release(WitU32 index)
{
    Pipe *p = &pipes[index];
    if (--p->References != 0) {
        return;
    }
    if (p->Shared) {
        WitU64 result = 0;
        wit_syscall(WIT_CALL_MEMORY_RELEASE, (WitU64)p->Buffer, 0, 0, &result);
        close_handle(p->Object);
        close_handle(p->Endpoints[READ_END]);
        close_handle(p->Endpoints[WRITE_END]);
        --shared_pipes;
    } else {
        __wit_munmap((long)p->Buffer, PIPE_BYTES);
    }
    memset(p, 0, sizeof(*p));
}

/* An end gained a descriptor (dup) or lost one (close); waiters see the change. The last descriptor of an end of a
 * shared pipe closes that end's endpoint in this process: when no other process holds it, the kernel tells the other
 * end PEER_CLOSED. */
void __wit_pipe_end(WitU32 index, int write_end, int delta)
{
    Pipe *p = &pipes[index];
    __wit_lock(&p->Lock);
    if (write_end) {
        p->Writers += (WitU32)delta;
    } else {
        p->Readers += (WitU32)delta;
    }
    if (p->Shared) {
        const int end = write_end ? WRITE_END : READ_END;
        if ((write_end ? p->Writers : p->Readers) == 0 && p->Endpoints[end]) {
            close_handle(p->Endpoints[end]);
            p->Endpoints[end] = 0;
        }
        wake(p, end);
    }
    changed(p);
    __wit_unlock(&p->Lock);
}

/* A read of a shared ring: the bytes there are, or the end of the file once no writer is left anywhere. */
static long shared_read(Pipe *p, unsigned char *buffer, unsigned long bytes, int nonblocking)
{
    for (;;) {
        drain(p, READ_END); /* before the look, so that a change after it leaves a message for the wait */
        __wit_lock(&p->Lock);
        if (!p->Shared) {
            __wit_unlock(&p->Lock);
            return -EAGAIN; /* the caller looks again */
        }
        Ring *ring = ring_of(p);
        const WitU32 head = ring->Head;
        const WitU32 count = __atomic_load_n(&ring->Tail, __ATOMIC_ACQUIRE) - head;
        if (count) {
            const unsigned long done = bytes < count ? bytes : count;
            for (unsigned long i = 0; i < done; ++i) {
                buffer[i] = ring_bytes(p)[(head + i) % PIPE_BYTES];
            }
            __atomic_store_n(&ring->Head, head + (WitU32)done, __ATOMIC_RELEASE);
            wake(p, READ_END);
            changed(p);
            __wit_unlock(&p->Lock);
            return (long)done;
        }
        if (p->PeerClosed[READ_END]) {
            __wit_unlock(&p->Lock); /* no handle of the write end's endpoint is left anywhere: no writer is */
            return 0;
        }
        __wit_unlock(&p->Lock);
        if (nonblocking) {
            return -EAGAIN;
        }
        if (park_shared(p, READ_END) == -EINTR) {
            return -EINTR;
        }
    }
}

/* A write to a shared ring: every byte, parking while the ring is full; EPIPE once no reader is left anywhere. */
static long shared_write(Pipe *p, const unsigned char *buffer, unsigned long bytes, int nonblocking)
{
    unsigned long done = 0;
    while (done < bytes) {
        drain(p, WRITE_END);
        __wit_lock(&p->Lock);
        if (!p->Shared) {
            __wit_unlock(&p->Lock);
            return done ? (long)done : -EAGAIN;
        }
        if (p->PeerClosed[WRITE_END]) {
            __wit_unlock(&p->Lock); /* no handle of the read end's endpoint is left anywhere: no reader is */
            if (done) {
                return (long)done;
            }
            __wit_tkill((int)__wit_gettid(), SIGPIPE);
            return -EPIPE;
        }
        Ring *ring = ring_of(p);
        const WitU32 tail = ring->Tail;
        const unsigned long room = PIPE_BYTES - (tail - __atomic_load_n(&ring->Head, __ATOMIC_ACQUIRE));
        const unsigned long wanted = bytes - done;
        if (room && (wanted > PIPE_ATOMIC || room >= wanted)) {
            const unsigned long chunk = wanted < room ? wanted : room;
            for (unsigned long i = 0; i < chunk; ++i) {
                ring_bytes(p)[(tail + i) % PIPE_BYTES] = buffer[done + i];
            }
            __atomic_store_n(&ring->Tail, tail + (WitU32)chunk, __ATOMIC_RELEASE);
            done += chunk;
            wake(p, WRITE_END);
            changed(p);
            __wit_unlock(&p->Lock);
            continue;
        }
        __wit_unlock(&p->Lock);
        if (nonblocking) {
            return done ? (long)done : -EAGAIN;
        }
        if (park_shared(p, WRITE_END) == -EINTR) {
            return done ? (long)done : -EINTR;
        }
    }
    return (long)done;
}

/* What a reader takes: the bytes there are, at least one, or the end of the file once no writer is left. */
long __wit_pipe_read(WitU32 index, unsigned char *buffer, unsigned long bytes, int nonblocking)
{
    Pipe *p = &pipes[index];
    if (bytes == 0) {
        return 0;
    }
    for (;;) {
        if (__atomic_load_n(&p->Shared, __ATOMIC_ACQUIRE)) {
            const long shared = shared_read(p, buffer, bytes, nonblocking);
            if (shared != -EAGAIN || nonblocking) {
                return shared;
            }
            continue;
        }
        __wit_lock(&p->Lock);
        if (p->Shared) {
            __wit_unlock(&p->Lock);
            continue;
        }
        if (p->Count) {
            unsigned long done = 0;
            while (done < bytes && p->Count) {
                buffer[done++] = p->Buffer[p->Head];
                p->Head = (p->Head + 1) % PIPE_BYTES;
                --p->Count;
            }
            changed(p);
            __wit_unlock(&p->Lock);
            return (long)done;
        }
        if (!p->Writers) {
            __wit_unlock(&p->Lock);
            return 0;
        }
        const int seen = p->Sequence;
        __wit_unlock(&p->Lock);
        if (nonblocking) {
            return -EAGAIN;
        }
        if (park(p, seen) == -EINTR) {
            return -EINTR;
        }
    }
}

/* A writer puts every byte, parking while the pipe is full; at most PIPE_ATOMIC bytes go in one piece. */
long __wit_pipe_write(WitU32 index, const unsigned char *buffer, unsigned long bytes, int nonblocking)
{
    Pipe *p = &pipes[index];
    unsigned long done = 0;
    while (done < bytes) {
        if (__atomic_load_n(&p->Shared, __ATOMIC_ACQUIRE)) {
            const long shared = shared_write(p, buffer + done, bytes - done, nonblocking);
            if (shared == -EAGAIN && !nonblocking) {
                continue;
            }
            return shared < 0 ? (done ? (long)done : shared) : (long)done + shared;
        }
        __wit_lock(&p->Lock);
        if (p->Shared) {
            __wit_unlock(&p->Lock);
            continue;
        }
        if (!p->Readers) {
            __wit_unlock(&p->Lock);
            if (done) {
                return (long)done;
            }
            __wit_tkill((int)__wit_gettid(), SIGPIPE);
            return -EPIPE;
        }
        const unsigned long room = PIPE_BYTES - p->Count;
        const unsigned long wanted = bytes - done;
        /* A write of at most PIPE_ATOMIC bytes waits for room for all of them, never interleaving with another. */
        if (room && (wanted > PIPE_ATOMIC || room >= wanted)) {
            const unsigned long chunk = wanted < room ? wanted : room;
            for (unsigned long i = 0; i < chunk; ++i) {
                p->Buffer[(p->Head + p->Count) % PIPE_BYTES] = buffer[done + i];
                ++p->Count;
            }
            done += chunk;
            changed(p);
            __wit_unlock(&p->Lock);
            continue;
        }
        const int seen = p->Sequence;
        __wit_unlock(&p->Lock);
        if (nonblocking) {
            return done ? (long)done : -EAGAIN;
        }
        if (park(p, seen) == -EINTR) {
            return done ? (long)done : -EINTR;
        }
    }
    return (long)done;
}

/* An end's readiness for poll, as Linux reports a pipe's: the read end is readable with bytes and hung up once no
 * writer is left; the write end is writable while PIPE_BUF bytes fit and in error once no reader is left. */
static short readiness(const Pipe *p, int write_end, short events)
{
    const WitU32 count = p->Shared ? ring_count(p) : p->Count;
    const int writers = p->Shared ? (p->Writers || (!p->PeerClosed[READ_END] && p->Endpoints[READ_END])) : p->Writers;
    const int readers = p->Shared ? (p->Readers || (!p->PeerClosed[WRITE_END] && p->Endpoints[WRITE_END])) : p->Readers;
    if (write_end) {
        return (
            short)((PIPE_BYTES - count >= PIPE_ATOMIC ? events & (POLLOUT | POLLWRNORM) : 0) | (readers ? 0 : POLLERR));
    }
    return (short)((count ? events & (POLLIN | POLLRDNORM) : 0) | (writers ? 0 : POLLHUP));
}

short __wit_pipe_poll(WitU32 index, int write_end, short events)
{
    Pipe *p = &pipes[index];
    if (p->Shared) {
        drain(p, write_end ? WRITE_END : READ_END); /* the other end's close, if it came */
    }
    __wit_lock(&p->Lock);
    const short ready = readiness(p, write_end, events);
    __wit_unlock(&p->Lock);
    return ready;
}

/* An end's readiness with its edge (epoll.c, N4.2): the token changes whenever what an edge-triggered waiter watches
 * changed — the bytes the other side moved and the ends that are open. The local ring's sequence counts every change;
 * a shared ring's counts and ends are read directly, since another process's change bumps no word here. Both are read
 * under the one lock with the readiness, so that no change falls between the two. */
short __wit_pipe_edge(WitU32 index, int write_end, short events, WitU64 *token)
{
    Pipe *p = &pipes[index];
    if (p->Shared) {
        drain(p, write_end ? WRITE_END : READ_END);
    }
    __wit_lock(&p->Lock);
    const short ready = readiness(p, write_end, events);
    if (p->Shared) {
        const Ring *ring = ring_of(p);
        const WitU32 moved =
            write_end ? __atomic_load_n(&ring->Head, __ATOMIC_ACQUIRE) : __atomic_load_n(&ring->Tail, __ATOMIC_ACQUIRE);
        *token = ((WitU64)moved << 8) |
            (WitU64)((p->PeerClosed[READ_END] ? 1 : 0) |
                (p->PeerClosed[WRITE_END] ? 2 : 0) |
                (p->Readers ? 4 : 0) |
                (p->Writers ? 8 : 0) |
                (p->Endpoints[READ_END] ? 16 : 0) |
                (p->Endpoints[WRITE_END] ? 32 : 0));
    } else {
        *token = (WitU64)(WitU32)p->Sequence;
    }
    __wit_unlock(&p->Lock);
    return ready;
}

/* A change poll must look at that no pipe made (N4.3b: the terminal's input arrived): the word bumped and its parkers
 * woken. */
void __wit_poll_bump(void)
{
    __atomic_add_fetch(&poll_sequence, 1, __ATOMIC_RELEASE);
    __wit_futex(&poll_sequence, FUTEX_WAKE | FUTEX_PRIVATE, 0x7FFFFFFF, 0, 0, 0);
}

/* The word poll parks on: read before the readiness so that a change between the two is not lost. */
int __wit_pipe_sequence(void)
{
    return __atomic_load_n(&poll_sequence, __ATOMIC_ACQUIRE);
}

/* Parks until a pipe changed after the sequence the caller saw: 0, -ETIMEDOUT at the deadline, -EINTR after a handler.
 * While a shared pipe lives the park ends every 10 ms, since a change in another process bumps no word here. */
long __wit_pipe_wait(int seen, WitU64 deadline)
{
    if (__atomic_load_n(&shared_pipes, __ATOMIC_ACQUIRE)) {
        WitU64 now = 0, frequency = 0;
        wit_syscall(WIT_CALL_CLOCK_READ, WIT_CLOCK_MONOTONIC, 0, 0, &now);
        wit_syscall(WIT_CALL_CLOCK_FREQUENCY, WIT_CLOCK_MONOTONIC, 0, 0, &frequency);
        const WitU64 slice = now + SHARED_POLL_NANOSECONDS * frequency / 1000000000ULL;
        if (slice < deadline) {
            const long r = __wit_futex_wait_until(&poll_sequence, seen, slice);
            return r == -EAGAIN || r == -ETIMEDOUT ? 0 : r;
        }
    }
    const long r = __wit_futex_wait_until(&poll_sequence, seen, deadline);
    return r == -EAGAIN ? 0 : r;
}

/* Bytes a read would take now (FIONREAD). */
long __wit_pipe_available(WitU32 index)
{
    Pipe *p = &pipes[index];
    __wit_lock(&p->Lock);
    const long count = p->Shared ? (long)ring_count(p) : (long)p->Count;
    __wit_unlock(&p->Lock);
    return count;
}
