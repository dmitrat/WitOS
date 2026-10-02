#ifndef WITOS_MEMORY_INFO_H
#define WITOS_MEMORY_INFO_H
#include "types.h"

#define WIT_MEMORY_INFO_VERSION 2U
#define WIT_MEMORY_INFO_SIZE 112U

/* A serialized allocator snapshot, not a promise that a later commit succeeds.
 * All counts are bytes. Owned includes fixed mappings and private page tables;
 * DynamicCommitted includes committed no-access leaves. Physical totals cover
 * the kernel's eligible allocator RAM, excluding reserved firmware memory. */
typedef struct WitUserMemoryInfo {
    WitU32 Version;
    WitU32 Size;
    WitU32 PageSize;
    WitU32 ProcessorCount; /* Online processors usable by this kernel. */
    WitU64 PhysicalTotalBytes;
    WitU64 PhysicalAvailableBytes;
    WitU64 OwnedLimitBytes;
    WitU64 OwnedBytes;
    WitU64 VirtualBase;
    WitU64 VirtualBytes;
    WitU64 ReservedBytes;
    WitU64 DynamicCommittedBytes;
    WitU64 PrivatePageTableBytes;
    WitU32 ReservationCount;
    WitU32 ReservationCapacity;
    /* Separate near-code arena; VirtualBase/VirtualBytes remain the data arena.
     * ReservedBytes and DynamicCommittedBytes cover both, counting backing once. */
    WitU64 CodeVirtualBase;
    WitU64 CodeVirtualBytes;
} WitUserMemoryInfo;

WIT_STATIC_ASSERT(sizeof(WitUserMemoryInfo) == WIT_MEMORY_INFO_SIZE, "Memory information ABI");
#endif
