#define _GNU_SOURCE
#include "witos_libc.h"
#include "witos/channels.h"
#include "witos/memory_object.h"
#include "witos/socket.h"
#include "witos/wait_objects.h"
#include <errno.h>
#include <fcntl.h>
#include <netinet/in.h>
#include <poll.h>
#include <signal.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/prctl.h>
#include <sys/socket.h>
#include <sys/syscall.h>
#include <sys/time.h>
#include <sys/uio.h>
#include <time.h>

/* Sockets over the network service (plan step W2.1a, witos/socket.h): AF_INET's stream and datagram sockets of a
 * process whose start message carried the service's clients' endpoint (WitProcessContext.Network); without one,
 * socket() answers EAFNOSUPPORT. One session serves the process, connected at its first socket: a request at a time
 * through the session's Busy word, its bytes in the window, the wait for the reply holding no library lock. No
 * request of the service waits: one that would answers EAGAIN, and the service sends a readiness event for the socket
 * when its state changes, which the network thread — a detached thread of the library's, as the terminal's is — counts
 * in the socket's record and turns into a bump of the poll word (pipe.c). A blocking call reads the word, asks, and on
 * EAGAIN parks on the word until it moves, a timeout of SO_RCVTIMEO or SO_SNDTIMEO passes or a handler ran (EINTR),
 * then asks again; a nonblocking one, O_NONBLOCK or MSG_DONTWAIT, answers EAGAIN. A socket's record lives while a
 * descriptor or a call in flight holds it; its CLOSE goes to the service after the last reference, outside the table
 * lock (__wit_socket_flush). The library keeps what the service has no use for: SO_RCVTIMEO, SO_SNDTIMEO and
 * SO_LINGER, which it records, since the service's close never waits. /etc/resolv.conf (files.c) is the lease's DNS
 * servers, which CONFIGURATION gives. */

#define WINDOW WIT_SOCKET_WINDOW_BYTES
#define WINDOW_RIGHTS (WIT_RIGHT_MAP | WIT_RIGHT_WRITE | WIT_RIGHT_TRANSFER)
#define SOCKETS 256
#define FUTEX_WAIT 0
#define FUTEX_WAKE 1
#define FUTEX_PRIVATE 128
#define SERVICE_GONE (-ENETDOWN)
#define RESOLV_BYTES 160U
#define TCP_NODELAY 1 /* <netinet/tcp.h>'s, whose <sys/types.h> brings <endian.h> and its warnings with it */

typedef struct Socket {
    WitU32 References; /* the descriptors and the calls in flight; zero when free */
    WitU32 Id; /* the service's socket */
    int Type; /* SOCK_STREAM or SOCK_DGRAM */
    int Closing; /* its last reference went: the service's CLOSE is due */
    WitU64 ReceiveTimeout, SendTimeout; /* SO_RCVTIMEO and SO_SNDTIMEO in nanoseconds; zero waits for ever */
    struct linger Linger; /* SO_LINGER as set */
} Socket;

static Socket sockets[SOCKETS];
static volatile int socket_lock;
static volatile int closes; /* a record waits for its CLOSE */

static struct {
    volatile int Busy; /* a request in flight: the next request's thread waits on this word */
    WitU64 Channel; /* the library's end of the session channel */
    WitU64 Events; /* the library's end of the events channel */
    WitU64 Object; /* the window's memory object */
    unsigned char *Window; /* the window, mapped */
    int Connected;
} session;

static volatile int connect_lock;
static unsigned char resolv[RESOLV_BYTES]; /* /etc/resolv.conf as the last CONFIGURATION gave it */
static WitU32 resolv_length;

static void close_handle(WitU64 handle)
{
    WitU64 result = 0;
    if (handle) {
        wit_syscall(WIT_CALL_HANDLE_CLOSE, handle, 0, 0, &result);
    }
}

/* The readiness events the session received, whatever their socket: epoll's edge token (W2.1b). */
static volatile int events;

/* The network thread: each readiness event counted, and the poll word bumped, so that a blocked call, a parked poll or
 * an epoll wait looks again. */
static void *events_thread(void *argument)
{
    WitSocketEvent event;
    WitChannelMessage message;
    WitUserWaitRequest wait;
    WitU64 result = 0;
    (void)argument;
    __wit_prctl(PR_SET_NAME, (unsigned long)"wit-network");
    memset(&message, 0, sizeof(message));
    message.Version = WIT_CHANNEL_MESSAGE_VERSION;
    message.Size = sizeof(message);
    message.Data = (WitU64)&event;
    message.Bytes = sizeof(event);
    memset(&wait, 0, sizeof(wait));
    wait.Version = WIT_WAIT_OBJECTS_VERSION;
    wait.Size = sizeof(wait);
    wait.Handles = (WitU64)&session.Events;
    wait.Count = 1;
    wait.Deadline = WIT_WAIT_INFINITE;
    for (;;) {
        WitU64 status;
        int received = 0;
        while ((status = wit_syscall(WIT_CALL_CHANNEL_RECEIVE, session.Events, (WitU64)&message, sizeof(message),
                    &result)) == WIT_STATUS_OK) {
            if ((result & 0xFFFFFFFFULL) != sizeof(event) ||
                event.Version != WIT_SOCKET_VERSION ||
                event.Size != sizeof(event)) {
                continue;
            }
            __atomic_add_fetch(&events, 1, __ATOMIC_RELEASE);
            received = 1;
        }
        if (received) {
            __wit_poll_bump();
        }
        if (status != WIT_STATUS_TIMED_OUT) {
            __wit_poll_bump(); /* the service went: whoever waits learns it at the next request */
            return 0;
        }
        status = wit_syscall(WIT_CALL_OBJECT_WAIT, (WitU64)&wait, sizeof(wait), 0, &result);
        if (status != WIT_STATUS_OK && status != WIT_STATUS_INTERRUPTED) {
            return 0;
        }
    }
}

