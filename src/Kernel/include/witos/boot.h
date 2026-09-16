#ifndef WITOS_BOOT_H
#define WITOS_BOOT_H

#include "types.h"

#define WIT_BOOT_MAGIC 0x574954424F4F5430ULL
#define WIT_BOOT_VERSION 1U
#define WIT_ARCH_X64 1U
#define WIT_BOOT_SERVICES_EXITED 1ULL
#define WIT_MAX_MEMORY_REGIONS 1024U
#define WIT_MEMORY_RESERVED 0U
#define WIT_MEMORY_USABLE 1U

/* M0 handoff uses identity-mapped pointers in a 64-bit address space.
 * Only conventional RAM is usable. Firmware, image and stack stay reserved. */
typedef struct WitMemoryRegion {
    WitU64 Base;
    WitU64 Length;
    WitU32 Kind;
    WitU32 Reserved;
} WitMemoryRegion;

typedef struct WitBootInfo {
    WitU64 Magic;
    WitU32 Version;
    WitU32 Size;
    WitU32 Architecture;
    WitU32 MemoryRegionCount;
    const WitMemoryRegion *MemoryRegions;
    WitU64 Flags;
} WitBootInfo;

_Static_assert(sizeof(void *) == 8, "M0 requires a 64-bit target");
_Static_assert(sizeof(WitMemoryRegion) == 24, "Memory region ABI");
_Static_assert(sizeof(WitBootInfo) == 40, "Boot info ABI");

WIT_NORETURN void wit_kernel_entry(const WitBootInfo *boot);

#endif
