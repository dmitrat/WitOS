#ifndef WITOS_FLAT_H
#define WITOS_FLAT_H
#include "types.h"

/* The flat image of the root task (RFC 0011 section 7.11, plan step K4): a header, at most four page-aligned
 * segments and their bytes; no names, no imports, no relocations, no sections the kernel must understand. The
 * kernel validates the whole image before it maps a page: the magic and version, the header size, the segment
 * count, every segment's alignment, protection, file range within the image and address within the component's
 * fixed region, and that segments do not overlap. The format is the boot package's companion, read by the loader
 * from the boot disk into extents the boot contract names; it is not an application format (phase S brings ELF). */
#define WIT_FLAT_MAGIC 0x3154414C46544957ULL /* "WITFLAT1" */
#define WIT_FLAT_VERSION 1U
#define WIT_FLAT_HEADER_SIZE 64U
#define WIT_FLAT_SEGMENT_SIZE 32U
#define WIT_FLAT_SEGMENTS 4U
#define WIT_FLAT_MAX_BYTES (4U << 20) /* the image file */
#define WIT_FLAT_MAX_SEGMENT_BYTES (1U << 20) /* one segment in memory */

typedef struct WitFlatSegment {
    WitU64 Address; /* virtual, page-aligned, within the component's fixed region */
    WitU64 FileOffset; /* of the bytes in the image file; page-aligned */
    WitU32 FileSize; /* bytes copied; the rest of MemorySize is zero */
    WitU32 MemorySize; /* page multiple, at least FileSize */
    WitU32 Protection; /* WIT_MEMORY_READ, READ|WRITE or READ|EXECUTE */
    WitU32 Reserved;
} WitFlatSegment;

typedef struct WitFlatHeader {
    WitU64 Magic;
    WitU32 Version, Size; /* Size: of the header; the segments follow it */
    WitU64 Entry; /* virtual, inside an executable segment */
    WitU32 SegmentCount, Reserved0;
    WitU64 Reserved[4];
} WitFlatHeader;

WIT_STATIC_ASSERT(sizeof(WitFlatSegment) == WIT_FLAT_SEGMENT_SIZE, "Flat segment ABI");
WIT_STATIC_ASSERT(sizeof(WitFlatHeader) == WIT_FLAT_HEADER_SIZE, "Flat header ABI");
#endif
