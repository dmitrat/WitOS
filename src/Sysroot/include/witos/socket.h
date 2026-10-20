#ifndef WITOS_SOCKET_H
#define WITOS_SOCKET_H
#include "witos/types.h"

/* The network service's protocol (plan step W1.2a, ADR 0025; W0's decisions 4 and 6). The network service
 * /system/Network runs the managed TCP/IP stack over the adapters the device manager hands it and serves sockets to its
 * clients, which hold a duplicate of its clients' endpoint (SEND alone). A client connects once: it sends
 * WIT_SOCKET_CONNECT on that endpoint with three handles — the end of a session channel it made, a duplicate of a memory
 * object of its own, the window (MAP, WRITE, TRANSFER), and the end of an events channel it made — and from then on sends
 * requests on the session and receives each reply there, one at a time. No request waits: one that would answers
 * EAGAIN, and the service sends a WitSocketEvent on the events channel when a socket's readiness changes (a datagram or a
 * connection came, room to send, an error), from which the libc builds blocking calls, poll and epoll (W2.1); a full
 * events queue drops the event, since the events it holds wake the client already, and a client tries its request again
 * after any event. Data, datagrams and the configuration travel in the window; an address travels in the request and the
 * reply; a request's reserved words are zero. An error is an errno value. Every socket belongs to the session that opened
 * it (another session's is EBADF) and ends with it. The device manager starts the service with the grant of a channel to
 * itself, moves each adapter's packet channel (witos/packet.h) to it in a WitNetworkAdapter message, then sends one with
 * WIT_SOCKET_ADAPTERS_END and no handle, and the service answers with a WitServiceStatus (witos/manager.h) whose one
 * handle is the clients' endpoint (SEND, DUPLICATE, TRANSFER). Nothing of the protocol is an application API:
 * applications see sockets through the libc. */
#define WIT_SOCKET_VERSION 1U
#define WIT_SOCKET_PATH "/system/Network"

/* Operations. */
#define WIT_SOCKET_CONNECT 1U /* on the clients' endpoint: the session's end, the window and the events' end */
#define WIT_SOCKET_OPEN 2U /* Argument[0] the family, [1] the type, [2] the protocol: the reply's Socket */
#define WIT_SOCKET_BIND 3U /* Socket, Address */
/* CONNECT_TO: Socket, Address. A datagram socket takes its peer at once, and AF_UNSPEC dissolves its association; a
 * stream socket answers EINPROGRESS and becomes writable once its connection is established, or reports the error
 * through SO_ERROR, as a nonblocking connect does on Linux. */
#define WIT_SOCKET_CONNECT_TO 4U
#define WIT_SOCKET_LISTEN 5U /* Socket, Argument[0] the backlog (W1.2b) */
#define WIT_SOCKET_ACCEPT 6U /* Socket: the reply's Socket, the connection, and Address, its peer (W1.2b) */
/* SEND: Socket, Length bytes of the window at Window. A datagram goes to Address, or to the peer for AF_UNSPEC; a stream
 * takes as many bytes as its send buffer holds and ignores Address. The reply's Length: the bytes sent or taken. */
#define WIT_SOCKET_SEND 7U
/* RECEIVE: Socket, at most Length bytes into the window at Window, Flags WIT_SOCKET_PEEK. The reply's Length: the bytes
 * written, zero at a stream's end; Value: a datagram's whole length; Address: its source, or a stream's peer. */
#define WIT_SOCKET_RECEIVE 8U
#define WIT_SOCKET_SHUTDOWN 9U /* Socket, Argument[0] SHUT_RD, SHUT_WR or SHUT_RDWR (W1.2b) */
#define WIT_SOCKET_CLOSE 10U /* Socket */
#define WIT_SOCKET_NAME 11U /* Socket, Flags WIT_SOCKET_PEER for the peer's: the reply's Address */
#define WIT_SOCKET_POLL 12U /* Socket: the reply's Value, the readiness (WIT_SOCKET_READABLE...) */
#define WIT_SOCKET_GET_OPTION 13U /* Socket, Argument[0] the level, Argument[1] the name: the reply's Value */
#define WIT_SOCKET_SET_OPTION 14U /* Socket, Argument[0] the level, Argument[1] the name, Argument[2] the value */
#define WIT_SOCKET_CONFIGURATION 15U /* the interface and its lease, a WitSocketConfiguration at Window */

