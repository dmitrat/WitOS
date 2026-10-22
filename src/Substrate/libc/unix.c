#define _GNU_SOURCE
#include "witos_libc.h"
#include "witos/channels.h"
#include "witos/memory_object.h"
#include "witos/wait_objects.h"
#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <signal.h>
#include <stddef.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/socket.h>
#include <sys/syscall.h>
#include <sys/time.h>
#include <sys/uio.h>
#include <sys/un.h>
#include <time.h>

/* Local sockets (plan step W4.1): AF_UNIX's stream and sequenced-packet sockets, and socketpair's of the three types,
 * without the network service. A connection is two rings of RING_BYTES in one region, a header page before them: ring r
 * carries side r's bytes to side 1 - r, its tail the writer's word and its head the reader's, free-running counts
 * written with release and read with acquire, so one process writes a ring and one reads it, as a shared pipe's
 * (pipe.c, N4.2). A stream's ring holds bytes; a sequenced packet's or a datagram's holds records, a 32-bit length then
 * the bytes padded to 4, each written whole or not at all.
 *
 * socketpair's region is a memory object of this process alone, and a waiter parks on the header's sequence word, which
 * every change bumps. A named socket's connection may cross processes: the names live in the namespace service
 * (witos/namespace.h, SOCKET_BIND, a path below a mount point or an abstract name), which keeps a listening socket's end
 * of the channel its connections arrive on and hands a connecting client a duplicate. The client makes the region, a
 * memory object, and a wakeup channel, and sends the listener a ConnectMessage with the object and the channel's second
 * end; from then on each side sends an empty message on its end after a change of its rings or its state, and a waiter
 * drains its end, looks, and waits on it (OBJECT_WAIT). Another thread of the process may have drained the wakeup a
 * waiter needed, so the wait ends every 10 ms; poll and epoll look as often while such a socket lives
 * (__wit_poll_sources), as for a shared pipe. The kernel's PEER_CLOSED on a side's end says the other side's process let
 * its end go: the side closed, or its process ended.
 *
 * As on Linux, a connection completes once it is queued at the listener, before accept, and the client may write up to
 * a ring meanwhile; the listener's queue is its channel's, four connections, and a client that finds it full waits for
 * room (EAGAIN when it must not wait). A listener that closes with connections queued drops them, and those clients read
 * ECONNRESET; a side that closes with bytes unread does the same to its peer. A path stays a socket's name until it is
 * unlinked (the service sees it in stat, listings and rename), an abstract name goes with its socket. No ancillary data
 * (SCM_RIGHTS, SCM_CREDENTIALS) is served; SO_PEERCRED reports the one account every process runs as. Every call runs
 * outside the table lock but the descriptors' own steps (syscall.c), and a socket's record lives while a descriptor or a
 * call in flight holds it; its name's close goes to the service after the last reference, outside the table lock
 * (__wit_local_flush). */

#define LOCALS 64U
#define PAGE 4096U
#define RING_BYTES 65536U /* a power of two, so the free-running counts wrap with the ring */
#define REGION_BYTES (PAGE + 2U * RING_BYTES) /* the header page and the two rings: 33 pages, one mapping */
#define MAGIC 0x584E5557U /* "WUNX" */
#define REGION_VERSION 1U
#define CONNECT_VERSION 1U
#define NAME_BYTES 108U /* sun_path */
#define SLICE_NANOSECONDS 10000000ULL
#define FUTEX_WAIT 0
#define FUTEX_WAKE 1
#define FUTEX_PRIVATE 128

/* The header page of a connection's region. */
typedef struct Header {
    WitU32 Magic, Version;
    WitU32 Capacity, Type; /* RING_BYTES; SOCK_STREAM, SOCK_SEQPACKET or SOCK_DGRAM */
    volatile WitU32 Head[2], Tail[2]; /* ring r: side r writes its tail, side 1 - r its head */
    volatile WitU32 Closed[2]; /* side s let its last descriptor go */
    volatile WitU32 Abandoned[2]; /* side s closed with bytes unread: its peer reads ECONNRESET */
    volatile WitU32 ShutWrite[2]; /* side s writes no more (SHUT_WR) */
    volatile WitU32 ShutRead[2]; /* side s reads no more (SHUT_RD): its peer's writes are EPIPE */
    volatile WitU32 Accepted; /* the listener took the connection */
    volatile int Sequence; /* a socketpair's: bumped and woken on every change */
} Header;

/* What a client sends on a listener's end, with the region's object and the second end of the wakeup channel. */
typedef struct ConnectMessage {
    WitU32 Version, Size;
    WitU32 Type, NameLength;
    unsigned char Name[NAME_BYTES]; /* the client's own address, which accept and getpeername give its peer */
} ConnectMessage;

enum {
    StateFree,
    StateFresh,
    StateBound,
    StateListening,
    StateConnected
};

typedef struct Local {
    volatile int Lock; /* the record's state, its side of the rings and its end */
    WitU32 References; /* descriptors and calls in flight, under local_lock; zero when free */
    WitU32 Id; /* the record's generation, which tells epoll a registration's socket from a later one */
    int State, Type, Busy; /* Busy: a bind, listen or connect in flight */
    int Closing; /* its last reference went and its name's close is due */
    int Side; /* a connection's: 0 for the client and the first of a pair, 1 for the accepted and the second */
    int Pair; /* the region is a socketpair's, this process's alone */
    int PeerGone; /* PEER_CLOSED on the end */
    int ResetSeen; /* ECONNRESET was reported */
    Header *Region;
    WitU64 Endpoint; /* a connection's wakeup end, or a listener's end its connections arrive on */
    WitU64 NameId; /* the service's binding of the name */
    WitU32 Accepts; /* connections accepted: a listener's edge for epoll */
    WitU32 NameLength, PeerLength;
    unsigned char Name[NAME_BYTES]; /* the address after sun_family, as getsockname gives it */
    unsigned char Peer[NAME_BYTES];
    WitU64 ReceiveTimeout, SendTimeout; /* nanoseconds; zero waits for ever */
    struct linger Linger;
} Local;

static Local locals[LOCALS];
static volatile int local_lock;
static volatile int closes; /* a record waits for its name's close */
static volatile int napping; /* the word a connect waiting for a full queue and an accept with no client park on */

static void close_handle(WitU64 handle)
{
    WitU64 result = 0;
    if (handle) {
        wit_syscall(WIT_CALL_HANDLE_CLOSE, handle, 0, 0, &result);
    }
}

static long errno_of(WitU64 status)
{
    return status == WIT_STATUS_NO_MEMORY ? -ENOMEM : __wit_errno(status);
}

static int stream(const Local *l)
{
    return l->Type == SOCK_STREAM;
}

/* The absolute deadline of a timeout in nanoseconds; WIT_WAIT_INFINITE for none. */
static WitU64 deadline_after(WitU64 nanoseconds)
{
    WitU64 deadline = WIT_WAIT_INFINITE;
    if (nanoseconds) {
        const struct timespec timeout = {(time_t)(nanoseconds / 1000000000ULL), (long)(nanoseconds % 1000000000ULL)};
        __wit_futex_deadline(&timeout, &deadline);
    }
    return deadline;
}

/* The earlier of a deadline and the end of a 10 ms slice from now; *now the clock's reading. */
static WitU64 slice_until(WitU64 deadline, WitU64 *now)
{
    WitU64 frequency = 0;
    wit_syscall(WIT_CALL_CLOCK_READ, WIT_CLOCK_MONOTONIC, 0, 0, now);
    wit_syscall(WIT_CALL_CLOCK_FREQUENCY, WIT_CLOCK_MONOTONIC, 0, 0, &frequency);
    const WitU64 slice = *now + SLICE_NANOSECONDS * frequency / 1000000000ULL;
    return slice < deadline ? slice : deadline;
}

