#include "witos/arch.h"
#include "witos/boot.h"
#include "witos/device.h"
#include "witos/platform.h"
#include "witos/processor.h"

/* QEMU virt device enumeration (plan step K3.1): the PCI functions behind the generic ECAM host bridge the
 * flattened device tree describes (compatible "pci-host-ecam-generic": reg, bus-range, interrupt-map), read
 * through the ECAM pages mapped on demand. The firmware assigned the BARs; the INTx lines come from the device
 * tree's interrupt map, GIC SPI numbers as INTIDs. Without a device tree (the firmware publishes one only when it
 * runs without ACPI) there is no bus and the table is empty. Nothing here knows a device class. */

#define FDT_MAGIC 0xD00DFEEDU
#define FDT_BEGIN_NODE 1U
#define FDT_END_NODE 2U
#define FDT_PROP 3U
#define FDT_NOP 4U
#define FDT_END 9U
#define FDT_MAX_BYTES (16U << 20) /* a sanity bound on the padded total size */
#define FDT_BLOCK_BYTES (256U << 10) /* the structure or strings block of a board's tree */
#define MAPPED_CAPACITY 192U
#define BRIDGE_DEPTH 4U

static WitU64 mapped[MAPPED_CAPACITY];
static WitU32 mapped_count;

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

/* Maps a page for reading once; the device tree and the ECAM window are never usable RAM. */
static void map_once(const WitBootInfo *boot, WitU64 page)
{
    for (WitU32 i = 0; i < mapped_count; ++i) {
        if (mapped[i] == page) {
            return;
        }
    }
    require(mapped_count < MAPPED_CAPACITY, "Too many device pages mapped during enumeration");
    wit_arch_map_device_page(boot, page);
    mapped[mapped_count++] = page;
}

static void map_range(const WitBootInfo *boot, WitU64 base, WitU64 bytes)
{
    for (WitU64 page = base & ~4095ULL; page < base + bytes; page += 4096) {
        map_once(boot, page);
    }
}

static WitU32 be32(const WitU8 *p)
{
    return ((WitU32)p[0] << 24) | ((WitU32)p[1] << 16) | ((WitU32)p[2] << 8) | p[3];
}

static WitU64 be64(const WitU8 *p)
{
    return ((WitU64)be32(p) << 32) | be32(p + 4);
}

static int same(const char *a, const char *b)
{
    while (*a && *a == *b) {
        ++a;
        ++b;
    }
    return *a == *b;
}

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
#define NODE_DEPTH 8U
static InterruptController controllers[CONTROLLER_CAPACITY];