/* The session connected: its channel, its window, the events channel and the network thread. */
static long connect_session(void)
{
    WitU64 ends[2] = {0, 0}, events[2] = {0, 0}, handles[3] = {0, 0, 0}, result = 0, status;
    WitSocketRequest request;
    WitChannelMessage message;
    WitMemoryMapRequest map;
    long error = 0;
    if (!__wit_process.Network) {
        return -EAFNOSUPPORT;
    }
    __wit_lock(&connect_lock);
    if (session.Connected) {
        __wit_unlock(&connect_lock);
        return 0;
    }
    status = wit_syscall(WIT_CALL_CHANNEL_CREATE, (WitU64)ends, 0, 0, &result);
    if (status == WIT_STATUS_OK) {
        status = wit_syscall(WIT_CALL_CHANNEL_CREATE, (WitU64)events, 0, 0, &result);
    }
    if (status == WIT_STATUS_OK) {
        status = wit_syscall(WIT_CALL_MEMORY_OBJECT_CREATE, WINDOW, 0, 0, &session.Object);
    }
    if (status == WIT_STATUS_OK) {
        memset(&map, 0, sizeof(map));
        map.Version = WIT_MEMORY_MAP_VERSION;
        map.Size = sizeof(map);
        map.Object = session.Object;
        map.Bytes = WINDOW;
        map.Protection = WIT_MEMORY_READ | WIT_MEMORY_WRITE;
        map.Target = WIT_PROCESS_SELF;
        status = wit_syscall(WIT_CALL_MEMORY_OBJECT_MAP, (WitU64)&map, sizeof(map), 0, &result);
        session.Window = status == WIT_STATUS_OK ? (unsigned char *)result : 0;
    }
    if (status == WIT_STATUS_OK) {
        status = wit_syscall(WIT_CALL_HANDLE_DUPLICATE, session.Object, (WitU64)&handles[1], WINDOW_RIGHTS, &result);
    }
    if (status == WIT_STATUS_OK) {
        handles[0] = ends[1];
        handles[2] = events[1];
        memset(&request, 0, sizeof(request));
        request.Version = WIT_SOCKET_VERSION;
        request.Size = sizeof(request);
        request.Operation = WIT_SOCKET_CONNECT;
        request.Length = WINDOW;
        memset(&message, 0, sizeof(message));
        message.Version = WIT_CHANNEL_MESSAGE_VERSION;
        message.Size = sizeof(message);
        message.Data = (WitU64)&request;
        message.Bytes = sizeof(request);
        message.Handles = (WitU64)handles;
        message.HandleCount = 3;
        status = wit_syscall(WIT_CALL_CHANNEL_SEND, __wit_process.Network, (WitU64)&message, sizeof(message), &result);
        if (status == WIT_STATUS_OK) {
            handles[0] = handles[1] = handles[2] = 0; /* moved */
        }
    }
    if (status != WIT_STATUS_OK) {
        error = status == WIT_STATUS_PEER_CLOSED ? SERVICE_GONE : __wit_errno(status);
    } else {
        session.Events = events[0];
        error = __wit_thread_start_detached(events_thread, 0) ? -EAGAIN : 0;
    }
    if (error) {
        close_handle(handles[0]);
        close_handle(handles[1]);
        close_handle(handles[2]);
        close_handle(ends[0]);
        close_handle(events[0]);
        if (session.Window) {
            wit_syscall(WIT_CALL_MEMORY_RELEASE, (WitU64)session.Window, 0, 0, &result);
            session.Window = 0;
        }
        close_handle(session.Object);
        session.Object = 0;
        session.Events = 0;
        __wit_unlock(&connect_lock);
        return error;
    }
    session.Channel = ends[0];
    session.Connected = 1;
    __wit_unlock(&connect_lock);
    return 0;
}

/* The session's turn: one request at a time, the window the request's meanwhile. */
static long acquire(void)
{
    const long connected = connect_session();
    if (connected < 0) {
        return connected;
    }
    while (__atomic_exchange_n(&session.Busy, 1, __ATOMIC_ACQUIRE)) {
        __wit_futex(&session.Busy, FUTEX_WAIT | FUTEX_PRIVATE, 1, 0, 0, 0);
    }
    return 0;
}

static void release(void)
{
    __atomic_store_n(&session.Busy, 0, __ATOMIC_RELEASE);
    __wit_futex(&session.Busy, FUTEX_WAKE | FUTEX_PRIVATE, 1, 0, 0, 0);
}

static void make(WitSocketRequest *request, WitU32 operation, WitU32 socket)
{
    memset(request, 0, sizeof(*request));
    request->Version = WIT_SOCKET_VERSION;
    request->Size = sizeof(*request);
    request->Operation = operation;
    request->Socket = socket;
}

/* The request sent and its reply received, the wait between them without any lock: 0, or ENETDOWN when the service
 * went or broke the protocol; the reply's own error stays the caller's to read. */