/* A pause of a slice, or until the deadline: 0, -ETIMEDOUT at the deadline, -EINTR after a handler. */
static long nap(WitU64 deadline)
{
    WitU64 now = 0;
    if (__wit_cancel_requested()) {
        return -EINTR;
    }
    const WitU64 until = slice_until(deadline, &now);
    if (deadline != WIT_WAIT_INFINITE && now >= deadline) {
        return -ETIMEDOUT;
    }
    const long r = __wit_futex_wait_until(&napping, __atomic_load_n(&napping, __ATOMIC_ACQUIRE), until);
    return r == -EINTR ? -EINTR : 0;
}

/* Maps a region's object into this process, read and written. */
static long map_region(WitU64 object, Header **header)
{
    WitMemoryMapRequest request;
    WitU64 result = 0;
    memset(&request, 0, sizeof(request));
    request.Version = WIT_MEMORY_MAP_VERSION;
    request.Size = sizeof(request);
    request.Object = object;
    request.Bytes = REGION_BYTES;
    request.Protection = WIT_MEMORY_READ | WIT_MEMORY_WRITE;
    request.Target = WIT_PROCESS_SELF;
    const WitU64 status = wit_syscall(WIT_CALL_MEMORY_OBJECT_MAP, (WitU64)&request, sizeof(request), 0, &result);
    if (status != WIT_STATUS_OK) {
        return errno_of(status);
    }
    *header = (Header *)result;
    return 0;
}

static void unmap_region(Header *header)
{
    WitU64 result = 0;
    wit_syscall(WIT_CALL_MEMORY_RELEASE, (WitU64)header, 0, 0, &result);
}

/* A new region of the type, its object's handle kept for the caller, zeroed rings and the header set. */
static long make_region(int type, WitU64 *object, Header **header)
{
    *object = 0;
    const WitU64 status = wit_syscall(WIT_CALL_MEMORY_OBJECT_CREATE, REGION_BYTES, 0, 0, object);
    if (status != WIT_STATUS_OK) {
        return errno_of(status);
    }
    const long mapped = map_region(*object, header);
    if (mapped < 0) {
        close_handle(*object);
        *object = 0;
        return mapped;
    }
    (*header)->Magic = MAGIC;
    (*header)->Version = REGION_VERSION;
    (*header)->Capacity = RING_BYTES;
    (*header)->Type = (WitU32)type;
    return 0;
}

static unsigned char *ring_of(Header *h, int ring)
{
    return (unsigned char *)h + PAGE + (WitU64)ring * RING_BYTES;
}

/* Bytes into a ring at a free-running position, wrapping. */
static void ring_put(Header *h, int ring, WitU32 at, const unsigned char *bytes, WitU32 count)
{
    unsigned char *base = ring_of(h, ring);
    const WitU32 offset = at % RING_BYTES, first = count < RING_BYTES - offset ? count : RING_BYTES - offset;
    memcpy(base + offset, bytes, first);
    memcpy(base, bytes + first, count - first);
}

static void ring_get(Header *h, int ring, WitU32 at, unsigned char *bytes, WitU32 count)
{
    const unsigned char *base = ring_of(h, ring);
    const WitU32 offset = at % RING_BYTES, first = count < RING_BYTES - offset ? count : RING_BYTES - offset;
    memcpy(bytes, base + offset, first);
    memcpy(bytes + first, base, count - first);
}

/* The bytes of vectors from skip, at most count, into a ring at a position. */
static void gather(
    Header *h, int ring, WitU32 at, const struct iovec *vectors, int vector_count, WitU64 skip, WitU32 count)
{
    for (int i = 0; i < vector_count && count; ++i) {
        WitU64 length = vectors[i].iov_len;
        const unsigned char *base = vectors[i].iov_base;
        if (skip >= length) {
            skip -= length;
            continue;
        }
        base += skip;
        length -= skip;
        skip = 0;
        const WitU32 piece = length < count ? (WitU32)length : count;
        ring_put(h, ring, at, base, piece);
        at += piece;
        count -= piece;
    }
}

/* count bytes of a ring at a position into vectors from skip. */
static void scatter(
    Header *h, int ring, WitU32 at, const struct iovec *vectors, int vector_count, WitU64 skip, WitU32 count)
{
    for (int i = 0; i < vector_count && count; ++i) {
        WitU64 length = vectors[i].iov_len;
        unsigned char *base = vectors[i].iov_base;
        if (skip >= length) {
            skip -= length;
            continue;
        }
        base += skip;
        length -= skip;
        skip = 0;
        const WitU32 piece = length < count ? (WitU32)length : count;
        ring_get(h, ring, at, base, piece);
        at += piece;
        count -= piece;
    }
}

static WitU64 total_of(const struct iovec *vectors, int count)
{
    WitU64 total = 0;
    for (int i = 0; i < count; ++i) {
        total += vectors[i].iov_len;
    }
    return total;
}

/* A change of this side the other may wait for: a socketpair's sequence bumped, or a message on the end; and the word a
 * parked poll of this process looks at. */
static void notify(Local *l)
{
    if (l->Pair) {
        __atomic_add_fetch(&l->Region->Sequence, 1, __ATOMIC_RELEASE);
        __wit_futex(&l->Region->Sequence, FUTEX_WAKE | FUTEX_PRIVATE, 0x7FFFFFFF, 0, 0, 0);
    } else if (l->Endpoint) {
        WitChannelMessage message;
        WitU64 result = 0;
        memset(&message, 0, sizeof(message));
        message.Version = WIT_CHANNEL_MESSAGE_VERSION;
        message.Size = sizeof(message);
        if (wit_syscall(WIT_CALL_CHANNEL_SEND, l->Endpoint, (WitU64)&message, sizeof(message), &result) ==
            WIT_STATUS_PEER_CLOSED) {
            l->PeerGone = 1;
        }
    }
    __wit_poll_bump();
}

/* Takes every wakeup off a connection's end before a look, so that a change after the look leaves one for the wait;
 * notes the peer's end gone. */
static void drain(Local *l)
{
    WitChannelMessage message;
    unsigned char bytes[16];
    WitU64 result = 0, status;
    if (l->Pair || !l->Endpoint) {
        return;
    }
    memset(&message, 0, sizeof(message));
    message.Version = WIT_CHANNEL_MESSAGE_VERSION;
    message.Size = sizeof(message);
    message.Data = (WitU64)bytes;
    message.Bytes = sizeof(bytes);
    while ((status = wit_syscall(WIT_CALL_CHANNEL_RECEIVE, l->Endpoint, (WitU64)&message, sizeof(message), &result)) ==
        WIT_STATUS_OK) {
    }
    if (status == WIT_STATUS_PEER_CLOSED) {
        l->PeerGone = 1;
    }
}

/* The sequence a socketpair's waiter saw before its look. */
static int seen_of(const Local *l)
{
    return l->Pair ? __atomic_load_n(&l->Region->Sequence, __ATOMIC_ACQUIRE) : 0;
}

/* Parks until a change, the deadline or a handler: 0, -ETIMEDOUT, -EINTR. A connection across processes waits on its
 * end for a slice at most, since another thread may have drained the wakeup. */
static long park(Local *l, int seen, WitU64 deadline)
{
    if (__wit_cancel_requested()) {
        return -EINTR;
    }
    if (l->Pair) {
        const long r = __wit_futex_wait_until(&l->Region->Sequence, seen, deadline);
        return r == -EAGAIN ? 0 : r;
    }
    WitUserWaitRequest wait;
    WitU64 now = 0, result = 0;
    memset(&wait, 0, sizeof(wait));
    wait.Version = WIT_WAIT_OBJECTS_VERSION;
    wait.Size = sizeof(wait);
    wait.Handles = (WitU64)&l->Endpoint;
    wait.Count = 1;
    wait.Deadline = slice_until(deadline, &now);
    if (deadline != WIT_WAIT_INFINITE && now >= deadline) {
        return -ETIMEDOUT;
    }
    const WitU64 status = wit_syscall(WIT_CALL_OBJECT_WAIT, (WitU64)&wait, sizeof(wait), 0, &result);
    if (status == WIT_STATUS_INTERRUPTED) {
        return -EINTR;
    }
    if (status == WIT_STATUS_PEER_CLOSED) {
        l->PeerGone = 1;
    }
    return 0;
}