#define WIT_SOCKET_PEEK 0x2U /* RECEIVE: the datagram or the bytes stay (MSG_PEEK) */
#define WIT_SOCKET_PEER 0x1U /* NAME: the peer's address (getpeername) */

/* Readiness, poll's bits. */
#define WIT_SOCKET_READABLE 0x1U /* POLLIN */
#define WIT_SOCKET_WRITABLE 0x4U /* POLLOUT */
#define WIT_SOCKET_ERROR 0x8U /* POLLERR */
#define WIT_SOCKET_HANGUP 0x10U /* POLLHUP */

#define WIT_SOCKET_WINDOW_BYTES (16U * 4096U) /* the largest window */
#define WIT_SOCKET_ADAPTER 1U /* WitNetworkAdapter's Operation, on the grant channel */
#define WIT_SOCKET_ADAPTERS_END 0x1U /* WitNetworkAdapter's Flags: no handle, no adapter follows */

/* An address of the socket protocol: AF_INET's port and four bytes (AF_INET6's sixteen from a later step). */
typedef struct WitSocketAddress {
    WitU16 Family; /* AF_INET (2), or AF_UNSPEC (0) for none */
    WitU16 Port; /* in host order */
    WitU32 Reserved;
    WitU8 Address[16]; /* AF_INET: the first four, in network order */
} WitSocketAddress;

typedef struct WitSocketRequest {
    WitU32 Version, Size; /* WIT_SOCKET_VERSION, the size of this structure */
    WitU32 Operation, Flags;
    WitU32 Socket, Reserved;
    WitU64 Length; /* SEND: the bytes; RECEIVE: at most the bytes; CONFIGURATION: the room */
    WitU64 Window; /* the offset of the bytes in the window */
    WitU64 Argument[3];
    WitSocketAddress Address;
} WitSocketRequest;

typedef struct WitSocketReply {
    WitU32 Version, Size; /* WIT_SOCKET_VERSION, the size of this structure */
    WitU32 Operation, Error; /* the request's operation; zero or an errno value */
    WitU32 Socket, Reserved; /* OPEN and ACCEPT: the new socket */
    WitU64 Length; /* SEND: the bytes sent; RECEIVE: the bytes written */
    WitU64 Value; /* RECEIVE: the datagram's length; POLL: the readiness; GET_OPTION: the value */
    WitSocketAddress Address; /* RECEIVE: the source; NAME and ACCEPT: the address */
} WitSocketReply;

/* On the events channel: a socket's readiness changed. */
typedef struct WitSocketEvent {
    WitU32 Version, Size; /* WIT_SOCKET_VERSION, the size of this structure */
    WitU32 Socket, Readiness;
} WitSocketEvent;

/* CONFIGURATION's answer in the window. */
typedef struct WitSocketConfiguration {
    WitU32 Version, Size; /* WIT_SOCKET_VERSION, the size of this structure */
    WitU8 Address[4], Mask[4], Gateway[4]; /* the interface's, zeros while it has no lease */
    WitU32 DnsCount; /* the DNS servers that follow, at most four */
    WitU8 Dns[4][4];
    WitU64 Mac; /* the adapter's address, its first byte the lowest */
    WitU32 Mtu, Leased; /* Leased: one once DHCP configured the interface */
} WitSocketConfiguration;

/* On the grant channel, from the device manager: an adapter's packet channel in the one handle slot, then one with
 * WIT_SOCKET_ADAPTERS_END and no handle. */
typedef struct WitNetworkAdapter {
    WitU32 Version, Size; /* WIT_SOCKET_VERSION, the size of this structure */
    WitU32 Operation, Flags; /* WIT_SOCKET_ADAPTER; WIT_SOCKET_ADAPTERS_END */
    char Device[48]; /* the adapter's device identity, zero-terminated */
} WitNetworkAdapter;

WIT_STATIC_ASSERT(sizeof(WitSocketAddress) == 24, "Socket address");
WIT_STATIC_ASSERT(sizeof(WitSocketRequest) == 88, "Socket request");
WIT_STATIC_ASSERT(sizeof(WitSocketReply) == 64, "Socket reply");
WIT_STATIC_ASSERT(sizeof(WitSocketEvent) == 16, "Socket event");
WIT_STATIC_ASSERT(sizeof(WitSocketConfiguration) == 56, "Socket configuration");
WIT_STATIC_ASSERT(sizeof(WitNetworkAdapter) == 64, "Network adapter");
#endif
