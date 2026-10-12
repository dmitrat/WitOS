#ifndef WITOS_BLOCK_H
#define WITOS_BLOCK_H
#include "witos/types.h"

/* The block protocol (plan step D3.2, ADR 0025, storage document §7): what a block driver serves on the block channel
 * whose other end moves with its ready answer (witos/driver.h). The client attaches a window first: a duplicate of a
 * memory object of its own, moved in the request's one handle slot with Length its bytes, which the client maps and the
 * driver pins for the device's DMA, so that the device reads and writes the client's bytes in place; a later ATTACH
 * replaces the window. A request names bytes of the device and of the window; the driver serves one request at a time
 * and answers each with a reply. Offsets, lengths and window offsets are multiples of the device's block; a length is
 * positive, at most the reply's MaxTransfer, and lies within the device and the window. The driver checks the whole
 * request before anything reaches the device: a request it cannot serve as it is gets EINVAL, a write to a read-only
 * device EROFS, an unknown operation EOPNOTSUPP, bytes that are no request of this version EPROTO (with Operation 0),
 * and a request the device failed EIO. */
#define WIT_BLOCK_VERSION 1U
#define WIT_BLOCK_ATTACH 1U /* the window, in the one handle slot (MAP, WRITE, TRANSFER) */
#define WIT_BLOCK_INFO 2U /* the device: its bytes, its block, its flags, the transfer limit */
#define WIT_BLOCK_READ 3U /* Length bytes of the device at Offset into the window at Window */
#define WIT_BLOCK_WRITE 4U /* Length bytes of the window at Window to the device at Offset */
#define WIT_BLOCK_FLUSH 5U /* every completed write durable */

/* A write's flag: the write and every write completed before it durable when it completes (a write, then a flush). */
#define WIT_BLOCK_DURABLE 1U

/* The device's flags in the INFO reply. */
#define WIT_BLOCK_READ_ONLY 1U
#define WIT_BLOCK_FLUSHES 2U /* the device has a volatile cache that FLUSH empties */

#define WIT_BLOCK_WINDOW_BYTES (64U * 4096U) /* the largest window: one mapping of an object */

typedef struct WitBlockRequest {
    WitU32 Version, Size; /* WIT_BLOCK_VERSION, the size of this structure */
    WitU32 Operation, Flags;
    WitU64 Offset; /* bytes of the device */
    WitU64 Length; /* bytes */
    WitU64 Window; /* the offset of the bytes in the window */
} WitBlockRequest;

typedef struct WitBlockReply {
    WitU32 Version, Size; /* WIT_BLOCK_VERSION, the size of this structure */
    WitU32 Operation, Error; /* the request's operation; zero or an errno value */
    WitU64 Length; /* INFO: the device's bytes; ATTACH: the window's */
    WitU32 BlockSize; /* INFO: its logical block */
    WitU32 Flags; /* INFO: WIT_BLOCK_READ_ONLY and WIT_BLOCK_FLUSHES */
    WitU32 MaxTransfer; /* INFO: the bytes one request may carry */
    WitU32 Reserved;
} WitBlockReply;

WIT_STATIC_ASSERT(sizeof(WitBlockRequest) == 40, "Block request");
WIT_STATIC_ASSERT(sizeof(WitBlockReply) == 40, "Block reply");
#endif
