#ifndef WITOS_MESSAGE_INTERRUPT_H
#define WITOS_MESSAGE_INTERRUPT_H
#include "types.h"

/* Message interrupts (RFC 0011 section 7.7, ADR 0029, plan step H1.2b). BindMessage(WitMessageBindRequest, 48, 0) ->
 * interrupt handle with QUERY, DUPLICATE and TRANSFER: the holder of a device (a device handle with BIND) binds a
 * message target the board allocates to an event of its own (SIGNAL), and the kernel writes the target's Address and
 * Data into the request. The driver writes them into its function's MSI capability or an MSI-X table entry through the
 * regions it maps; the kernel programs no device. Each message the device writes sets the event; a message is an edge,
 * nothing stays masked, and INTERRUPT_ACK of such a binding succeeds and does nothing. The binding holds the device and
 * the event and frees its target with its last handle. Without an IOMMU the device holder is the trust boundary, as for
 * DMA. A board without message interrupts answers UNSUPPORTED, and QUERY's WIT_ABI_FEATURE_MESSAGE_INTERRUPTS is
 * clear there. */
#define WIT_MESSAGE_BIND_VERSION 1U
#define WIT_MESSAGE_BIND_SIZE 48U

typedef struct WitMessageBindRequest {
    WitU32 Version, Size;
    WitU64 Device; /* device handle with BIND */
    WitU64 Event; /* event handle with SIGNAL */
    WitU32 Flags, Reserved; /* zero */
    WitU64 Address; /* written by the kernel: where the device writes the message */
    WitU32 Data, Reserved1; /* written by the kernel: what it writes; Reserved1 zero */
} WitMessageBindRequest;

WIT_STATIC_ASSERT(sizeof(WitMessageBindRequest) == WIT_MESSAGE_BIND_SIZE, "Message bind request ABI");
#endif
