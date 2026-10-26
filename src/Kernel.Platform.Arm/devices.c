#include "witos/arch.h"
#include "witos/boot.h"
#include "witos/device.h"
#include "witos/platform.h"
#include "witos/processor.h"
#include "arm.h"

/* Device enumeration (plan step K3.1): the PCI functions behind the generic ECAM host bridge the flattened device tree
 * describes (compatible "pci-host-ecam-generic": reg, bus-range, interrupt-map), read
 * through the ECAM pages mapped on demand. The firmware assigned the BARs; the INTx lines come from the device
 * tree's interrupt map, GIC SPI numbers as INTIDs. Without a device tree (the firmware publishes one only when it
 * runs without ACPI) there is no bus and the table is empty. Nothing here knows a device class. */

#define BRIDGE_DEPTH 4U

/* What the device tree says about the ECAM host bridge. */
typedef struct EcamBridge {
    WitU64 Base, Size;
    WitU32 BusFirst, BusLast;
    const WitU8 *InterruptMap;
    WitU32 InterruptMapBytes;
    WitU32 MapMask[4]; /* interrupt-map-mask: three address cells and the pin */
    int Found;
} EcamBridge;

/* An interrupt controller the map's entries name by phandle: its address and interrupt cells size each entry. */
typedef struct InterruptController {
    WitU32 Phandle, AddressCells, InterruptCells, Live;
} InterruptController;

#define CONTROLLER_CAPACITY 4U
static InterruptController controllers[CONTROLLER_CAPACITY];

/* Walks the tree once; the first node of the root compatible with the generic ECAM bridge wins. */
static void read_tree(const WitBootInfo *boot, EcamBridge *bridge)
{
    WitArmTree tree;
    WitArmTreeItem item;
    EcamBridge candidate = {0};
    int compatible_seen = 0;
    InterruptController node[WIT_ARM_TREE_DEPTH + 1] = {{0}}; /* the controller properties of the open nodes */
    if (!wit_arm_tree_open(boot, &tree)) {
        return;
    }
    while (wit_arm_tree_next(&tree, &item)) {
        const WitU32 depth = item.Depth;
        if (item.Kind == WIT_ARM_TREE_BEGIN) {
            if (depth <= WIT_ARM_TREE_DEPTH) {
                node[depth] = (InterruptController){0};
            }
            if (depth == 2) {
                compatible_seen = 0;
                candidate = (EcamBridge){0};
            }
        } else if (item.Kind == WIT_ARM_TREE_END) {
            if (depth == 2 && compatible_seen && candidate.Size && !bridge->Found) {
                *bridge = candidate;
                bridge->Found = 1;
            }
            if (depth <= WIT_ARM_TREE_DEPTH && node[depth].Live) {
                for (WitU32 i = 0; i < CONTROLLER_CAPACITY; ++i) {
                    if (!controllers[i].Live) {
                        controllers[i] = node[depth];
                        break;
                    }
                }
            }
        } else if (item.Kind == WIT_ARM_TREE_PROPERTY) {
            const char *name = item.Name;
            const WitU8 *value = item.Value;
            const WitU32 length = item.Length;
            if (depth <= WIT_ARM_TREE_DEPTH) {
                if (wit_arm_same(name, "phandle") && length >= 4) {
                    node[depth].Phandle = wit_arm_be32(value);
                } else if (wit_arm_same(name, "#address-cells") && length >= 4) {
                    node[depth].AddressCells = wit_arm_be32(value);
                } else if (wit_arm_same(name, "#interrupt-cells") && length >= 4) {
                    node[depth].InterruptCells = wit_arm_be32(value);
                } else if (wit_arm_same(name, "interrupt-controller")) {
                    node[depth].Live = 1;
                }
            }
            if (depth != 2 || bridge->Found) {
                continue;
            }
            if (wit_arm_same(name, "compatible")) {
                compatible_seen |= wit_arm_tree_names(&item, "pci-host-ecam-generic");
            } else if (wit_arm_same(name, "reg")) {
                wit_arm_tree_reg(&tree, &item, 0, &candidate.Base, &candidate.Size);
            } else if (wit_arm_same(name, "bus-range") && length >= 8) {
                candidate.BusFirst = wit_arm_be32(value);
                candidate.BusLast = wit_arm_be32(value + 4);
            } else if (wit_arm_same(name, "interrupt-map")) {
                candidate.InterruptMap = value;
                candidate.InterruptMapBytes = length;
            } else if (wit_arm_same(name, "interrupt-map-mask") && length >= 16) {
                for (WitU32 i = 0; i < 4; ++i) {
                    candidate.MapMask[i] = wit_arm_be32(value + i * 4);
                }
            }
        }
    }
}

