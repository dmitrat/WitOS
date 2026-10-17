#ifndef WITOS_DEVICE_H
#define WITOS_DEVICE_H
#include "boot.h"
#include "types.h"

/* Device descriptors (RFC 0011 section 7.7, RFC 0007 sections 13 and 14, plan step K3.1). The platform enumerates
 * the board's devices once at boot: for each, an opaque identity, the memory regions it exposes and the interrupt
 * lines it can raise. The kernel publishes the table unchanged as a read-only memory object and knows nothing of
 * what a device is; a descriptor says that a device exists, the handle DEVICE_ACQUIRE mints is the authority over
 * it. The table is ABI: every field is little-endian and the structures are fixed-size so a reader needs no parser
 * beyond its header. */
#define WIT_DEVICE_TABLE_VERSION 1U
#define WIT_DEVICE_TABLE_SIZE 16U
#define WIT_DEVICE_DESCRIPTOR_SIZE 232U
#define WIT_DEVICE_REGIONS 7U /* regions one descriptor lists: a PCI function's configuration page and its six BARs */
#define WIT_DEVICE_LINES 2U /* interrupt lines one descriptor lists */

/* Bus of a descriptor: how Address and Identity read. PCI: Address = bus << 16 | device << 8 | function;
 * Identity[0] = vendor | device << 16, Identity[1] = class << 24 | subclass << 16 | interface << 8 | revision,
 * Identity[2] = subsystem vendor | subsystem id << 16, Identity[3] = header type. */
#define WIT_DEVICE_BUS_PCI 1U
/* Firmware (plan step C2.1): a device the firmware set up and the boot contract describes, not a function of a bus.
 * Address = 0; Identity[0] = the kind, the other words by kind. The framebuffer: Identity[1] = width | height << 16
 * in pixels, Identity[2] = the stride in pixels, Identity[3] = the pixel format (WIT_PIXEL_*), region 0 = its memory,
 * no lines. */
#define WIT_DEVICE_BUS_FIRMWARE 2U
#define WIT_FIRMWARE_DEVICE_FRAMEBUFFER 1U
/* Region flags. */
#define WIT_DEVICE_REGION_MEMORY 1U /* memory-mapped, mappable through DEVICE_MEMORY */
#define WIT_DEVICE_REGION_PORT 2U /* port I/O: described, never mappable (UNSUPPORTED) */
#define WIT_DEVICE_REGION_PREFETCHABLE 4U
#define WIT_DEVICE_REGION_CONFIG 8U /* the function's configuration space (ECAM page) */
/* Line kinds. */
#define WIT_DEVICE_LINE_LEVEL 1U /* a shared, level-triggered line (PCI INTx) */

typedef struct WitDeviceRegion {
    WitU64 Base; /* physical */
    WitU64 Size; /* bytes; a multiple of the page size for memory regions */
    WitU32 Flags, Reserved;
} WitDeviceRegion;

typedef struct WitDeviceLine {
    WitU32 Kind;
    WitU32 Line; /* the platform's line number: a PIC input on q35, a GIC INTID on virt */
} WitDeviceLine;

typedef struct WitDeviceDescriptor {
    WitU32 Bus, Address;
    WitU32 Identity[4];
    WitU32 RegionCount, LineCount;
    WitDeviceRegion Regions[WIT_DEVICE_REGIONS];
    WitDeviceLine Lines[WIT_DEVICE_LINES];
    WitU32 Reserved[4];
} WitDeviceDescriptor;

/* The table: this header, then Count descriptors. */
typedef struct WitDeviceTable {
    WitU32 Version, Size; /* of this header */
    WitU32 Count, DescriptorSize;
} WitDeviceTable;

WIT_STATIC_ASSERT(sizeof(WitDeviceRegion) == 24, "Device region ABI");
WIT_STATIC_ASSERT(sizeof(WitDeviceLine) == 8, "Device line ABI");
WIT_STATIC_ASSERT(sizeof(WitDeviceDescriptor) == WIT_DEVICE_DESCRIPTOR_SIZE, "Device descriptor ABI");
WIT_STATIC_ASSERT(sizeof(WitDeviceTable) == WIT_DEVICE_TABLE_SIZE, "Device table ABI");

/* The descriptor of the firmware's framebuffer from the boot contract (C2.1), for a platform's enumeration: 1 when the
 * boot carries one that the descriptor's words can hold and the descriptor was filled, 0 otherwise. The common kernel
 * never calls it; each platform appends the descriptor after the devices of its bus. */
static inline int wit_device_describe_framebuffer(const WitBootInfo *boot, WitDeviceDescriptor *d)
{
    if (boot->FramebufferBytes == 0 || boot->FramebufferWidth > 0xFFFFU || boot->FramebufferHeight > 0xFFFFU) {
        return 0;
    }
    for (WitU32 i = 0; i < sizeof(*d); ++i) {
        ((WitU8 *)d)[i] = 0;
    }
    d->Bus = WIT_DEVICE_BUS_FIRMWARE;
    d->Address = 0;
    d->Identity[0] = WIT_FIRMWARE_DEVICE_FRAMEBUFFER;
    d->Identity[1] = boot->FramebufferWidth | (boot->FramebufferHeight << 16);
    d->Identity[2] = boot->FramebufferStride;
    d->Identity[3] = boot->FramebufferFormat;
    d->RegionCount = 1;
    d->Regions[0].Base = boot->FramebufferBase;
    d->Regions[0].Size = boot->FramebufferBytes;
    d->Regions[0].Flags = WIT_DEVICE_REGION_MEMORY;
    return 1;
}
#endif
