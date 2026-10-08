#ifndef WITOS_DMA_H
#define WITOS_DMA_H
#include "types.h"

/* DMA pinning (RFC 0011 section 7.7, plan step K3.2). Pin(WitDmaPinRequest, 56, 0) -> pin handle: the holder of a
 * device (a device handle with BIND) pins a page-aligned window of a memory object it holds with MAP for that
 * device; the object's pages are eagerly backed and never move, so the pin is a reference that keeps the object
 * alive and the physical ranges of the window, written to Ranges in order with every entry after the last zeroed.
 * Without an IOMMU the ranges are physical addresses and the device holder is the trust boundary: nothing stops a
 * device programmed with another address (RFC 0004 section 57). */
#define WIT_DMA_PIN_VERSION 1U
#define WIT_DMA_PIN_SIZE 56U
#define WIT_DMA_RANGE_SIZE 16U
#define WIT_DMA_RANGES_MAX 64U /* RangeCapacity at most: one range per page of the largest object */

typedef struct WitDmaRange {
    WitU64 Address; /* physical, or a device address once an IOMMU maps it */
    WitU64 Size; /* bytes; zero ends the list */
} WitDmaRange;

typedef struct WitDmaPinRequest {
    WitU32 Version, Size;
    WitU64 Device; /* device handle with BIND */
    WitU64 Object; /* memory object handle with MAP: an anonymous object */
    WitU64 Offset; /* page-aligned window of the object */
    WitU64 Bytes;
    WitU64 Ranges; /* WitDmaRange[RangeCapacity], written whole */
    WitU32 RangeCapacity, Flags;
} WitDmaPinRequest;

WIT_STATIC_ASSERT(sizeof(WitDmaRange) == WIT_DMA_RANGE_SIZE, "DMA range ABI");
WIT_STATIC_ASSERT(sizeof(WitDmaPinRequest) == WIT_DMA_PIN_SIZE, "DMA pin request ABI");
#endif