/* The other side let its end go: it closed, or its process ended. */
static int peer_closed(const Local *l)
{
    return l->PeerGone || __atomic_load_n(&l->Region->Closed[1 - l->Side], __ATOMIC_ACQUIRE);
}

/* ECONNRESET is due once: the listener dropped the connection before accepting it, or the peer closed with bytes of this
 * side unread. */
static int reset_due(const Local *l)
{
    const Header *h = l->Region;
    return !l->ResetSeen &&
        peer_closed(l) &&
        ((l->Side == 0 && !__atomic_load_n(&h->Accepted, __ATOMIC_ACQUIRE)) ||
            __atomic_load_n(&h->Abandoned[1 - l->Side], __ATOMIC_ACQUIRE));
}

/* The bytes a reader takes now: a stream's, or the next record's length (FIONREAD). */
static WitU32 readable(const Local *l)
{
    const Header *h = l->Region;
    const int ring = 1 - l->Side;
    const WitU32 head = h->Head[ring], count = __atomic_load_n(&h->Tail[ring], __ATOMIC_ACQUIRE) - head;
    if (stream(l) || !count) {
        return count;
    }
    WitU32 length = 0;
    ring_get((Header *)h, ring, head, (unsigned char *)&length, sizeof(length));
    return length;
}

/* The room a writer of this side has. */
static WitU32 room(const Local *l)
{
    const Header *h = l->Region;
    return RING_BYTES - (h->Tail[l->Side] - __atomic_load_n(&h->Head[l->Side], __ATOMIC_ACQUIRE));
}

/* A record of the table, holding one reference: a fresh socket of the type. */
static long allocate(int type, WitU32 *index)
{
    __wit_lock(&local_lock);
    for (WitU32 i = 0; i < LOCALS; ++i) {
        Local *l = &locals[i];
        if (l->State == StateFree && !l->References && !l->Closing) {
            const WitU32 id = l->Id + 1;
            memset(l, 0, sizeof(*l));
            l->Id = id;
            l->References = 1;
            l->State = StateFresh;
            l->Type = type;
            *index = i;
            __wit_unlock(&local_lock);
            return 0;
        }
    }
    __wit_unlock(&local_lock);
    return -ENFILE;
}

void __wit_local_reference(WitU32 index)
{
    __wit_lock(&local_lock);
    ++locals[index].References;
    __wit_unlock(&local_lock);
}

/* What the last reference leaves: a listener's end closed, which drops the connections queued there; a connection's side
 * closed in the header, the peer told, the end closed and the region let go (a socketpair's by its last side). */
static void tear_down(Local *l)
{
    if (l->State == StateListening) {
        close_handle(l->Endpoint);
        __wit_poll_sources(-1);
    }
    if (l->State == StateConnected) {
        Header *h = l->Region;
        const int side = l->Side;
        if (__atomic_load_n(&h->Tail[1 - side], __ATOMIC_ACQUIRE) != h->Head[1 - side]) {
            __atomic_store_n(&h->Abandoned[side], 1, __ATOMIC_RELEASE);
        }
        __atomic_store_n(&h->Closed[side], 1, __ATOMIC_RELEASE);
        notify(l);
        if (l->Pair) {
            if (__atomic_load_n(&h->Closed[1 - side], __ATOMIC_ACQUIRE)) {
                unmap_region(h);
            }
        } else {
            close_handle(l->Endpoint);
            unmap_region(h);
            __wit_poll_sources(-1);
        }
    }
    l->Endpoint = 0;
    l->Region = 0;
}

/* A reference goes; after the last the socket closes, and a bound one's name is due to close in the service, which
 * __wit_local_flush tells it outside the table lock. */
void __wit_local_release(WitU32 index)
{
    __wit_lock(&local_lock);
    Local *l = &locals[index];
    if (--l->References == 0) {
        tear_down(l);
        if (l->NameId) {
            l->Closing = 1;
            __atomic_store_n(&closes, 1, __ATOMIC_RELEASE);
        }
        l->State = StateFree;
    }
    __wit_unlock(&local_lock);
}

/* The service told of every name whose socket closed, outside the table lock. */
void __wit_local_flush(void)
{
    if (!__atomic_load_n(&closes, __ATOMIC_ACQUIRE)) {
        return;
    }
    for (;;) {
        WitU64 id = 0;
        __wit_lock(&local_lock);
        for (WitU32 i = 0; i < LOCALS && !id; ++i) {
            if (locals[i].Closing) {
                id = locals[i].NameId;
                locals[i].NameId = 0;
                locals[i].Closing = 0;
            }
        }
        if (!id) {
            __atomic_store_n(&closes, 0, __ATOMIC_RELEASE);
        }
        __wit_unlock(&local_lock);
        if (!id) {
            return;
        }
        __wit_remote_socket_close(id);
    }
}

WitU32 __wit_local_id(WitU32 index)
{
    return locals[index].Id;
}

static long done(WitU32 index, long result)
{
    __wit_local_release(index);
    return result;
}

/* The address a call names: *abstract, and the name's bytes without a path's zero or an abstract name's leading one;
 * zero bytes for an address of the family alone. */
static long address_in(
    const struct sockaddr *address, socklen_t length, unsigned char *name, WitU32 *count, int *abstract)
{
    if (!address) {
        return -EFAULT;
    }
    if (length < (socklen_t)sizeof(sa_family_t) || length > (socklen_t)sizeof(struct sockaddr_un)) {
        return -EINVAL;
    }
    if (address->sa_family != AF_UNIX) {
        return -EINVAL;
    }
    const char *path = ((const struct sockaddr_un *)address)->sun_path;
    const WitU32 bytes = (WitU32)(length - offsetof(struct sockaddr_un, sun_path));
    *abstract = bytes > 0 && path[0] == 0;
    if (*abstract) {
        *count = bytes - 1;
        memcpy(name, path + 1, *count);
        return 0;
    }
    WitU32 n = 0;
    while (n < bytes && path[n]) {
        ++n;
    }
    memcpy(name, path, n);
    *count = n;
    return 0;
}

/* An address as getsockname, getpeername and accept give it: the family and the stored bytes, cut to the caller's
 * room, with the whole length. */
static void address_out(const unsigned char *bytes, WitU32 count, struct sockaddr *address, socklen_t *length)
{
    if (!address || !length) {
        return;
    }
    struct sockaddr_un full;
    memset(&full, 0, sizeof(full));
    full.sun_family = AF_UNIX;
    memcpy(full.sun_path, bytes, count);
    const socklen_t whole = (socklen_t)(offsetof(struct sockaddr_un, sun_path) + count);
    memcpy(address, &full, *length < whole ? *length : whole);
    *length = whole;
}

/* The bytes getsockname gives for a name: a path with its zero where sun_path has room for it, an abstract name after
 * its zero; at most NAME_BYTES. */
static WitU32 stored_name(unsigned char *out, const unsigned char *name, WitU32 count, int abstract)
{
    if (abstract) {
        out[0] = 0;
        memcpy(out + 1, name, count); /* address_in gives at most NAME_BYTES - 1 */
        return count + 1;
    }
    memcpy(out, name, count);
    if (count == NAME_BYTES) {
        return count; /* a path that fills sun_path has no zero, as Linux keeps it */
    }
    out[count] = 0;
    return count + 1;
}

/* The service's form of a name: a path normalized from the current directory, which must lie below a mount point, or
 * an abstract name's bytes as they are. */