static long exchange(const WitSocketRequest *request, WitSocketReply *reply)
{
    WitChannelMessage message;
    WitUserWaitRequest wait;
    WitU64 result = 0, status;
    memset(&message, 0, sizeof(message));
    message.Version = WIT_CHANNEL_MESSAGE_VERSION;
    message.Size = sizeof(message);
    message.Data = (WitU64)request;
    message.Bytes = sizeof(*request);
    status = wit_syscall(WIT_CALL_CHANNEL_SEND, session.Channel, (WitU64)&message, sizeof(message), &result);
    if (status != WIT_STATUS_OK) {
        return SERVICE_GONE;
    }
    memset(&wait, 0, sizeof(wait));
    wait.Version = WIT_WAIT_OBJECTS_VERSION;
    wait.Size = sizeof(wait);
    wait.Handles = (WitU64)&session.Channel;
    wait.Count = 1;
    wait.Deadline = WIT_WAIT_INFINITE;
    message.Data = (WitU64)reply;
    message.Bytes = sizeof(*reply);
    while ((status = wit_syscall(WIT_CALL_CHANNEL_RECEIVE, session.Channel, (WitU64)&message, sizeof(message),
                &result)) == WIT_STATUS_TIMED_OUT) {
        status = wit_syscall(WIT_CALL_OBJECT_WAIT, (WitU64)&wait, sizeof(wait), 0, &result);
        if (status != WIT_STATUS_OK && status != WIT_STATUS_INTERRUPTED) {
            return SERVICE_GONE;
        }
    }
    if (status != WIT_STATUS_OK ||
        (result & 0xFFFFFFFFULL) != sizeof(*reply) ||
        reply->Version != WIT_SOCKET_VERSION ||
        reply->Size != sizeof(*reply) ||
        reply->Operation != request->Operation) {
        return SERVICE_GONE;
    }
    return 0;
}

/* One request: the bytes of in gathered into the window at its start first, at most the window; the reply's bytes
 * scattered into out after, from the window's start; the reply's error as a negative errno. */
static long call(WitSocketRequest *request, const struct iovec *in, int in_count, WitU64 in_skip,
    const struct iovec *out, int out_count, WitU64 out_skip, WitSocketReply *reply)
{
    long error = acquire();
    if (error < 0) {
        return error;
    }
    WitU64 filled = 0;
    for (int i = 0; i < in_count && filled < request->Length; ++i) {
        WitU64 length = in[i].iov_len;
        const unsigned char *base = in[i].iov_base;
        if (in_skip >= length) {
            in_skip -= length;
            continue;
        }
        base += in_skip;
        length -= in_skip;
        in_skip = 0;
        const WitU64 piece = length < request->Length - filled ? length : request->Length - filled;
        memcpy(session.Window + filled, base, piece);
        filled += piece;
    }
    request->Window = 0;
    error = exchange(request, reply);
    if (!error && !reply->Error && out_count) {
        WitU64 left = reply->Length < WINDOW ? reply->Length : WINDOW, taken = 0;
        for (int i = 0; i < out_count && left; ++i) {
            WitU64 length = out[i].iov_len;
            unsigned char *base = out[i].iov_base;
            if (out_skip >= length) {
                out_skip -= length;
                continue;
            }
            base += out_skip;
            length -= out_skip;
            out_skip = 0;
            const WitU64 piece = length < left ? length : left;
            memcpy(base, session.Window + taken, piece);
            taken += piece;
            left -= piece;
        }
    }
    release();
    return error ? error : reply->Error ? -(long)reply->Error : 0;
}

/* A request without bytes. */
static long simple(WitSocketRequest *request, WitSocketReply *reply)
{
    return call(request, 0, 0, 0, 0, 0, 0, reply);
}

/* The record of a socket the service opened, holding one reference. Under the record lock. */
static long allocate(WitU32 id, int type, WitU32 *index)
{
    __wit_lock(&socket_lock);
    for (WitU32 i = 0; i < SOCKETS; ++i) {
        if (!sockets[i].References && !sockets[i].Closing) {
            memset(&sockets[i], 0, sizeof(sockets[i]));
            sockets[i].References = 1;
            sockets[i].Id = id;
            sockets[i].Type = type;
            *index = i;
            __wit_unlock(&socket_lock);
            return 0;
        }
    }
    __wit_unlock(&socket_lock);
    return -EMFILE;
}

void __wit_socket_reference(WitU32 index)
{
    __wit_lock(&socket_lock);
    ++sockets[index].References;
    __wit_unlock(&socket_lock);
}

/* A reference goes; after the last, the socket's CLOSE is due, which __wit_socket_flush sends outside the table lock. */
void __wit_socket_release(WitU32 index)
{
    __wit_lock(&socket_lock);
    if (--sockets[index].References == 0) {
        sockets[index].Closing = 1;
        __atomic_store_n(&closes, 1, __ATOMIC_RELEASE);
    }
    __wit_unlock(&socket_lock);
}

/* The CLOSE of every socket whose last reference went, outside the table lock. */
void __wit_socket_flush(void)
{
    WitSocketRequest request;
    WitSocketReply reply;
    if (!__atomic_load_n(&closes, __ATOMIC_ACQUIRE)) {
        return;
    }
    for (;;) {
        WitU32 id = 0;
        int found = 0;
        __wit_lock(&socket_lock);
        for (WitU32 i = 0; i < SOCKETS && !found; ++i) {
            if (sockets[i].Closing) {
                id = sockets[i].Id;
                memset(&sockets[i], 0, sizeof(sockets[i]));
                found = 1;
            }
        }
        if (!found) {
            __atomic_store_n(&closes, 0, __ATOMIC_RELEASE);
        }
        __wit_unlock(&socket_lock);
        if (!found) {
            return;
        }
        make(&request, WIT_SOCKET_CLOSE, id);
        simple(&request, &reply);
    }
}

static WitU32 id_of(WitU32 index)
{
    return sockets[index].Id; /* set before the record's first reference left the allocating thread */
}

/* epoll (W2.1b): a record's service identifier, which tells a registration's socket from a later one in the same
 * record. */
WitU32 __wit_socket_id(WitU32 index)
{
    return id_of(index);
}

/* epoll's edge token for every socket of the process: the readiness events the session received, whatever their
 * socket. The service drops an event its full queue cannot take, since the events the queue holds wake the client, and
 * a client asks again after any event (W1.2a), so an edge-triggered registration reports again after any event; one
 * whose state did not change is reported once more, as Linux's epoll may, and its owner meets EAGAIN. */