/* Walks the tree once; the first node compatible with the generic ECAM bridge wins. */
static void read_tree(const WitBootInfo *boot, EcamBridge *bridge)
{
    const WitU8 *tree = (const WitU8 *)boot->DeviceTree;
    map_once(boot, boot->DeviceTree & ~4095ULL);
    if (be32(tree) != FDT_MAGIC) {
        return;
    }
    /* The firmware's copy of the tree is padded to a size well beyond its blocks (its total size is more than a
     * megabyte), so only the structure and strings blocks of a version 17 header are mapped, never the whole. */
    const WitU32 total = be32(tree + 4), structure = be32(tree + 8), strings = be32(tree + 12);
    const WitU32 version = be32(tree + 20), strings_size = be32(tree + 32), structure_size = be32(tree + 36);
    if (version < 17 ||
        total > FDT_MAX_BYTES ||
        structure >= total ||
        strings >= total ||
        structure_size > total - structure ||
        strings_size > total - strings ||
        structure_size > FDT_BLOCK_BYTES ||
        strings_size > FDT_BLOCK_BYTES) {
        return;
    }
    map_range(boot, boot->DeviceTree + structure, structure_size);
    map_range(boot, boot->DeviceTree + strings, strings_size);
    const WitU32 limit = structure + structure_size, strings_limit = strings + strings_size;
    WitU32 offset = structure, depth = 0;
    int in_bridge = 0, compatible_seen = 0;
    EcamBridge candidate = {0};
    InterruptController node[NODE_DEPTH + 1] = {{0}}; /* the controller properties of the open nodes, by depth */
    while (offset + 4 <= limit) {
        const WitU32 token = be32(tree + offset);
        offset += 4;
        if (token == FDT_BEGIN_NODE) {
            WitU32 length = 0;
            while (offset + length < limit && tree[offset + length]) {
                ++length;
            }
            const char *name = (const char *)tree + offset;
            offset = (offset + length + 1 + 3) & ~3U;
            ++depth;
            if (depth <= NODE_DEPTH) {
                node[depth] = (InterruptController){0};
            }
            if (depth == 2 && !bridge->Found) {
                in_bridge = 1;
                compatible_seen = 0;
                candidate = (EcamBridge){0};
                (void)name;
            } else if (depth > 2) {
                in_bridge = 0;
            }
        } else if (token == FDT_END_NODE) {
            if (depth == 2 && in_bridge && compatible_seen && candidate.Size && !bridge->Found) {
                *bridge = candidate;
                bridge->Found = 1;
            }
            in_bridge = 0;
            if (depth && depth <= NODE_DEPTH && node[depth].Live) {
                for (WitU32 i = 0; i < CONTROLLER_CAPACITY; ++i) {
                    if (!controllers[i].Live) {
                        controllers[i] = node[depth];
                        break;
                    }
                }
            }
            if (depth) {
                --depth;
            }
        } else if (token == FDT_PROP) {
            if (offset + 8 > limit) {
                return;
            }
            const WitU32 length = be32(tree + offset), name_offset = be32(tree + offset + 4);
            offset += 8;
            if (offset + length > limit || strings + name_offset >= strings_limit) {
                return;
            }
            const char *name = (const char *)tree + strings + name_offset;
            const WitU8 *value = tree + offset;
            offset = (offset + length + 3) & ~3U;
            if (depth <= NODE_DEPTH) {
                if (same(name, "phandle") && length >= 4) {
                    node[depth].Phandle = be32(value);
                } else if (same(name, "#address-cells") && length >= 4) {
                    node[depth].AddressCells = be32(value);
                } else if (same(name, "#interrupt-cells") && length >= 4) {
                    node[depth].InterruptCells = be32(value);
                } else if (same(name, "interrupt-controller")) {
                    node[depth].Live = 1;
                }
            }
            if (!in_bridge || depth != 2) {
                continue;
            }
            if (same(name, "compatible")) {
                for (WitU32 i = 0; i < length;) {
                    if (same((const char *)value + i, "pci-host-ecam-generic")) {
                        compatible_seen = 1;
                    }
                    while (i < length && value[i]) {
                        ++i;
                    }
                    ++i;
                }
            } else if (same(name, "reg") && length >= 16) {
                candidate.Base = be64(value);
                candidate.Size = be64(value + 8);
            } else if (same(name, "bus-range") && length >= 8) {
                candidate.BusFirst = be32(value);
                candidate.BusLast = be32(value + 4);
            } else if (same(name, "interrupt-map")) {
                candidate.InterruptMap = value;
                candidate.InterruptMapBytes = length;
            } else if (same(name, "interrupt-map-mask") && length >= 16) {
                for (WitU32 i = 0; i < 4; ++i) {
                    candidate.MapMask[i] = be32(value + i * 4);
                }
            }
        } else if (token == FDT_NOP) {
        } else {
            return; /* FDT_END or an unknown token. */
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
        const InterruptController *parent = controller(be32(e + 16));
        if (!parent || parent->InterruptCells < 2 || parent->AddressCells > 4) {
            return 0;
        }
        const WitU32 entry = (3 + 1 + 1 + parent->AddressCells + parent->InterruptCells) * 4;
        if (at + entry > bridge->InterruptMapBytes) {
            return 0;
        }
        const WitU32 child = be32(e) & bridge->MapMask[0];
        const WitU32 child_pin = be32(e + 12) & bridge->MapMask[3];
        if (child == (address & bridge->MapMask[0]) && child_pin == (pin & bridge->MapMask[3])) {
            const WitU8 *cells = e + 20 + parent->AddressCells * 4;
            const WitU32 type = be32(cells), number = be32(cells + 4);
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
    map_once(boot, page);
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
    if (!boot->DeviceTree || (boot->DeviceTree & 7)) {
        wit_console_write("[DEVICES] No device tree: no bus enumerated\n");
        return 0;
    }
    read_tree(boot, &bridge);
    if (!bridge.Found || !bridge.Base || !bridge.Size || bridge.BusLast < bridge.BusFirst) {
        wit_console_write("[DEVICES] No generic ECAM bridge in the device tree\n");
        return 0;
    }
    return describe_bus(boot, &bridge, bridge.BusFirst, 0, table, 0, capacity);
}

/* The processors of the virt board (plan step K7.1): the children of /cpus whose device_type is "cpu", each with its
 * reg (the MPIDR affinity in one or two cells, as the parent's #address-cells says) and, when present, a status other
 * than "okay" that marks it unusable. The tree's blocks are mapped as for the devices; the walk is the same. */
WitU32 wit_platform_processors(const WitBootInfo *boot, WitProcessorDescriptor *out, WitU32 capacity)
{
    const WitU8 *tree = (const WitU8 *)boot->DeviceTree;
    WitU32 count = 0;
    if (!boot->DeviceTree || (boot->DeviceTree & 7) || !capacity) {
        return 0;
    }
    map_once(boot, boot->DeviceTree & ~4095ULL);
    if (be32(tree) != FDT_MAGIC) {
        return 0;
    }
    const WitU32 total = be32(tree + 4), structure = be32(tree + 8), strings = be32(tree + 12);
    const WitU32 version = be32(tree + 20), strings_size = be32(tree + 32), structure_size = be32(tree + 36);
    if (version < 17 ||
        total > FDT_MAX_BYTES ||
        structure >= total ||
        strings >= total ||
        structure_size > total - structure ||
        strings_size > total - strings ||
        structure_size > FDT_BLOCK_BYTES ||
        strings_size > FDT_BLOCK_BYTES) {
        return 0;
    }
    map_range(boot, boot->DeviceTree + structure, structure_size);
    map_range(boot, boot->DeviceTree + strings, strings_size);
    const WitU32 limit = structure + structure_size, strings_limit = strings + strings_size;
    WitU32 offset = structure, depth = 0, address_cells = 1;
    int in_cpus = 0, is_cpu = 0, has_reg = 0, disabled = 0;
    WitU64 reg = 0;
    while (offset + 4 <= limit) {
        const WitU32 token = be32(tree + offset);
        offset += 4;
        if (token == FDT_BEGIN_NODE) {
            WitU32 length = 0;
            while (offset + length < limit && tree[offset + length]) {
                ++length;
            }
            const char *name = (const char *)tree + offset;
            offset = (offset + length + 1 + 3) & ~3U;
            ++depth;
            if (depth == 2) { /* the root node is depth 1 */
                in_cpus = same(name, "cpus");
                address_cells = 1;
            } else if (depth == 3 && in_cpus) {
                is_cpu = 0;
                has_reg = 0;
                disabled = 0;
                reg = 0;
            }
        } else if (token == FDT_END_NODE) {
            if (depth == 3 && in_cpus && is_cpu && has_reg && count < capacity) {
                out[count].HardwareId = reg;
                out[count].Enabled = !disabled;
                out[count].Reserved = 0;
                ++count;
            }
            if (depth == 2) {
                in_cpus = 0;
            }
            if (depth) {
                --depth;
            }
        } else if (token == FDT_PROP) {
            if (offset + 8 > limit) {
                return count;
            }
            const WitU32 length = be32(tree + offset), name_offset = be32(tree + offset + 4);
            offset += 8;
            if (offset + length > limit || strings + name_offset >= strings_limit) {
                return count;
            }
            const char *name = (const char *)tree + strings + name_offset;
            const WitU8 *value = tree + offset;
            offset = (offset + length + 3) & ~3U;
            if (!in_cpus) {
                continue;
            }
            if (depth == 2 && same(name, "#address-cells") && length >= 4) {
                address_cells = be32(value);
            } else if (depth == 3 && same(name, "device_type") && length >= 4) {
                is_cpu = same((const char *)value, "cpu");
            } else if (depth == 3 && same(name, "reg") && length >= address_cells * 4 && address_cells <= 2) {
                reg = address_cells == 2 ? be64(value) : be32(value);
                has_reg = 1;
            } else if (depth == 3 && same(name, "status") && length >= 5) {
                disabled = !same((const char *)value, "okay");
            }
        } else if (token == FDT_NOP) {
        } else {
            break; /* FDT_END or an unknown token. */
        }
    }
    return count;
}