static long service_name(
    const unsigned char *name, WitU32 count, int abstract, int connecting, unsigned char *out, WitU32 *out_count)
{
    if (abstract) {
        memcpy(out, name, count);
        *out_count = count;
        return 0;
    }
    char path[NAME_BYTES + 1];
    if (!count) {
        return -ENOENT;
    }
    memcpy(path, name, count);
    path[count] = 0;
    return __wit_local_path(path, out, out_count, connecting);
}

static long open_local(long type, long protocol)
{
    const long kind = type & 0xF, flags = type & ~0xFL;
    WitU32 index = 0;
    if (flags & ~(long)(SOCK_NONBLOCK | SOCK_CLOEXEC)) {
        return -EINVAL;
    }
    if (protocol != 0 && protocol != PF_UNIX) {
        return -EPROTONOSUPPORT;
    }
    if (kind != SOCK_STREAM && kind != SOCK_SEQPACKET) {
        return -ESOCKTNOSUPPORT; /* a datagram socket of its own has no name to send to: socketpair's are served */
    }
    long r = allocate((int)kind, &index);
    if (r < 0) {
        return r;
    }
    r = __wit_local_install(index, (flags & SOCK_NONBLOCK ? O_NONBLOCK : 0) | (flags & SOCK_CLOEXEC ? O_CLOEXEC : 0));
    return r < 0 ? done(index, r) : r;
}

/* socketpair: two records over one region of this process, connected from the start. */
static long pair(long type, long protocol, int *fds)
{
    const long kind = type & 0xF, flags = type & ~0xFL;
    WitU32 first = 0, second = 0;
    WitU64 object = 0;
    Header *h = 0;
    if (!fds) {
        return -EFAULT;
    }
    if (flags & ~(long)(SOCK_NONBLOCK | SOCK_CLOEXEC)) {
        return -EINVAL;
    }
    if (protocol != 0 && protocol != PF_UNIX) {
        return -EPROTONOSUPPORT;
    }
    if (kind != SOCK_STREAM && kind != SOCK_SEQPACKET && kind != SOCK_DGRAM) {
        return -ESOCKTNOSUPPORT;
    }
    long r = make_region((int)kind, &object, &h);
    if (r < 0) {
        return r;
    }
    close_handle(object); /* the mapping keeps it */
    h->Accepted = 1;
    if ((r = allocate((int)kind, &first)) < 0) {
        unmap_region(h);
        return r;
    }
    if ((r = allocate((int)kind, &second)) < 0) {
        __wit_local_release(first); /* fresh: nothing to tear down */
        unmap_region(h);
        return r;
    }
    for (int side = 0; side < 2; ++side) {
        Local *l = &locals[side ? second : first];
        l->State = StateConnected;
        l->Side = side;
        l->Pair = 1;
        l->Region = h;
    }
    r = __wit_local_install_pair(
        first, second, (flags & SOCK_NONBLOCK ? O_NONBLOCK : 0) | (flags & SOCK_CLOEXEC ? O_CLOEXEC : 0), fds);
    if (r < 0) {
        __wit_local_release(first); /* the second's release lets the region go */
        __wit_local_release(second);
    }
    return r;
}

/* Takes the record's one operation at a time (bind, listen, connect): 0, or the error for a socket in the wrong state
 * or busy with another. */
static long begin(Local *l, int wanted_state, long wrong)
{
    __wit_lock(&l->Lock);
    const long r = l->Busy ? -EALREADY : l->State != wanted_state ? wrong : 0;
    if (r == 0) {
        l->Busy = 1;
    }
    __wit_unlock(&l->Lock);
    return r;
}

/* A random abstract name, as Linux's autobind gives one: five hexadecimal digits. */
static void autobind_name(unsigned char *name)
{
    static const char digits[] = "0123456789abcdef";
    WitU64 result = 0;
    unsigned char random[5];
    wit_syscall(WIT_CALL_RANDOM, (WitU64)random, sizeof(random), 0, &result);
    for (int i = 0; i < 5; ++i) {
        name[i] = (unsigned char)digits[random[i] & 0xF];
    }
}

static long bind_local(WitU32 index, const struct sockaddr *address, socklen_t length)
{
    Local *l = &locals[index];
    unsigned char name[NAME_BYTES], service[1024];
    WitU32 count = 0, service_count = 0;
    WitU64 id = 0;
    int abstract = 0;
    long r = address_in(address, length, name, &count, &abstract);
    if (r < 0) {
        return r;
    }
    const int autobind = length == (socklen_t)sizeof(sa_family_t);
    if ((r = begin(l, StateFresh, -EINVAL)) < 0) {
        return r;
    }
    for (int attempt = 0; attempt < (autobind ? 16 : 1); ++attempt) {
        if (autobind) {
            abstract = 1;
            count = 5;
            autobind_name(name);
        }
        r = service_name(name, count, abstract, 0, service, &service_count);
        if (r == 0) {
            r = __wit_remote_socket_bind(service, service_count, abstract, l->Type, &id);
        }
        if (r != -EADDRINUSE) {
            break;
        }
    }
    __wit_lock(&l->Lock);
    if (r == 0) {
        l->NameId = id;
        l->NameLength = stored_name(l->Name, name, count, abstract);
        l->State = StateBound;
    }
    l->Busy = 0;
    __wit_unlock(&l->Lock);
    return r;
}

static long listen_local(WitU32 index)
{
    Local *l = &locals[index];
    WitU64 ends[2] = {0, 0}, offered = 0, result = 0;
    int moved = 0;
    __wit_lock(&l->Lock);
    const int listening = l->State == StateListening;
    __wit_unlock(&l->Lock);
    if (listening) {
        return 0; /* a second listen changes the backlog alone, which the channel's queue sets */
    }
    long r = begin(l, StateBound, -EINVAL);
    if (r < 0) {
        return r;
    }
    WitU64 status = wit_syscall(WIT_CALL_CHANNEL_CREATE, (WitU64)ends, 0, 0, &result);
    if (status == WIT_STATUS_OK) {
        status = wit_syscall(WIT_CALL_HANDLE_DUPLICATE, ends[0], (WitU64)&offered,
            WIT_RIGHT_SEND | WIT_RIGHT_DUPLICATE | WIT_RIGHT_TRANSFER, &result);
    }
    r = status == WIT_STATUS_OK ? __wit_remote_socket_listen(l->NameId, offered, &moved) : errno_of(status);
    if (!moved) {
        close_handle(offered);
    }
    close_handle(ends[0]); /* the service holds the sending end; this socket receives on the other */
    __wit_lock(&l->Lock);
    if (r == 0) {
        l->Endpoint = ends[1];
        l->State = StateListening;
    } else {
        close_handle(ends[1]);
    }
    l->Busy = 0;
    __wit_unlock(&l->Lock);
    if (r == 0) {
        __wit_poll_sources(1);
    }
    return r;
}

/* A connection to a listener: the region and the wakeup channel made, the listener sent the region's object and the
 * channel's second end on the end the service handed over; complete once queued there. */
