#ifndef WITOS_SERIAL_H
#define WITOS_SERIAL_H
#include "witos/types.h"

/* The serial protocol (plan step N4.3a, ADR 0025): what the driver of a byte-stream device — the virtio console,
 * /system/Drivers/VirtioConsole — serves on the channel whose other end moves with its ready answer (witos/driver.h).
 * The client attaches a window first, as a block client does (witos/block.h): a duplicate of a memory object of its
 * own in the request's one handle slot, Length its bytes, which the driver maps and copies through, since a console
 * carries bytes by the hundred and pins nothing of the client's. WRITE sends Length bytes of the window at Window to
 * the device and answers when the device took them. READ answers with the bytes the device received, into the window
 * at Window, at least one and at most Length, once there are any: the driver holds the one READ until then, unless
 * NONBLOCK asks for an answer at once with what there is. SIZE answers the device's columns and rows, zero when the
 * device reports none. One client, one request of each kind at a time; a WRITE is served while a READ waits. Errors
 * are errno values: EINVAL for a request the driver cannot serve as it is, EOPNOTSUPP for an unknown operation,
 * EPROTO for bytes that are no request of this version, EBUSY for a second READ while one waits, EIO when the device
 * failed. Nothing of the protocol is an application API. */
#define WIT_SERIAL_VERSION 1U
#define WIT_SERIAL_ATTACH 1U /* the window, in the one handle slot (MAP, WRITE, TRANSFER); Length its bytes */
#define WIT_SERIAL_WRITE 2U /* Length bytes of the window at Window to the device */
#define WIT_SERIAL_READ 3U /* up to Length bytes the device received into the window at Window */
#define WIT_SERIAL_SIZE 4U /* the device's columns and rows */

#define WIT_SERIAL_NONBLOCK 1U /* READ: answer at once, with no bytes when none came */

#define WIT_SERIAL_WINDOW_BYTES (16U * 4096U) /* the largest window */

typedef struct WitSerialRequest {
    WitU32 Version, Size; /* WIT_SERIAL_VERSION, the size of this structure */
    WitU32 Operation, Flags;
    WitU64 Length; /* bytes to write or at most to read; ATTACH: the window's bytes */
    WitU64 Window; /* the offset of the bytes in the window */
} WitSerialRequest;

typedef struct WitSerialReply {
    WitU32 Version, Size; /* WIT_SERIAL_VERSION, the size of this structure */
    WitU32 Operation, Error; /* the request's operation; zero or an errno value */
    WitU64 Length; /* bytes written or read; ATTACH: the window's */
    WitU32 Columns, Rows; /* SIZE: the device's, or zeros */
} WitSerialReply;

WIT_STATIC_ASSERT(sizeof(WitSerialRequest) == 32, "Serial request");
WIT_STATIC_ASSERT(sizeof(WitSerialReply) == 32, "Serial reply");
#endif
