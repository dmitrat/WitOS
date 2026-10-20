#ifndef WITOS_PACKET_H
#define WITOS_PACKET_H
#include "witos/types.h"

/* The packet protocol (plan step W1.1, ADR 0025; RFC 0007's NetworkQueueDevice): what the driver of a network adapter —
 * the virtio network device, /system/Drivers/VirtioNet — serves on the channel whose other end moves with its ready
 * answer (witos/driver.h), to the one client that takes it: the network service (W1.2), the device manager's probe until
 * then. The client attaches a window first (ATTACH: a duplicate of a memory object of its own in the one handle slot,
 * Length its bytes), which the driver maps and copies frames through. INFO answers the adapter's address, its MTU and
 * whether its link is up. SEND transmits Count frames packed at Window, Length bytes in all, and answers once the device
 * took them. RECEIVE answers with frames the device received, packed into the window at Window: at least one and as many
 * whole frames as Length holds, once there are any, the driver holding the one RECEIVE until then unless NONBLOCK asks
 * for an answer at once. A packed frame is a WitPacketFrame, whose Bytes are the frame's, followed by the frame; the next
 * one starts at the next multiple of 8. A frame is Ethernet II without its check sequence, of 14 to 14 + MTU bytes. The
 * driver keeps at most WIT_PACKET_QUEUE received frames for a client that reads slowly and drops the newest beyond them,
 * as a full receive ring does. One client, one request of each kind at a time; a SEND is served while a RECEIVE waits.
 * Errors are errno values: EINVAL for a request the driver cannot serve as it is, EMSGSIZE for a frame shorter than its
 * header or longer than the MTU allows and for a RECEIVE whose bytes cannot hold the oldest frame waiting, EOPNOTSUPP for an unknown operation, EPROTO for bytes that are no request of this
 * version, EBUSY for a second RECEIVE while one waits, EIO when the device failed. Nothing of the protocol is an
 * application API: applications see sockets. */
#define WIT_PACKET_VERSION 1U
#define WIT_PACKET_ATTACH 1U /* the window, in the one handle slot (MAP, WRITE, TRANSFER); Length its bytes */
#define WIT_PACKET_INFO 2U /* the adapter's address, MTU and link */
#define WIT_PACKET_SEND 3U /* Count frames packed at Window, Length bytes in all */
#define WIT_PACKET_RECEIVE 4U /* whole frames the device received, packed into at most Length bytes at Window */

#define WIT_PACKET_NONBLOCK 1U /* RECEIVE: answer at once, with no frame when none came */

#define WIT_PACKET_LINK_UP 1U /* INFO's Link: the adapter reports its link up, or reports no link state */

#define WIT_PACKET_WINDOW_BYTES (16U * 4096U) /* the largest window */
#define WIT_PACKET_QUEUE 256U /* the received frames the driver keeps for a client that reads slowly */
#define WIT_PACKET_HEADER_BYTES 14U /* an Ethernet II header: two addresses and the type */

typedef struct WitPacketRequest {
    WitU32 Version, Size; /* WIT_PACKET_VERSION, the size of this structure */
    WitU32 Operation, Flags;
    WitU64 Length; /* ATTACH: the window's bytes; SEND: the packed frames' bytes; RECEIVE: the bytes they may fill */
    WitU64 Window; /* the offset of the packed frames in the window, a multiple of 8 */
    WitU32 Count; /* SEND: the frames */
    WitU32 Reserved;
} WitPacketRequest;

typedef struct WitPacketReply {
    WitU32 Version, Size; /* WIT_PACKET_VERSION, the size of this structure */
    WitU32 Operation, Error; /* the request's operation; zero or an errno value */
    WitU64 Length; /* RECEIVE: the packed frames' bytes; SEND: the bytes sent; ATTACH: the window's */
    WitU32 Count; /* SEND and RECEIVE: the frames */
    WitU32 Link; /* INFO: WIT_PACKET_LINK_UP or zero */
    WitU8 Address[6]; /* INFO: the adapter's MAC address */
    WitU16 Mtu; /* INFO: the largest payload of a frame */
    WitU32 Reserved[2];
} WitPacketReply;

typedef struct WitPacketFrame {
    WitU32 Bytes; /* the frame's bytes, which follow */
    WitU32 Reserved;
} WitPacketFrame;

WIT_STATIC_ASSERT(sizeof(WitPacketRequest) == 40, "Packet request");
WIT_STATIC_ASSERT(sizeof(WitPacketReply) == 48, "Packet reply");
WIT_STATIC_ASSERT(sizeof(WitPacketFrame) == 8, "Packet frame header");
#endif