static long connect_local(WitU32 index, int nonblocking, const struct sockaddr *address, socklen_t length)
{
    Local *l = &locals[index];
    unsigned char name[NAME_BYTES], service[1024];
    WitU32 count = 0, service_count = 0;
    WitU64 listener = 0, object = 0, shared = 0, ends[2] = {0, 0}, result = 0;
    Header *h = 0;
    int abstract = 0;
    long r = address_in(address, length, name, &count, &abstract);
    if (r < 0) {
        return r;
    }
    if (length == (socklen_t)sizeof(sa_family_t)) {
        return -EINVAL;
    }
    __wit_lock(&l->Lock);
    r = l->Busy ? -EALREADY : l->State == StateConnected ? -EISCONN : l->State == StateListening ? -EINVAL : 0;
    if (r == 0) {
        l->Busy = 1;
    }
    __wit_unlock(&l->Lock);
    if (r < 0) {
        return r;
    }
    r = service_name(name, count, abstract, 1, service, &service_count);
    if (r == 0) {
        r = __wit_remote_socket_connect(service, service_count, abstract, l->Type, &listener);
    }
    if (r == 0) {
        r = make_region(l->Type, &object, &h);
    }
    if (r == 0) {
        WitU64 status = wit_syscall(WIT_CALL_CHANNEL_CREATE, (WitU64)ends, 0, 0, &result);
        if (status == WIT_STATUS_OK) {
            status = wit_syscall(WIT_CALL_HANDLE_DUPLICATE, object, (WitU64)&shared,
                WIT_RIGHT_MAP | WIT_RIGHT_WRITE | WIT_RIGHT_TRANSFER, &result);
        }
        r = status == WIT_STATUS_OK ? 0 : errno_of(status);
    }
    if (r == 0) {
        ConnectMessage request;
        WitChannelMessage message;
        WitU64 handles[2] = {shared, ends[1]};
        const WitU64 deadline = deadline_after(l->SendTimeout);
        memset(&request, 0, sizeof(request));
        request.Version = CONNECT_VERSION;
        request.Size = sizeof(request);
        request.Type = (WitU32)l->Type;
        __wit_lock(&l->Lock);
        request.NameLength = l->NameLength;
        memcpy(request.Name, l->Name, l->NameLength);
        __wit_unlock(&l->Lock);
        memset(&message, 0, sizeof(message));
        message.Version = WIT_CHANNEL_MESSAGE_VERSION;
        message.Size = sizeof(message);
        message.Data = (WitU64)&request;
        message.Bytes = sizeof(request);
        message.Handles = (WitU64)handles;
        message.HandleCount = 2;
        for (;;) {
            const WitU64 status =
                wit_syscall(WIT_CALL_CHANNEL_SEND, listener, (WitU64)&message, sizeof(message), &result);
            if (status == WIT_STATUS_OK) {
                shared = ends[1] = 0; /* moved */
                break;
            }
            if (status == WIT_STATUS_PEER_CLOSED) {
                r = -ECONNREFUSED; /* the listener closed */
                break;
            }
            if (status != WIT_STATUS_BUSY) {
                r = errno_of(status);
                break;
            }
            if (nonblocking) {
                r = -EAGAIN; /* the listener's queue is full, as Linux answers a nonblocking connect */
                break;
            }
            const long waited = nap(deadline);
            if (waited < 0) {
                r = waited == -ETIMEDOUT ? -EAGAIN : waited;
                break;
            }
        }
    }
    close_handle(listener);
    close_handle(shared);
    close_handle(ends[1]);
    close_handle(object); /* the mapping keeps it */
    __wit_lock(&l->Lock);
    if (r == 0) {
        l->Region = h;
        l->Endpoint = ends[0];
        l->Side = 0;
        l->PeerLength = stored_name(l->Peer, name, count, abstract);
        l->State = StateConnected;
    } else {
        close_handle(ends[0]);
        if (h) {
            unmap_region(h);
        }
    }
    l->Busy = 0;
    __wit_unlock(&l->Lock);
    if (r == 0) {
        __wit_poll_sources(1);
    }
    return r;
}

/* One connection off the listener's queue: its region mapped and checked, a record for it; -EAGAIN when none is
 * queued, -ECONNABORTED for one that was no connection of a client, which is dropped. */
static long take_connection(Local *listener, WitU32 *accepted)
{
    ConnectMessage request;
    unsigned char bytes[WIT_CHANNEL_MESSAGE_BYTES];
    WitU64 handles[WIT_CHANNEL_MESSAGE_HANDLES] = {0, 0, 0, 0}, result = 0;
    WitChannelMessage message;
    Header *h = 0;
    memset(&message, 0, sizeof(message));
    message.Version = WIT_CHANNEL_MESSAGE_VERSION;
    message.Size = sizeof(message);
    message.Data = (WitU64)bytes;
    message.Bytes = sizeof(bytes);
    message.Handles = (WitU64)handles;
    message.HandleCount = WIT_CHANNEL_MESSAGE_HANDLES;
    const WitU64 status =
        wit_syscall(WIT_CALL_CHANNEL_RECEIVE, listener->Endpoint, (WitU64)&message, sizeof(message), &result);
    if (status == WIT_STATUS_TIMED_OUT || status == WIT_STATUS_PEER_CLOSED) {
        return -EAGAIN; /* none queued; after PEER_CLOSED none ever comes, since the name is gone */
    }
    if (status != WIT_STATUS_OK) {
        return errno_of(status);
    }
    const WitU32 received = (WitU32)(result & 0xFFFFFFFFULL), handle_count = (WitU32)(result >> 32);
    memcpy(&request, bytes, sizeof(request) < received ? sizeof(request) : received);
    long r = received == sizeof(request) &&
            handle_count == 2 &&
            request.Version == CONNECT_VERSION &&
            request.Size == sizeof(request) &&
            request.Type == (WitU32)listener->Type &&
            request.NameLength <= NAME_BYTES
        ? map_region(handles[0], &h)
        : -ECONNABORTED;
    if (r == 0 &&
        (h->Magic != MAGIC || h->Version != REGION_VERSION || h->Capacity != RING_BYTES || h->Type != request.Type)) {
        unmap_region(h);
        r = -ECONNABORTED;
    }
    if (r == 0 && (r = allocate(listener->Type, accepted)) < 0) {
        unmap_region(h);
    }
    if (r < 0) {
        for (WitU32 i = 0; i < handle_count && i < WIT_CHANNEL_MESSAGE_HANDLES; ++i) {
            close_handle(handles[i]); /* the client reads ECONNRESET */
        }
        return r;
    }
    close_handle(handles[0]); /* the mapping keeps it */
    Local *l = &locals[*accepted];
    l->Region = h;
    l->Endpoint = handles[1];
    l->Side = 1;
    l->State = StateConnected;
    __wit_lock(&listener->Lock);
    l->NameLength = listener->NameLength;
    memcpy(l->Name, listener->Name, listener->NameLength);
    ++listener->Accepts;
    __wit_unlock(&listener->Lock);
    l->PeerLength = request.NameLength;
    memcpy(l->Peer, request.Name, request.NameLength);
    __atomic_store_n(&h->Accepted, 1, __ATOMIC_RELEASE);
    __wit_poll_sources(1);
    notify(l);
    return 0;
}

static long accept_local(WitU32 index, int nonblocking, struct sockaddr *address, socklen_t *length, long flags)
{
    Local *l = &locals[index];
    WitU32 accepted = 0;
    if (flags & ~(long)(SOCK_NONBLOCK | SOCK_CLOEXEC)) {
        return -EINVAL;
    }
    __wit_lock(&l->Lock);
    const int listening = l->State == StateListening;
    __wit_unlock(&l->Lock);
    if (!listening) {
        return -EINVAL;
    }
    const WitU64 deadline = deadline_after(l->ReceiveTimeout);
    for (;;) {
        const long r = take_connection(l, &accepted);
        if (r == 0) {
            break;
        }
        if (r == -ECONNABORTED) {
            continue; /* a stray message: the next one */
        }
        if (r != -EAGAIN || nonblocking) {
            return r;
        }
        /* Waiting consumes nothing, so every accepting thread sees each connection the queue holds. */
        WitUserWaitRequest wait;
        WitU64 now = 0, result = 0;
        if (__wit_cancel_requested()) {
            return -EINTR;
        }
        memset(&wait, 0, sizeof(wait));
        wait.Version = WIT_WAIT_OBJECTS_VERSION;
        wait.Size = sizeof(wait);
        wait.Handles = (WitU64)&l->Endpoint;
        wait.Count = 1;
        wait.Deadline = deadline;
        wit_syscall(WIT_CALL_CLOCK_READ, WIT_CLOCK_MONOTONIC, 0, 0, &now);
        if (deadline != WIT_WAIT_INFINITE && now >= deadline) {
            return -EAGAIN; /* SO_RCVTIMEO passed, as Linux answers */
        }
        const WitU64 status = wit_syscall(WIT_CALL_OBJECT_WAIT, (WitU64)&wait, sizeof(wait), 0, &result);
        if (status == WIT_STATUS_INTERRUPTED) {
            return -EINTR;
        }
        if (status == WIT_STATUS_PEER_CLOSED) {
            const long napped = nap(deadline); /* the name is gone: no connection comes, the call waits as Linux's */
            if (napped < 0) {
                return napped == -ETIMEDOUT ? -EAGAIN : napped;
            }
        }
    }
    const long fd = __wit_local_install(
        accepted, (flags & SOCK_NONBLOCK ? O_NONBLOCK : 0) | (flags & SOCK_CLOEXEC ? O_CLOEXEC : 0));
    if (fd < 0) {
        __wit_local_release(accepted);
        return fd;
    }
    address_out(locals[accepted].Peer, locals[accepted].PeerLength, address, length);
    return fd;
}