int __wit_socket_events(void)
{
    return __atomic_load_n(&events, __ATOMIC_ACQUIRE);
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

/* Parks until the poll word moved past what the caller saw before its request: 0, -EAGAIN at the deadline (Linux's
 * answer when SO_RCVTIMEO or SO_SNDTIMEO passes), -EINTR after a handler or for a cancellation point about to park. */
static long await_change(int seen, WitU64 deadline)
{
    if (__wit_cancel_requested()) {
        return -EINTR;
    }
    const long waited = __wit_pipe_wait(seen, deadline);
    return waited == -ETIMEDOUT ? -EAGAIN : waited;
}

/* An AF_INET address of the caller's, or AF_UNSPEC where allowed. */
static long address_in(const struct sockaddr *address, socklen_t length, WitSocketAddress *out, int unspecified)
{
    memset(out, 0, sizeof(*out));
    if (!address) {
        return -EFAULT;
    }
    if (length < (socklen_t)sizeof(sa_family_t)) {
        return -EINVAL;
    }
    if (address->sa_family == AF_UNSPEC && unspecified) {
        return 0;
    }
    if (address->sa_family != AF_INET) {
        return -EAFNOSUPPORT;
    }
    if (length < (socklen_t)sizeof(struct sockaddr_in)) {
        return -EINVAL;
    }
    const struct sockaddr_in *in = (const struct sockaddr_in *)address;
    out->Family = AF_INET;
    out->Port = ntohs(in->sin_port);
    memcpy(out->Address, &in->sin_addr, 4);
    return 0;
}

/* An address for the caller, cut to its room, with its whole length, as Linux writes one. */
static void address_out(const WitSocketAddress *in, struct sockaddr *address, socklen_t *length)
{
    struct sockaddr_in out;
    if (!address || !length) {
        return;
    }
    memset(&out, 0, sizeof(out));
    out.sin_family = AF_INET;
    out.sin_port = htons(in->Port);
    memcpy(&out.sin_addr, in->Address, 4);
    memcpy(address, &out, *length < sizeof(out) ? *length : sizeof(out));
    *length = sizeof(out);
}

/* The socket a descriptor names, with a reference the caller gives back: -EBADF or -ENOTSOCK otherwise. */
static long find(long fd, WitU32 *index, int *nonblocking)
{
    const long found = __wit_socket_lookup(fd, index, nonblocking);
    return found > 0 ? 0 : found == 0 ? -ENOTSOCK : found;
}

static long done(WitU32 index, long result)
{
    __wit_socket_release(index);
    __wit_socket_flush();
    return result;
}

static long open_socket(long domain, long type, long protocol)
{
    WitSocketRequest request;
    WitSocketReply reply;
    WitU32 index = 0;
    const long kind = type & 0xF, flags = type & ~0xFL;
    if (flags & ~(long)(SOCK_NONBLOCK | SOCK_CLOEXEC)) {
        return -EINVAL;
    }
    if (domain != AF_INET || !__wit_process.Network) {
        return -EAFNOSUPPORT; /* AF_INET6 is not there yet (W4.3), .NET then speaks IPv4 alone; AF_UNIX is unix.c's (W4.1) */
    }
    make(&request, WIT_SOCKET_OPEN, 0);
    request.Argument[0] = AF_INET;
    request.Argument[1] = (WitU64)kind;
    request.Argument[2] = (WitU64)protocol;
    long r = simple(&request, &reply);
    if (r < 0) {
        return r;
    }
    r = allocate(reply.Socket, kind == SOCK_STREAM ? SOCK_STREAM : SOCK_DGRAM, &index);
    if (r < 0) {
        make(&request, WIT_SOCKET_CLOSE, reply.Socket);
        simple(&request, &reply);
        return r;
    }
    const long fd =
        __wit_socket_install(index, (flags & SOCK_NONBLOCK ? O_NONBLOCK : 0) | (flags & SOCK_CLOEXEC ? O_CLOEXEC : 0));
    if (fd < 0) {
        return done(index, fd);
    }
    return fd;
}

static long bind_socket(long fd, const struct sockaddr *address, socklen_t length)
{
    WitSocketRequest request;
    WitSocketReply reply;
    WitU32 index;
    int nonblocking;
    long r = find(fd, &index, &nonblocking);
    if (r < 0) {
        return r;
    }
    make(&request, WIT_SOCKET_BIND, id_of(index));
    r = address_in(address, length, &request.Address, 0);
    return done(index, r < 0 ? r : simple(&request, &reply));
}

/* A stream socket's connection waited for: 0 once established, its SO_ERROR when it failed. */
static long await_connection(WitU32 index)
{
    WitSocketRequest request;
    WitSocketReply reply;
    const WitU64 deadline = deadline_after(sockets[index].SendTimeout);
    for (;;) {
        const int seen = __wit_pipe_sequence();
        make(&request, WIT_SOCKET_POLL, id_of(index));
        long r = simple(&request, &reply);
        if (r < 0) {
            return r;
        }
        if (reply.Value & (WIT_SOCKET_WRITABLE | WIT_SOCKET_ERROR | WIT_SOCKET_HANGUP)) {
            make(&request, WIT_SOCKET_GET_OPTION, id_of(index));
            request.Argument[0] = SOL_SOCKET;
            request.Argument[1] = SO_ERROR;
            r = simple(&request, &reply);
            return r < 0 ? r : -(long)reply.Value;
        }
        r = await_change(seen, deadline);
        if (r == -EAGAIN) {
            return -EINPROGRESS; /* SO_SNDTIMEO passed: the connection goes on, as Linux's does */
        }
        if (r < 0) {
            return r;
        }
    }
}

static long connect_socket(long fd, const struct sockaddr *address, socklen_t length)
{
    WitSocketRequest request;
    WitSocketReply reply;
    WitU32 index;
    int nonblocking;
    long r = find(fd, &index, &nonblocking);
    if (r < 0) {
        return r;
    }
    make(&request, WIT_SOCKET_CONNECT_TO, id_of(index));
    r = address_in(address, length, &request.Address, 1);
    if (r == 0) {
        r = simple(&request, &reply);
    }
    if (r == -EINPROGRESS && !nonblocking) {
        r = await_connection(index);
    }
    return done(index, r);
}

static long listen_socket(long fd, long backlog)
{
    WitSocketRequest request;
    WitSocketReply reply;
    WitU32 index;
    int nonblocking;
    long r = find(fd, &index, &nonblocking);
    if (r < 0) {
        return r;
    }
    make(&request, WIT_SOCKET_LISTEN, id_of(index));
    request.Argument[0] = backlog < 0 ? 0 : (WitU64)backlog;
    return done(index, simple(&request, &reply));
}

static long accept_socket(long fd, struct sockaddr *address, socklen_t *length, long flags)
{
    WitSocketRequest request;
    WitSocketReply reply;
    WitU32 index, accepted;
    int nonblocking;
    if (flags & ~(long)(SOCK_NONBLOCK | SOCK_CLOEXEC)) {
        return -EINVAL;
    }
    long r = find(fd, &index, &nonblocking);
    if (r < 0) {
        return r;
    }
    const WitU64 deadline = deadline_after(sockets[index].ReceiveTimeout);
    for (;;) {
        const int seen = __wit_pipe_sequence();
        make(&request, WIT_SOCKET_ACCEPT, id_of(index));
        r = simple(&request, &reply);
        if (r != -EAGAIN || nonblocking) {
            break;
        }
        r = await_change(seen, deadline);
        if (r < 0) {
            break;
        }
    }
    if (r < 0) {
        return done(index, r);
    }
    const WitU32 id = reply.Socket;
    const WitSocketAddress peer = reply.Address;
    r = allocate(id, SOCK_STREAM, &accepted);
    if (r < 0) {
        make(&request, WIT_SOCKET_CLOSE, id);
        simple(&request, &reply);
        return done(index, r);
    }
    r = __wit_socket_install(
        accepted, (flags & SOCK_NONBLOCK ? O_NONBLOCK : 0) | (flags & SOCK_CLOEXEC ? O_CLOEXEC : 0));
    if (r < 0) {
        __wit_socket_release(accepted);
        return done(index, r);
    }
    address_out(&peer, address, length);
    return done(index, r);
}

static long name_socket(long fd, struct sockaddr *address, socklen_t *length, int peer)
{
    WitSocketRequest request;
    WitSocketReply reply;
    WitU32 index;
    int nonblocking;
    if (!address || !length) {
        return -EFAULT;
    }
    long r = find(fd, &index, &nonblocking);
    if (r < 0) {
        return r;
    }
    make(&request, WIT_SOCKET_NAME, id_of(index));
    request.Flags = peer ? WIT_SOCKET_PEER : 0;
    r = simple(&request, &reply);
    if (r == 0) {
        address_out(&reply.Address, address, length);
    }
    return done(index, r);
}

static WitU64 total_of(const struct iovec *vectors, int count)
{
    WitU64 total = 0;
    for (int i = 0; i < count; ++i) {
        total += vectors[i].iov_len;
    }
    return total;
}

/* send, sendto, sendmsg and write: a datagram whole, or a stream's bytes in pieces of the window, waiting for room
 * unless the call must not wait; EPIPE raises SIGPIPE unless MSG_NOSIGNAL, as Linux's stream sockets do. */
static long send_vectors(WitU32 index, int nonblocking, const struct iovec *vectors, int count, long flags,
    const struct sockaddr *address, socklen_t length)
{
    WitSocketRequest request;
    WitSocketReply reply;
    WitSocketAddress to;
    if (flags & MSG_OOB) {
        return -EOPNOTSUPP;
    }
    nonblocking = nonblocking || (flags & MSG_DONTWAIT);
    memset(&to, 0, sizeof(to));
    if (address) {
        const long r = address_in(address, length, &to, 0);
        if (r < 0) {
            return r;
        }
    }
    const WitU64 total = total_of(vectors, count);
    if (sockets[index].Type == SOCK_DGRAM) {
        if (total > WINDOW) {
            return -EMSGSIZE;
        }
        make(&request, WIT_SOCKET_SEND, id_of(index));
        request.Length = total;
        request.Address = to;
        const long r = call(&request, vectors, count, 0, 0, 0, 0, &reply);
        return r < 0 ? r : (long)reply.Length;
    }
    if (total == 0) {
        return 0;
    }
    const WitU64 deadline = deadline_after(sockets[index].SendTimeout);
    WitU64 sent = 0;
    long error = 0;
    while (sent < total) {
        const int seen = __wit_pipe_sequence();
        make(&request, WIT_SOCKET_SEND, id_of(index));
        request.Length = total - sent < WINDOW ? total - sent : WINDOW;
        const long r = call(&request, vectors, count, sent, 0, 0, 0, &reply);
        if (r == 0) {
            sent += reply.Length;
            if (reply.Length == request.Length) {
                continue;
            }
        } else if (r != -EAGAIN) {
            error = r;
            break;
        }
        if (nonblocking) {
            error = -EAGAIN;
            break;
        }
        const long waited = await_change(seen, deadline);
        if (waited < 0) {
            error = waited;
            break;
        }
    }
    if (error == -EPIPE && !(flags & MSG_NOSIGNAL)) {
        __wit_tkill((int)__wit_gettid(), SIGPIPE);
    }
    return sent ? (long)sent : error;
}

/* recv, recvfrom, recvmsg and read: the oldest datagram, cut to the room (MSG_TRUNC gives its whole length), or a
 * stream's bytes, all the room asks with MSG_WAITALL; zero at the stream's end; waiting for them unless the call must
 * not wait. */
static long receive_vectors(WitU32 index, int nonblocking, const struct iovec *vectors, int count, long flags,
    struct sockaddr *address, socklen_t *length, int *message_flags)
{
    WitSocketRequest request;
    WitSocketReply reply;
    if (flags & MSG_OOB) {
        return -EINVAL; /* no urgent data */
    }
    nonblocking = nonblocking || (flags & MSG_DONTWAIT);
    const int stream = sockets[index].Type == SOCK_STREAM;
    const WitU64 total = total_of(vectors, count);
    const WitU64 deadline = deadline_after(sockets[index].ReceiveTimeout);
    WitU64 got = 0;
    for (;;) {
        const int seen = __wit_pipe_sequence();
        make(&request, WIT_SOCKET_RECEIVE, id_of(index));
        request.Flags = flags & MSG_PEEK ? WIT_SOCKET_PEEK : 0;
        request.Length = total - got < WINDOW ? total - got : WINDOW;
        long r = call(&request, 0, 0, 0, vectors, count, got, &reply);
        if (r == 0) {
            if (!stream) {
                if (message_flags && reply.Value > reply.Length) {
                    *message_flags |= MSG_TRUNC;
                }
                address_out(&reply.Address, address, length);
                return (flags & MSG_TRUNC) ? (long)reply.Value : (long)reply.Length;
            }
            got += reply.Length;
            if (reply.Length == 0 || !(flags & MSG_WAITALL) || (flags & MSG_PEEK) || got >= total) {
                if (length) {
                    *length = 0; /* a stream's source is its peer, which recvfrom does not write */
                }
                return (long)got;
            }
            continue;
        }
        if (r != -EAGAIN || nonblocking) {
            return got ? (long)got : r;
        }
        r = await_change(seen, deadline);
        if (r < 0) {
            return got ? (long)got : r;
        }
    }
}

static long sendto_socket(
    long fd, const void *buffer, WitU64 bytes, long flags, const struct sockaddr *address, socklen_t length)
{
    WitU32 index;
    int nonblocking;
    long r = find(fd, &index, &nonblocking);
    if (r < 0) {
        return r;
    }
    const struct iovec vector = {(void *)buffer, bytes};
    return done(index, send_vectors(index, nonblocking, &vector, 1, flags, address, address ? length : 0));
}

static long recvfrom_socket(
    long fd, void *buffer, WitU64 bytes, long flags, struct sockaddr *address, socklen_t *length)
{
    WitU32 index;
    int nonblocking;
    long r = find(fd, &index, &nonblocking);
    if (r < 0) {
        return r;
    }
    const struct iovec vector = {buffer, bytes};
    return done(index, receive_vectors(index, nonblocking, &vector, 1, flags, address, length, 0));
}

static long sendmsg_socket(long fd, const struct msghdr *message, long flags)
{
    WitU32 index;
    int nonblocking;
    if (!message) {
        return -EFAULT;
    }
    if (message->msg_iovlen > 1024 || (message->msg_iovlen && !message->msg_iov)) {
        return -EMSGSIZE;
    }
    if (message->msg_controllen) {
        return -EOPNOTSUPP; /* no ancillary data is served */
    }
    long r = find(fd, &index, &nonblocking);
    if (r < 0) {
        return r;
    }
    return done(index,
        send_vectors(index, nonblocking, message->msg_iov, (int)message->msg_iovlen, flags, message->msg_name,
            message->msg_name ? message->msg_namelen : 0));
}

static long recvmsg_socket(long fd, struct msghdr *message, long flags)
{
    WitU32 index;
    int nonblocking;
    if (!message) {
        return -EFAULT;
    }
    if (message->msg_iovlen > 1024 || (message->msg_iovlen && !message->msg_iov)) {
        return -EMSGSIZE;
    }
    long r = find(fd, &index, &nonblocking);
    if (r < 0) {
        return r;
    }
    int message_flags = 0;
    socklen_t length = message->msg_namelen;
    r = receive_vectors(index, nonblocking, message->msg_iov, (int)message->msg_iovlen, flags, message->msg_name,
        message->msg_name ? &length : 0, &message_flags);
    if (r >= 0) {
        message->msg_namelen = message->msg_name ? length : 0;
        message->msg_controllen = 0; /* no ancillary data */
        message->msg_flags = message_flags;
    }
    return done(index, r);
}

static long shutdown_socket(long fd, long how)
{
    WitSocketRequest request;
    WitSocketReply reply;
    WitU32 index;
    int nonblocking;
    if (how < SHUT_RD || how > SHUT_RDWR) {
        return -EINVAL;
    }
    long r = find(fd, &index, &nonblocking);
    if (r < 0) {
        return r;
    }
    make(&request, WIT_SOCKET_SHUTDOWN, id_of(index));
    request.Argument[0] = (WitU64)how;
    return done(index, simple(&request, &reply));
}

/* The options the service keeps: SOL_SOCKET's it serves and TCP_NODELAY. */
static int served(long level, long name)
{
    if (level == SOL_SOCKET) {
        return name == SO_REUSEADDR ||
            name == SO_REUSEPORT ||
            name == SO_ERROR ||
            name == SO_BROADCAST ||
            name == SO_SNDBUF ||
            name == SO_RCVBUF ||
            name == SO_KEEPALIVE ||
            name == SO_ACCEPTCONN;
    }
    return level == IPPROTO_TCP && name == TCP_NODELAY;
}

static long getsockopt_socket(long fd, long level, long name, void *value, socklen_t *length)
{
    WitSocketRequest request;
    WitSocketReply reply;
    WitU32 index;
    int nonblocking;
    if (!value || !length) {
        return -EFAULT;
    }
    long r = find(fd, &index, &nonblocking);
    if (r < 0) {
        return r;
    }
    Socket *s = &sockets[index];
    if (level == SOL_SOCKET && (name == SO_RCVTIMEO || name == SO_SNDTIMEO)) {
        const WitU64 timeout = name == SO_RCVTIMEO ? s->ReceiveTimeout : s->SendTimeout;
        const struct timeval tv = {(time_t)(timeout / 1000000000ULL), (suseconds_t)(timeout % 1000000000ULL / 1000)};
        memcpy(value, &tv, *length < sizeof(tv) ? *length : sizeof(tv));
        *length = *length < sizeof(tv) ? *length : sizeof(tv);
        return done(index, 0);
    }
    if (level == SOL_SOCKET && name == SO_LINGER) {
        memcpy(value, &s->Linger, *length < sizeof(s->Linger) ? *length : sizeof(s->Linger));
        *length = *length < sizeof(s->Linger) ? *length : sizeof(s->Linger);
        return done(index, 0);
    }
    int answer;
    if (level == SOL_SOCKET && (name == SO_TYPE || name == SO_DOMAIN || name == SO_PROTOCOL)) {
        answer = name == SO_TYPE     ? s->Type
            : name == SO_DOMAIN      ? AF_INET
            : s->Type == SOCK_STREAM ? IPPROTO_TCP
                                     : IPPROTO_UDP;
    } else if (served(level, name)) {
        make(&request, WIT_SOCKET_GET_OPTION, id_of(index));
        request.Argument[0] = (WitU64)level;
        request.Argument[1] = (WitU64)name;
        r = simple(&request, &reply);
        if (r < 0) {
            return done(index, r);
        }
        answer = (int)reply.Value;
    } else {
        return done(index, -ENOPROTOOPT);
    }
    if (*length < sizeof(int)) {
        return done(index, -EINVAL);
    }
    memcpy(value, &answer, sizeof(answer));
    *length = sizeof(answer);
    return done(index, 0);
}

static long setsockopt_socket(long fd, long level, long name, const void *value, socklen_t length)
{
    WitSocketRequest request;
    WitSocketReply reply;
    WitU32 index;
    int nonblocking;
    if (!value) {
        return -EFAULT;
    }
    long r = find(fd, &index, &nonblocking);
    if (r < 0) {
        return r;
    }
    Socket *s = &sockets[index];
    if (level == SOL_SOCKET && (name == SO_RCVTIMEO || name == SO_SNDTIMEO)) {
        struct timeval tv;
        if (length < sizeof(tv)) {
            return done(index, -EINVAL);
        }
        memcpy(&tv, value, sizeof(tv));
        if (tv.tv_usec < 0 || tv.tv_usec >= 1000000) {
            return done(index, -EDOM);
        }
        const WitU64 timeout = tv.tv_sec < 0 ? 0 : (WitU64)tv.tv_sec * 1000000000ULL + (WitU64)tv.tv_usec * 1000ULL;
        *(name == SO_RCVTIMEO ? &s->ReceiveTimeout : &s->SendTimeout) = timeout;
        return done(index, 0);
    }
    if (level == SOL_SOCKET && name == SO_LINGER) {
        if (length < sizeof(s->Linger)) {
            return done(index, -EINVAL);
        }
        memcpy(&s->Linger, value, sizeof(s->Linger));
        return done(index, 0);
    }
    if (!served(level, name) || (level == SOL_SOCKET && (name == SO_ERROR || name == SO_ACCEPTCONN))) {
        return done(index, -ENOPROTOOPT);
    }
    if (length < sizeof(int)) {
        return done(index, -EINVAL);
    }
    int set;
    memcpy(&set, value, sizeof(set));
    make(&request, WIT_SOCKET_SET_OPTION, id_of(index));
    request.Argument[0] = (WitU64)level;
    request.Argument[1] = (WitU64)name;
    request.Argument[2] = (WitU64)(long)set;
    return done(index, simple(&request, &reply));
}

/* The socket calls (syscall.c sends them here outside the table lock). */
long __wit_socket_call(long n, long a1, long a2, long a3, long a4, long a5, long a6)
{
    switch (n) {
    case SYS_socket:
        return open_socket(a1, a2, a3);
    case SYS_socketpair:
        return a1 == AF_INET ? -EOPNOTSUPP : -EAFNOSUPPORT; /* AF_UNIX's pairs are unix.c's (W4.1) */
    case SYS_bind:
        return bind_socket(a1, (const struct sockaddr *)a2, (socklen_t)a3);
    case SYS_connect:
        return connect_socket(a1, (const struct sockaddr *)a2, (socklen_t)a3);
    case SYS_listen:
        return listen_socket(a1, a2);
#if defined(SYS_accept)
    case SYS_accept:
        return accept_socket(a1, (struct sockaddr *)a2, (socklen_t *)a3, 0);
#endif
    case SYS_accept4:
        return accept_socket(a1, (struct sockaddr *)a2, (socklen_t *)a3, a4);
    case SYS_getsockname:
        return name_socket(a1, (struct sockaddr *)a2, (socklen_t *)a3, 0);
    case SYS_getpeername:
        return name_socket(a1, (struct sockaddr *)a2, (socklen_t *)a3, 1);
    case SYS_sendto:
        return sendto_socket(a1, (const void *)a2, (WitU64)a3, a4, (const struct sockaddr *)a5, (socklen_t)a6);
    case SYS_recvfrom:
        return recvfrom_socket(a1, (void *)a2, (WitU64)a3, a4, (struct sockaddr *)a5, (socklen_t *)a6);
    case SYS_sendmsg:
        return sendmsg_socket(a1, (const struct msghdr *)a2, a3);
    case SYS_recvmsg:
        return recvmsg_socket(a1, (struct msghdr *)a2, a3);
    case SYS_shutdown:
        return shutdown_socket(a1, a2);
    case SYS_getsockopt:
        return getsockopt_socket(a1, a2, a3, (void *)a4, (socklen_t *)a5);
    case SYS_setsockopt:
        return setsockopt_socket(a1, a2, a3, (const void *)a4, (socklen_t)a5);
    default:
        return -ENOSYS;
    }
}

int __wit_is_socket_call(long n)
{
    switch (n) {
    case SYS_socket:
    case SYS_socketpair:
    case SYS_bind:
    case SYS_connect:
    case SYS_listen:
#if defined(SYS_accept)
    case SYS_accept:
#endif
    case SYS_accept4:
    case SYS_getsockname:
    case SYS_getpeername:
    case SYS_sendto:
    case SYS_recvfrom:
    case SYS_sendmsg:
    case SYS_recvmsg:
    case SYS_shutdown:
    case SYS_getsockopt:
    case SYS_setsockopt:
        return 1;
    default:
        return 0;
    }
}

/* read, readv, write and writev of a socket's descriptor: WIT_NOT_A_SOCKET for any other descriptor. */
long __wit_socket_transfer(long n, long fd, long a2, long a3)
{
    WitU32 index;
    int nonblocking;
    if (__wit_socket_lookup(fd, &index, &nonblocking) <= 0) {
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
        return done(index, send_vectors(index, nonblocking, vectors, count, 0, 0, 0));
    }
    return done(index, receive_vectors(index, nonblocking, vectors, count, 0, 0, 0, 0));
}

/* ioctl of a socket's descriptor: FIONREAD (the bytes a read takes now) and FIONBIO; WIT_NOT_A_SOCKET for any other
 * descriptor. */
long __wit_socket_ioctl(long fd, long command, void *argument)
{
    WitSocketRequest request;
    WitSocketReply reply;
    WitU32 index;
    int nonblocking;
    if (__wit_socket_lookup(fd, &index, &nonblocking) <= 0) {
        return WIT_NOT_A_SOCKET;
    }
    if (!argument && (command == FIONREAD || command == FIONBIO)) {
        return done(index, -EFAULT);
    }
    if (command == FIONREAD) {
        make(&request, WIT_SOCKET_POLL, id_of(index));
        const long r = simple(&request, &reply);
        if (r == 0) {
            *(int *)argument = (int)reply.Length;
        }
        return done(index, r);
    }
    if (command == FIONBIO) {
        return done(index, __wit_socket_nonblocking(fd, *(const int *)argument != 0));
    }
    return done(index, -ENOTTY);
}

/* poll's readiness of a socket (syscall.c, outside the table lock): the service's, as Linux's poll reports it. */
short __wit_socket_poll(WitU32 index, short events)
{
    WitSocketRequest request;
    WitSocketReply reply;
    make(&request, WIT_SOCKET_POLL, id_of(index));
    if (simple(&request, &reply) < 0) {
        return POLLERR;
    }
    short ready = 0;
    if (reply.Value & WIT_SOCKET_READABLE) {
        ready |= (short)(events & (POLLIN | POLLRDNORM));
    }
    if (reply.Value & WIT_SOCKET_WRITABLE) {
        ready |= (short)(events & (POLLOUT | POLLWRNORM));
    }
    if (reply.Value & WIT_SOCKET_ERROR) {
        ready |= POLLERR;
    }
    if (reply.Value & WIT_SOCKET_HANGUP) {
        ready |= POLLHUP;
    }
    return ready;
}

/* /etc/resolv.conf as the lease gives it (files.c reads it): asked of the service before an open of the file, outside
 * the table lock; a nameserver line for each DNS server, nothing without a lease. */
void __wit_resolv_refresh(void)
{
    WitSocketRequest request;
    WitSocketReply reply;
    WitSocketConfiguration configuration;
    unsigned char text[RESOLV_BYTES];
    WitU32 length = 0;
    if (!__wit_process.Network) {
        return;
    }
    make(&request, WIT_SOCKET_CONFIGURATION, 0);
    request.Length = sizeof(configuration);
    const struct iovec out = {&configuration, sizeof(configuration)};
    if (call(&request, 0, 0, 0, &out, 1, 0, &reply) < 0 ||
        configuration.Version != WIT_SOCKET_VERSION ||
        configuration.Size != sizeof(configuration)) {
        return;
    }
    for (WitU32 i = 0; configuration.Leased && i < configuration.DnsCount && i < 4; ++i) {
        const unsigned char *a = configuration.Dns[i];
        char line[40];
        int used = 0;
        memcpy(line, "nameserver ", 11);
        used = 11;
        for (int part = 0; part < 4; ++part) {
            unsigned value = a[part];
            char digits[3];
            int count = 0;
            do {
                digits[count++] = (char)('0' + value % 10);
                value /= 10;
            } while (value);
            while (count) {
                line[used++] = digits[--count];
            }
            line[used++] = part < 3 ? '.' : '\n';
        }
        if (length + (WitU32)used > sizeof(text)) {
            break;
        }
        memcpy(text + length, line, (unsigned long)used);
        length += (WitU32)used;
    }
    __wit_lock(&socket_lock);
    memcpy(resolv, text, length);
    resolv_length = length;
    __wit_unlock(&socket_lock);
}

/* The text of /etc/resolv.conf (files.c, under the table lock). */
WitU32 __wit_resolv_text(unsigned char *buffer, WitU32 capacity)
{
    __wit_lock(&socket_lock);
    const WitU32 length = resolv_length < capacity ? resolv_length : capacity;
    memcpy(buffer, resolv, length);
    __wit_unlock(&socket_lock);
    return length;
}

int __wit_resolv_present(void)
{
    return __wit_process.Network != 0;
}
