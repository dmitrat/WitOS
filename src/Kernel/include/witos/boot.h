#ifndef WITOS_BOOT_H
#define WITOS_BOOT_H

#include "types.h"

#define WIT_BOOT_MAGIC 0x574954424F4F5430ULL
#define WIT_BOOT_VERSION 2U
#define WIT_ARCH_X64 1U
#define WIT_BOOT_SERVICES_EXITED 1ULL
#define WIT_MAX_MEMORY_REGIONS 1024U
#define WIT_MEMORY_RESERVED 0U
#define WIT_MEMORY_USABLE 1U
#define WIT_MAX_IMAGE_SECTIONS 16U
#define WIT_IMAGE_READ 1U
#define WIT_IMAGE_WRITE 2U
#define WIT_IMAGE_EXECUTE 4U

/* Native handoff uses identity-mapped pointers in a 64-bit address space.
 * Only conventional RAM is usable. Firmware, image and stack stay reserved. */
typedef struct WitMemoryRegion {
    WitU64 Base;
    WitU64 Length;
    WitU32 Kind;
    WitU32 Reserved;
} WitMemoryRegion;

typedef struct WitImageSection {
    WitU64 Base;
    WitU64 Length;
    WitU32 Flags;
    WitU32 Reserved;
} WitImageSection;

typedef struct WitBootInfo {
    WitU64 Magic;
    WitU32 Version;
    WitU32 Size;
    WitU32 Architecture;
    WitU32 MemoryRegionCount;
    const WitMemoryRegion *MemoryRegions;
    WitU64 Flags;
    WitU64 ImageBase;
    WitU64 ImageSize;
    WitU32 ImageSectionCount;
    WitU32 Reserved;
    const WitImageSection *ImageSections;
} WitBootInfo;

_Static_assert(sizeof(void *) == 8, "The boot contract requires a 64-bit target");
_Static_assert(sizeof(WitMemoryRegion) == 24, "Memory region ABI");
_Static_assert(sizeof(WitBootInfo) == 72, "Boot info ABI");

WIT_NORETURN void wit_kernel_entry(const WitBootInfo *boot);

#endif