/* send, write: a stream's bytes as the ring takes them, waiting for room unless the call must not wait; a record whole.
 * EPIPE raises SIGPIPE unless MSG_NOSIGNAL. */
static long transmit(Local *l, int nonblocking, const struct iovec *vectors, int count, long flags)
{
    if (flags & MSG_OOB) {
        return -EOPNOTSUPP;
    }
    nonblocking = nonblocking || (flags & MSG_DONTWAIT);
    __wit_lock(&l->Lock);
    const int connected = l->State == StateConnected;
    __wit_unlock(&l->Lock);
    if (!connected) {
        return -ENOTCONN;
    }
    const WitU64 total = total_of(vectors, count);
    const WitU64 deadline = deadline_after(l->SendTimeout);
    const int records = !stream(l);
    if (records && total + 4 > RING_BYTES) {
        return -EMSGSIZE;
    }
    WitU64 sent = 0;
    long error = 0;
    for (;;) {
        drain(l);
        const int seen = seen_of(l);
        __wit_lock(&l->Lock);
        Header *h = l->Region;
        const int me = l->Side;
        if (h->ShutWrite[me] || peer_closed(l) || __atomic_load_n(&h->ShutRead[1 - me], __ATOMIC_ACQUIRE)) {
            __wit_unlock(&l->Lock);
            error = -EPIPE;
            break;
        }
        const WitU32 free = room(l), tail = h->Tail[me];
        if (records) {
            const WitU32 need = 4 + (((WitU32)total + 3) & ~3U);
            if (free >= need) {
                const WitU32 length = (WitU32)total;
                ring_put(h, me, tail, (const unsigned char *)&length, sizeof(length));
                gather(h, me, tail + 4, vectors, count, 0, length);
                __atomic_store_n(&h->Tail[me], tail + need, __ATOMIC_RELEASE);
                __wit_unlock(&l->Lock);
                notify(l);
                return (long)total;
            }
        } else if (free && sent < total) {
            const WitU32 put = total - sent < free ? (WitU32)(total - sent) : free;
            gather(h, me, tail, vectors, count, sent, put);
            __atomic_store_n(&h->Tail[me], tail + put, __ATOMIC_RELEASE);
            sent += put;
            __wit_unlock(&l->Lock);
            notify(l);
            if (sent == total) {
                return (long)sent;
            }
            continue;
        } else if (sent == total) {
            __wit_unlock(&l->Lock);
            return (long)sent; /* nothing to send */
        }
        __wit_unlock(&l->Lock);
        if (nonblocking) {
            error = -EAGAIN;
            break;
        }
        const long waited = park(l, seen, deadline);
        if (waited < 0) {
            error = waited == -ETIMEDOUT ? -EAGAIN : waited;
            break;
        }
    }
    if (sent) {
        return (long)sent;
    }
    if (error == -EPIPE && !(flags & MSG_NOSIGNAL)) {
        __wit_tkill((int)__wit_gettid(), SIGPIPE);
    }
    return error;
}

/* recv, read: a stream's bytes, all the room asks with MSG_WAITALL; a record, cut to the room (MSG_TRUNC gives its whole
 * length and recvmsg's flags the cut); zero once the peer writes no more, ECONNRESET once for a reset connection. */
static long receive(Local *l, int nonblocking, const struct iovec *vectors, int count, long flags, int *message_flags)
{
    if (flags & MSG_OOB) {
        return -EINVAL;
    }
    nonblocking = nonblocking || (flags & MSG_DONTWAIT);
    __wit_lock(&l->Lock);
    const int connected = l->State == StateConnected;
    __wit_unlock(&l->Lock);
    if (!connected) {
        return -ENOTCONN;
    }
    const WitU64 total = total_of(vectors, count);
    const WitU64 deadline = deadline_after(l->ReceiveTimeout);
    WitU64 got = 0;
    for (;;) {
        drain(l);
        const int seen = seen_of(l);
        __wit_lock(&l->Lock);
        Header *h = l->Region;
        const int me = l->Side, ring = 1 - me;
        const WitU32 head = h->Head[ring], available = __atomic_load_n(&h->Tail[ring], __ATOMIC_ACQUIRE) - head;
        if (available && stream(l)) {
            const WitU32 take = total - got < available ? (WitU32)(total - got) : available;
            scatter(h, ring, head, vectors, count, got, take);
            if (!(flags & MSG_PEEK)) {
                __atomic_store_n(&h->Head[ring], head + take, __ATOMIC_RELEASE);
            }
            got += take;
            __wit_unlock(&l->Lock);
            if (!(flags & MSG_PEEK) && take) {
                notify(l);
            }
            if (got >= total || !(flags & MSG_WAITALL) || (flags & MSG_PEEK)) {
                return (long)got;
            }
            continue;
        }
        if (available) {
            WitU32 length = 0;
            ring_get(h, ring, head, (unsigned char *)&length, sizeof(length));
            const WitU32 take = total < length ? (WitU32)total : length;
            scatter(h, ring, head + 4, vectors, count, 0, take);
            if (!(flags & MSG_PEEK)) {
                __atomic_store_n(&h->Head[ring], head + 4 + ((length + 3) & ~3U), __ATOMIC_RELEASE);
            }
            __wit_unlock(&l->Lock);
            if (!(flags & MSG_PEEK)) {
                notify(l);
            }
            if (message_flags && length > total) {
                *message_flags |= MSG_TRUNC;
            }
            return (flags & MSG_TRUNC) ? (long)length : (long)take;
        }
        if (h->ShutRead[me] || __atomic_load_n(&h->ShutWrite[ring], __ATOMIC_ACQUIRE) || peer_closed(l) || !total) {
            const int reset = reset_due(l) && total;
            if (reset) {
                l->ResetSeen = 1;
            }
            __wit_unlock(&l->Lock);
            return got ? (long)got : reset ? -ECONNRESET : 0;
        }
        __wit_unlock(&l->Lock);
        if (got) {
            return (long)got;
        }
        if (nonblocking) {
            return -EAGAIN;
        }
        const long waited = park(l, seen, deadline);
        if (waited < 0) {
            return waited == -ETIMEDOUT ? -EAGAIN : waited;
        }
    }
}

static long shutdown_local(WitU32 index, long how)
{
    Local *l = &locals[index];
    if (how < SHUT_RD || how > SHUT_RDWR) {
        return -EINVAL;
    }
    __wit_lock(&l->Lock);
    if (l->State != StateConnected) {
        __wit_unlock(&l->Lock);
        return l->State == StateListening ? 0 : -ENOTCONN;
    }
    if (how != SHUT_WR) {
        __atomic_store_n(&l->Region->ShutRead[l->Side], 1, __ATOMIC_RELEASE);
    }
    if (how != SHUT_RD) {
        __atomic_store_n(&l->Region->ShutWrite[l->Side], 1, __ATOMIC_RELEASE);
    }
    __wit_unlock(&l->Lock);
    notify(l);
    return 0;
}

