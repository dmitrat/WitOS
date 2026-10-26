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
/* Legacy (ABI-1 1.7, H5.2, ADR 0031): a device of the board's legacy resources that its firmware tables name. Address =
 * 0; Identity[0] = the kind. The PC's i8042: region 0 = port 0x60 (data), region 1 = port 0x64 (status and command), each
 * one byte; line 0 = the keyboard's IRQ 1, line 1 = the auxiliary device's IRQ 12, both edges. */
#define WIT_DEVICE_BUS_LEGACY 3U
#define WIT_LEGACY_DEVICE_I8042 1U
/* Region flags. */
#define WIT_DEVICE_REGION_MEMORY 1U /* memory-mapped, mappable through DEVICE_MEMORY */
#define WIT_DEVICE_REGION_PORT 2U /* port I/O: never mappable (UNSUPPORTED), reached through DEVICE_PORT (ABI-1 1.7) */
#define WIT_DEVICE_REGION_PREFETCHABLE 4U
#define WIT_DEVICE_REGION_CONFIG 8U /* the function's configuration space (ECAM page) */
/* Line kinds. */
#define WIT_DEVICE_LINE_LEVEL 1U /* a shared, level-triggered line (PCI INTx) */
#define WIT_DEVICE_LINE_EDGE 2U /* an edge-triggered line of its own (an ISA IRQ, ABI-1 1.7) */

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

/* Port I/O on a held device's port region (ABI-1 1.7, H5.2, ADR 0031). Port(device handle with BIND, WitDevicePortRequest,
 * 32) -> the value read, or 0 for a write: one access of Width bytes (1, 2 or 4, aligned in Offset) at Offset within the
 * region, which must be a port region of the device's descriptor; a written Value must fit the width. The whole request
 * is checked before the port is touched; the architecture without a port space answers UNSUPPORTED. */
#define WIT_DEVICE_PORT_VERSION 1U
#define WIT_DEVICE_PORT_SIZE 32U
#define WIT_DEVICE_PORT_READ 1U
#define WIT_DEVICE_PORT_WRITE 2U

typedef struct WitDevicePortRequest {
    WitU32 Version, Size;
    WitU32 Region, Operation; /* a port region's index, WIT_DEVICE_PORT_READ or WIT_DEVICE_PORT_WRITE */
    WitU32 Width, Offset; /* 1, 2 or 4 bytes at an offset within the region aligned to the width */
    WitU32 Value, Reserved; /* the value written; zero */
} WitDevicePortRequest;

/* The table: this header, then Count descriptors. */
typedef struct WitDeviceTable {
    WitU32 Version, Size; /* of this header */
    WitU32 Count, DescriptorSize;
} WitDeviceTable;

WIT_STATIC_ASSERT(sizeof(WitDeviceRegion) == 24, "Device region ABI");
WIT_STATIC_ASSERT(sizeof(WitDeviceLine) == 8, "Device line ABI");
WIT_STATIC_ASSERT(sizeof(WitDeviceDescriptor) == WIT_DEVICE_DESCRIPTOR_SIZE, "Device descriptor ABI");
WIT_STATIC_ASSERT(sizeof(WitDeviceTable) == WIT_DEVICE_TABLE_SIZE, "Device table ABI");
WIT_STATIC_ASSERT(sizeof(WitDevicePortRequest) == WIT_DEVICE_PORT_SIZE, "Device port request ABI");

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