static const InterruptController *controller(WitU32 phandle)
{
    for (WitU32 i = 0; i < CONTROLLER_CAPACITY; ++i) {
        if (controllers[i].Live && controllers[i].Phandle == phandle) {
            return &controllers[i];
        }
    }
    return 0;
}

/* The GIC INTID of a function's INTx pin from the interrupt map: each entry is the child unit address (three
 * cells: the PCI unit address, two zero cells), the pin, the parent phandle, then the parent's address cells and
 * its interrupt cells (the GIC: type 0 = SPI, number, flags); entries are matched under the mask like the Open
 * Firmware binding, and the parent's cell counts come from its node. */
static int map_interrupt(const EcamBridge *bridge, WitU32 bus, WitU32 device, WitU32 pin, WitU32 *intid)
{
    if (!bridge->InterruptMap) {
        return 0;
    }
    const WitU32 address = (bus << 16) | (device << 11);
    for (WitU32 at = 0; at + 24 <= bridge->InterruptMapBytes;) {
        const WitU8 *e = bridge->InterruptMap + at;
        const InterruptController *parent = controller(wit_arm_be32(e + 16));
        if (!parent || parent->InterruptCells < 2 || parent->AddressCells > 4) {
            return 0;
        }
        const WitU32 entry = (3 + 1 + 1 + parent->AddressCells + parent->InterruptCells) * 4;
        if (at + entry > bridge->InterruptMapBytes) {
            return 0;
        }
        const WitU32 child = wit_arm_be32(e) & bridge->MapMask[0];
        const WitU32 child_pin = wit_arm_be32(e + 12) & bridge->MapMask[3];
        if (child == (address & bridge->MapMask[0]) && child_pin == (pin & bridge->MapMask[3])) {
            const WitU8 *cells = e + 20 + parent->AddressCells * 4;
            const WitU32 type = wit_arm_be32(cells), number = wit_arm_be32(cells + 4);
            *intid = (type == 0 ? 32 : 16) + number; /* SPI INTIDs start at 32, PPIs at 16. */
            return 1;
        }
        at += entry;
    }
    return 0;
}

static volatile WitU32 *config(
    const WitBootInfo *boot, const EcamBridge *bridge, WitU32 bus, WitU32 device, WitU32 function, WitU32 offset)
{
    const WitU64 page = bridge->Base + ((WitU64)bus << 20) + ((WitU64)device << 15) + ((WitU64)function << 12);
    wit_arm_map_once(boot, page);
    return (volatile WitU32 *)(page + (offset & 0xFFCU));
}

static void describe_bars(const WitBootInfo *boot, const EcamBridge *bridge, WitU32 bus, WitU32 device, WitU32 function,
    WitDeviceDescriptor *d)
{
    volatile WitU32 *command = config(boot, bridge, bus, device, function, 0x04);
    const WitU32 saved = *command;
    *command = saved & ~3U;
    for (WitU32 bar = 0; bar < 6 && d->RegionCount < WIT_DEVICE_REGIONS;) {
        volatile WitU32 *slot = config(boot, bridge, bus, device, function, 0x10 + bar * 4);
        const WitU32 original = *slot;
        *slot = 0xFFFFFFFFU;
        const WitU32 probe = *slot;
        *slot = original;
        if (probe == 0) {
            ++bar;
            continue;
        }
        WitDeviceRegion *r = &d->Regions[d->RegionCount];
        if (original & 1) {
            r->Base = original & ~3U;
            r->Size = (~(probe & ~3U) + 1) & 0xFFFF;
            r->Flags = WIT_DEVICE_REGION_PORT;
            ++bar;
        } else {
            const int wide = (original & 6) == 4;
            WitU64 base = original & ~15U, mask = probe & ~15U;
            if (wide) {
                if (bar + 1 >= 6) {
                    break;
                }
                volatile WitU32 *upper = config(boot, bridge, bus, device, function, 0x14 + bar * 4);
                const WitU32 upper_original = *upper;
                *upper = 0xFFFFFFFFU;
                const WitU32 upper_probe = *upper;
                *upper = upper_original;
                base |= (WitU64)upper_original << 32;
                mask |= (WitU64)upper_probe << 32;
            } else {
                mask |= 0xFFFFFFFF00000000ULL;
            }
            r->Base = base;
            r->Size = ~mask + 1;
            r->Flags = WIT_DEVICE_REGION_MEMORY | ((original & 8) ? WIT_DEVICE_REGION_PREFETCHABLE : 0);
            bar += wide ? 2 : 1;
        }
        r->Reserved = 0;
        if (r->Size) {
            ++d->RegionCount;
        }
    }
    *command = saved;
}