static long name_local(WitU32 index, struct sockaddr *address, socklen_t *length, int peer)
{
    Local *l = &locals[index];
    if (!address || !length) {
        return -EFAULT;
    }
    __wit_lock(&l->Lock);
    if (peer && l->State != StateConnected) {
        __wit_unlock(&l->Lock);
        return -ENOTCONN;
    }
    unsigned char bytes[NAME_BYTES];
    const WitU32 count = peer ? l->PeerLength : l->NameLength;
    memcpy(bytes, peer ? l->Peer : l->Name, count);
    __wit_unlock(&l->Lock);
    address_out(bytes, count, address, length);
    return 0;
}

static long getsockopt_local(WitU32 index, long level, long name, void *value, socklen_t *length)
{
    Local *l = &locals[index];
    if (!value || !length) {
        return -EFAULT;
    }
    if (level != SOL_SOCKET) {
        return -EOPNOTSUPP; /* no protocol of AF_UNIX has options */
    }
    if (name == SO_RCVTIMEO || name == SO_SNDTIMEO) {
        const WitU64 timeout = name == SO_RCVTIMEO ? l->ReceiveTimeout : l->SendTimeout;
        const struct timeval tv = {(time_t)(timeout / 1000000000ULL), (suseconds_t)(timeout % 1000000000ULL / 1000)};
        memcpy(value, &tv, *length < sizeof(tv) ? *length : sizeof(tv));
        *length = *length < sizeof(tv) ? *length : sizeof(tv);
        return 0;
    }
    if (name == SO_LINGER) {
        memcpy(value, &l->Linger, *length < sizeof(l->Linger) ? *length : sizeof(l->Linger));
        *length = *length < sizeof(l->Linger) ? *length : sizeof(l->Linger);
        return 0;
    }
    if (name == SO_PEERCRED) {
        /* The one account every process runs as (root, N2.2), and the process id every process has. */
        const struct ucred credentials = {(pid_t)WIT_LIBC_PROCESS_ID, 0, 0};
        if (*length < sizeof(credentials)) {
            return -EINVAL;
        }
        memcpy(value, &credentials, sizeof(credentials));
        *length = sizeof(credentials);
        return 0;
    }
    int answer;
    __wit_lock(&l->Lock);
    switch (name) {
    case SO_TYPE:
        answer = l->Type;
        break;
    case SO_DOMAIN:
        answer = AF_UNIX;
        break;
    case SO_PROTOCOL:
        answer = 0;
        break;
    case SO_ACCEPTCONN:
        answer = l->State == StateListening;
        break;
    case SO_ERROR:
        answer = l->State == StateConnected && reset_due(l) ? ECONNRESET : 0;
        if (answer) {
            l->ResetSeen = 1;
        }
        break;
    case SO_SNDBUF:
    case SO_RCVBUF:
        answer = (int)RING_BYTES;
        break;
    case SO_SNDLOWAT:
    case SO_RCVLOWAT:
        answer = 1;
        break;
    case SO_PASSCRED:
    case SO_REUSEADDR:
    case SO_KEEPALIVE:
    case SO_BROADCAST:
        answer = 0;
        break;
    default:
        __wit_unlock(&l->Lock);
        return -ENOPROTOOPT;
    }
    __wit_unlock(&l->Lock);
    if (*length < sizeof(int)) {
        return -EINVAL;
    }
    memcpy(value, &answer, sizeof(answer));
    *length = sizeof(answer);
    return 0;
}

static long setsockopt_local(WitU32 index, long level, long name, const void *value, socklen_t length)
{
    Local *l = &locals[index];
    if (!value) {
        return -EFAULT;
    }
    if (level != SOL_SOCKET) {
        return -EOPNOTSUPP;
    }
    if (name == SO_RCVTIMEO || name == SO_SNDTIMEO) {
        struct timeval tv;
        if (length < sizeof(tv)) {
            return -EINVAL;
        }
        memcpy(&tv, value, sizeof(tv));
        if (tv.tv_usec < 0 || tv.tv_usec >= 1000000) {
            return -EDOM;
        }
        const WitU64 timeout = tv.tv_sec < 0 ? 0 : (WitU64)tv.tv_sec * 1000000000ULL + (WitU64)tv.tv_usec * 1000ULL;
        *(name == SO_RCVTIMEO ? &l->ReceiveTimeout : &l->SendTimeout) = timeout;
        return 0;
    }
    if (name == SO_LINGER) {
        if (length < sizeof(l->Linger)) {
            return -EINVAL;
        }
        memcpy(&l->Linger, value, sizeof(l->Linger));
        return 0;
    }
    switch (name) {
    case SO_SNDBUF:
    case SO_RCVBUF:
    case SO_SNDBUFFORCE:
    case SO_RCVBUFFORCE:
    case SO_PASSCRED:
    case SO_REUSEADDR:
    case SO_KEEPALIVE:
    case SO_BROADCAST:
    case SO_RCVLOWAT:
        /* Accepted as Linux accepts them: the rings keep their size, and nothing here depends on the rest. */
        return length < sizeof(int) ? -EINVAL : 0;
    default:
        return -ENOPROTOOPT;
    }
}

static long sendto_local(
    WitU32 index, int nonblocking, const void *buffer, WitU64 bytes, long flags, const struct sockaddr *address)
{
    Local *l = &locals[index];
    if (address) {
        __wit_lock(&l->Lock);
        const int connected = l->State == StateConnected;
        __wit_unlock(&l->Lock);
        return connected ? -EISCONN : -EOPNOTSUPP; /* an address goes with a connection, as Linux's AF_UNIX answers */
    }
    const struct iovec vector = {(void *)buffer, bytes};
    return transmit(l, nonblocking, &vector, 1, flags);
}

static long sendmsg_local(WitU32 index, int nonblocking, const struct msghdr *message, long flags)
{
    if (!message) {
        return -EFAULT;
    }
    if (message->msg_iovlen > 1024 || (message->msg_iovlen && !message->msg_iov)) {
        return -EMSGSIZE;
    }
    if (message->msg_controllen) {
        return -EOPNOTSUPP; /* no ancillary data is served */
    }
    if (message->msg_name) {
        return sendto_local(index, nonblocking, 0, 0, flags, message->msg_name);
    }
    return transmit(&locals[index], nonblocking, message->msg_iov, (int)message->msg_iovlen, flags);
}

static long recvmsg_local(WitU32 index, int nonblocking, struct msghdr *message, long flags)
{
    Local *l = &locals[index];
    int message_flags = 0;
    if (!message) {
        return -EFAULT;
    }
    if (message->msg_iovlen > 1024 || (message->msg_iovlen && !message->msg_iov)) {
        return -EMSGSIZE;
    }
    const long r = receive(l, nonblocking, message->msg_iov, (int)message->msg_iovlen, flags, &message_flags);
    if (r >= 0) {
        if (message->msg_name) {
            socklen_t length = message->msg_namelen;
            address_out(l->Peer, l->PeerLength, message->msg_name, &length);
            message->msg_namelen = length;
        }
        message->msg_controllen = 0;
        message->msg_flags = message_flags;
    }
    return r;
}

/* The socket calls of a local socket (syscall.c sends every socket call here first, outside the table lock):
 * WIT_NOT_A_SOCKET when the call is another family's or its descriptor no local socket. */
