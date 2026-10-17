#ifndef WITOS_BOOT_H
#define WITOS_BOOT_H

#include "types.h"

#define WIT_BOOT_MAGIC 0x574954424F4F5430ULL
/* v7 (plan step C2.1): the firmware's framebuffer joined the contract. */
#define WIT_BOOT_VERSION 7U
#define WIT_BOOT_SEED_BYTES 32U
#define WIT_ARCH_X64 1U
#define WIT_ARCH_ARM64 2U
#define WIT_ARCH_RISCV64 3U /* plan step V1 */
#define WIT_BOOT_SERVICES_EXITED 1ULL
#define WIT_MAX_MEMORY_REGIONS 1024U
#define WIT_MEMORY_RESERVED 0U
#define WIT_MEMORY_USABLE 1U
#define WIT_MAX_IMAGE_SECTIONS 16U
#define WIT_MAX_STORAGE_EXTENTS 128U
#define WIT_MAX_ROOT_EXTENTS 4U /* the root task image: at most 4 MiB in extents of a MiB */
#define WIT_IMAGE_READ 1U
#define WIT_IMAGE_WRITE 2U
#define WIT_IMAGE_EXECUTE 4U
/* Pixel formats of the firmware's framebuffer (C2.1), 32 bits a pixel: the bytes in memory are blue, green, red, then
 * one unused (UEFI's PixelBlueGreenRedReserved8BitPerColor), or red, green, blue, unused. */
#define WIT_PIXEL_BGRX 1U
#define WIT_PIXEL_RGBX 2U

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

typedef struct WitBootStorageExtent {
    WitU64 Base, Length;
} WitBootStorageExtent;

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
    /* Boot adapter transfers a private writable seed buffer in its image.
     * Kernel initialization consumes/wipes it; it is never exposed to users. */
    WitU8 *EntropySeed;
    WitU32 EntropySize;
    WitU32 EntropyReserved;
    const WitBootStorageExtent *StorageExtents;
    WitU64 StorageBytes;
    WitU32 StorageExtentCount, StorageReserved;
    /* Firmware tables the configuration table published, as physical addresses, zero when absent: the ACPI 2.0
     * RSDP and the flattened device tree. The common kernel never reads them; the platform enumerates its devices
     * from whichever it understands (plan step K3.1). */
    WitU64 AcpiRsdp;
    WitU64 DeviceTree;
    /* The root task's flat image (RFC 0011 section 7.11, plan step K4), read like the package into extents the
     * kernel maps; absent when the boot volume has no image (zero count and bytes). */
    const WitBootStorageExtent *RootTaskExtents;
    WitU64 RootTaskBytes;
    WitU32 RootTaskExtentCount, RootTaskReserved;
    /* The firmware's framebuffer (plan step C2.1, v7): the one display the firmware set up through its graphics
     * output protocol, as physical memory the kernel never reads — the platform publishes it as a device descriptor
     * (witos/device.h, the firmware bus) and a display service maps it through DEVICE_MEMORY. Zero bytes when the
     * firmware has none; otherwise a page-aligned range of whole pages, a geometry in pixels whose stride (pixels
     * per scan line) is at least the width, and a format of WIT_PIXEL_*. */
    WitU64 FramebufferBase;
    WitU64 FramebufferBytes;
    WitU32 FramebufferWidth, FramebufferHeight;
    WitU32 FramebufferStride, FramebufferFormat;
} WitBootInfo;

_Static_assert(sizeof(void *) == 8, "The boot contract requires a 64-bit target");
_Static_assert(sizeof(WitMemoryRegion) == 24, "Memory region ABI");
_Static_assert(sizeof(WitBootInfo) == 184, "Boot info ABI");

WIT_NORETURN void wit_kernel_entry(const WitBootInfo *boot);

#endif