static WitU32 describe_bus(const WitBootInfo *boot, const EcamBridge *bridge, WitU32 bus, WitU32 depth,
    WitDeviceDescriptor *table, WitU32 count, WitU32 capacity)
{
    for (WitU32 device = 0; device < 32; ++device) {
        const WitU32 first = *config(boot, bridge, bus, device, 0, 0x00);
        if ((first & 0xFFFF) == 0xFFFF || (first & 0xFFFF) == 0) {
            continue;
        }
        const WitU32 functions = (*config(boot, bridge, bus, device, 0, 0x0C) & 0x00800000U) ? 8 : 1;
        for (WitU32 function = 0; function < functions; ++function) {
            const WitU32 id = *config(boot, bridge, bus, device, function, 0x00);
            if ((id & 0xFFFF) == 0xFFFF || (id & 0xFFFF) == 0) {
                continue;
            }
            const WitU32 header = (*config(boot, bridge, bus, device, function, 0x0C) >> 16) & 0x7F;
            if (header == 1 && depth < BRIDGE_DEPTH) {
                const WitU32 secondary = (*config(boot, bridge, bus, device, function, 0x18) >> 8) & 0xFF;
                if (secondary > bus && secondary <= bridge->BusLast) {
                    count = describe_bus(boot, bridge, secondary, depth + 1, table, count, capacity);
                }
            }
            if (header != 0) {
                continue; /* no driver drives a bridge, and the table describes none (ADR 0029, H1.2a) */
            }
            if (count >= capacity) {
                return count;
            }
            WitDeviceDescriptor *d = &table[count];
            for (WitU32 i = 0; i < sizeof(*d); ++i) {
                ((WitU8 *)d)[i] = 0;
            }
            d->Bus = WIT_DEVICE_BUS_PCI;
            d->Address = (bus << 16) | (device << 8) | function;
            d->Identity[0] = id;
            d->Identity[1] = *config(boot, bridge, bus, device, function, 0x08);
            d->Identity[2] = header == 0 ? *config(boot, bridge, bus, device, function, 0x2C) : 0;
            d->Identity[3] = header;
            WitDeviceRegion *r = &d->Regions[d->RegionCount++];
            r->Base = bridge->Base + ((WitU64)bus << 20) + ((WitU64)device << 15) + ((WitU64)function << 12);
            r->Size = 4096;
            r->Flags = WIT_DEVICE_REGION_MEMORY | WIT_DEVICE_REGION_CONFIG;
            if (header == 0) {
                describe_bars(boot, bridge, bus, device, function, d);
            }
            const WitU32 pin = (*config(boot, bridge, bus, device, function, 0x3C) >> 8) & 0xFF;
            WitU32 intid = 0;
            if (pin >= 1 && pin <= 4 && map_interrupt(bridge, bus, device, pin, &intid)) {
                d->Lines[d->LineCount].Kind = WIT_DEVICE_LINE_LEVEL;
                d->Lines[d->LineCount].Line = intid;
                ++d->LineCount;
            }
            ++count;
        }
    }
    return count;
}

WitU32 wit_platform_devices(const WitBootInfo *boot, WitDeviceDescriptor *table, WitU32 capacity)
{
    EcamBridge bridge = {0};
    WitU32 count = 0;
    if (!boot->DeviceTree || (boot->DeviceTree & 7)) {
        wit_console_write("[DEVICES] No device tree: no bus enumerated\n");
    } else {
        read_tree(boot, &bridge);
        if (!bridge.Found || !bridge.Base || !bridge.Size || bridge.BusLast < bridge.BusFirst) {
            wit_console_write("[DEVICES] No generic ECAM bridge in the device tree\n");
        } else {
            count = describe_bus(boot, &bridge, bridge.BusFirst, 0, table, 0, capacity);
        }
    }
    /* The firmware's framebuffer (C2.1): a device of the boot contract, after the bus's functions. */
    if (count < capacity && wit_device_describe_framebuffer(boot, &table[count])) {
        ++count;
    }
    return count;
}