long __wit_local_call(long n, long a1, long a2, long a3, long a4, long a5, long a6)
{
    WitU32 index;
    int nonblocking;
    if (n == SYS_socket) {
        return a1 == AF_UNIX ? open_local(a2, a3) : WIT_NOT_A_SOCKET;
    }
    if (n == SYS_socketpair) {
        return a1 == AF_UNIX ? pair(a2, a3, (int *)a4) : WIT_NOT_A_SOCKET;
    }
    if (__wit_local_lookup(a1, &index, &nonblocking) <= 0) {
        return WIT_NOT_A_SOCKET;
    }
    switch (n) {
    case SYS_bind:
        return done(index, bind_local(index, (const struct sockaddr *)a2, (socklen_t)a3));
    case SYS_connect:
        return done(index, connect_local(index, nonblocking, (const struct sockaddr *)a2, (socklen_t)a3));
    case SYS_listen:
        return done(index, listen_local(index));
#if defined(SYS_accept)
    case SYS_accept:
        return done(index, accept_local(index, nonblocking, (struct sockaddr *)a2, (socklen_t *)a3, 0));
#endif
    case SYS_accept4:
        return done(index, accept_local(index, nonblocking, (struct sockaddr *)a2, (socklen_t *)a3, a4));
    case SYS_getsockname:
        return done(index, name_local(index, (struct sockaddr *)a2, (socklen_t *)a3, 0));
    case SYS_getpeername:
        return done(index, name_local(index, (struct sockaddr *)a2, (socklen_t *)a3, 1));
    case SYS_sendto:
        return done(
            index, sendto_local(index, nonblocking, (const void *)a2, (WitU64)a3, a4, (const struct sockaddr *)a5));
    case SYS_recvfrom: {
        const struct iovec vector = {(void *)a2, (WitU64)a3};
        const long r = receive(&locals[index], nonblocking, &vector, 1, a4, 0);
        if (r >= 0 && a5 && a6) {
            address_out(locals[index].Peer, locals[index].PeerLength, (struct sockaddr *)a5, (socklen_t *)a6);
        }
        return done(index, r);
    }
    case SYS_sendmsg:
        return done(index, sendmsg_local(index, nonblocking, (const struct msghdr *)a2, a3));
    case SYS_recvmsg:
        return done(index, recvmsg_local(index, nonblocking, (struct msghdr *)a2, a3));
    case SYS_shutdown:
        return done(index, shutdown_local(index, a2));
    case SYS_getsockopt:
        return done(index, getsockopt_local(index, a2, a3, (void *)a4, (socklen_t *)a5));
    case SYS_setsockopt:
        return done(index, setsockopt_local(index, a2, a3, (const void *)a4, (socklen_t)a5));
    default:
        return done(index, -ENOSYS);
    }
}

/* read, readv, write and writev of a local socket's descriptor: WIT_NOT_A_SOCKET for any other descriptor. */
long __wit_local_transfer(long n, long fd, long a2, long a3)
{
    WitU32 index;
    int nonblocking;
    if (__wit_local_lookup(fd, &index, &nonblocking) <= 0) {
        return WIT_NOT_A_SOCKET;
    }
    if ((n == SYS_read || n == SYS_write) && a3 < 0) {
        return done(index, -EINVAL);
    }
    if ((n == SYS_readv || n == SYS_writev) && (a3 < 0 || a3 > 1024)) {
        return done(index, -EINVAL);
    }
    struct iovec vector = {(void *)a2, (WitU64)a3};
    const struct iovec *vectors = n == SYS_read || n == SYS_write ? &vector : (const struct iovec *)a2;
    const int count = n == SYS_read || n == SYS_write ? 1 : (int)a3;
    if (n == SYS_write || n == SYS_writev) {
        return done(index, transmit(&locals[index], nonblocking, vectors, count, 0));
    }
    return done(index, receive(&locals[index], nonblocking, vectors, count, 0, 0));
}

/* ioctl of a local socket's descriptor: FIONREAD (a stream's bytes, the next record's length) and FIONBIO;
 * WIT_NOT_A_SOCKET for any other descriptor. */
long __wit_local_ioctl(long fd, long command, void *argument)
{
    WitU32 index;
    int nonblocking;
    if (__wit_local_lookup(fd, &index, &nonblocking) <= 0) {
        return WIT_NOT_A_SOCKET;
    }
    if (!argument && (command == FIONREAD || command == FIONBIO)) {
        return done(index, -EFAULT);
    }
    if (command == FIONREAD) {
        Local *l = &locals[index];
        __wit_lock(&l->Lock);
        if (l->State == StateListening) {
            __wit_unlock(&l->Lock);
            return done(index, -EINVAL);
        }
        *(int *)argument = l->State == StateConnected ? (int)readable(l) : 0;
        __wit_unlock(&l->Lock);
        return done(index, 0);
    }
    if (command == FIONBIO) {
        return done(index, __wit_socket_nonblocking(fd, *(const int *)argument != 0));
    }
    return done(index, -ENOTTY);
}

/* Whether a connection is queued at a listener, without taking it: a receive with no room refuses a queued message. */
static int queued(const Local *l)
{
    WitChannelMessage message;
    WitU64 result = 0;
    memset(&message, 0, sizeof(message));
    message.Version = WIT_CHANNEL_MESSAGE_VERSION;
    message.Size = sizeof(message);
    return wit_syscall(WIT_CALL_CHANNEL_RECEIVE, l->Endpoint, (WitU64)&message, sizeof(message), &result) ==
        WIT_STATUS_TOO_LARGE;
}

/* A socket's readiness as Linux's poll reports an AF_UNIX one, with the edge token epoll compares: what the peer wrote
 * and read, the sides' shutdowns and closes; a listener's connections taken and whether one is queued. */
static short readiness(Local *l, short events, WitU64 *token)
{
    short ready = 0;
    *token = 0;
    if (l->State == StateListening) {
        const int pending = queued(l);
        *token = ((WitU64)l->Accepts << 1) | (WitU64)pending;
        return pending ? (short)(events & (POLLIN | POLLRDNORM)) : 0;
    }
    if (l->State != StateConnected) {
        return (short)((events & (POLLOUT | POLLWRNORM)) | POLLHUP); /* unconnected, as Linux reports one */
    }
    const Header *h = l->Region;
    const int me = l->Side, peer = 1 - me;
    const int gone = peer_closed(l);
    const int receive_shut = h->ShutRead[me] || __atomic_load_n(&h->ShutWrite[peer], __ATOMIC_ACQUIRE) || gone;
    const int send_shut = h->ShutWrite[me] || __atomic_load_n(&h->ShutRead[peer], __ATOMIC_ACQUIRE) || gone;
    const WitU32 arrived = __atomic_load_n(&h->Tail[peer], __ATOMIC_ACQUIRE);
    const WitU32 taken = __atomic_load_n(&h->Head[me], __ATOMIC_ACQUIRE);
    if (arrived != h->Head[peer] || receive_shut) {
        ready |= (short)(events & (POLLIN | POLLRDNORM));
    }
    if (receive_shut) {
        ready |= (short)(events & POLLRDHUP);
    }
    if (!send_shut && room(l) >= RING_BYTES / 4) {
        ready |= (short)(events & (POLLOUT | POLLWRNORM));
    }
    if (receive_shut && send_shut) {
        ready |= POLLHUP;
    }
    if (reset_due(l)) {
        ready |= POLLERR;
    }
    const WitU64 flags = (WitU64)(gone |
        (h->ShutRead[me] << 1) |
        (h->ShutWrite[me] << 2) |
        (h->ShutRead[peer] << 3) |
        (h->ShutWrite[peer] << 4) |
        (h->Accepted << 5));
    *token = (((WitU64)taken << 32) | arrived) ^ (flags * 0x9E3779B97F4A7C15ULL);
    return ready;
}

/* poll's readiness of a local socket (files.c, under the table lock: the descriptor's reference keeps the record). */
short __wit_local_poll(WitU32 index, short events)
{
    WitU64 token;
    return __wit_local_edge(index, events, &token);
}

/* The readiness with epoll's edge token (epoll.c, under the table lock). */
short __wit_local_edge(WitU32 index, short events, WitU64 *token)
{
    Local *l = &locals[index];
    if (l->State == StateConnected) {
        drain(l); /* the peer's close, if it came */
    }
    __wit_lock(&l->Lock);
    const short ready = readiness(l, events, token);
    __wit_unlock(&l->Lock);
    return ready;
}
