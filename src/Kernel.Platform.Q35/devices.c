#include "witos/boot.h"
#include "witos/device.h"
#include "witos/platform.h"
#include "witos/x64_instructions.h"

/* q35 device enumeration (plan step K3.1): the PCI functions of the board through configuration mechanism #1
 * (ports 0xCF8 and 0xCFC), which needs no mapping; the ECAM window whose base the host bridge's PCIEXBAR register
 * holds is published as each function's configuration region but never read here. The firmware assigned the BARs
 * and routed INTx through the PIC: the Interrupt Line register holds the PIC input. Nothing here knows a device
 * class; the descriptors carry the identity words as the bus reports them. */

#define CONFIG_ADDRESS 0xCF8U
#define CONFIG_DATA 0xCFCU
#define CONFIG_ENABLE 0x80000000U
#define BUS_LIMIT 256U
#define BRIDGE_DEPTH 4U

static WitU32 config_read(WitU32 bus, WitU32 device, WitU32 function, WitU32 offset)
{
    wit_x64_out32(CONFIG_ADDRESS, CONFIG_ENABLE | (bus << 16) | (device << 11) | (function << 8) | (offset & 0xFCU));
    return wit_x64_in32(CONFIG_DATA);
}

static void config_write(WitU32 bus, WitU32 device, WitU32 function, WitU32 offset, WitU32 value)
{
    wit_x64_out32(CONFIG_ADDRESS, CONFIG_ENABLE | (bus << 16) | (device << 11) | (function << 8) | (offset & 0xFCU));
    wit_x64_out32(CONFIG_DATA, value);
}

/* PCIEXBAR of the q35 host bridge (D0:F0, offset 0x60): bits 35:28 the base, bits 2:1 the length, bit 0 enable. */
static WitU64 ecam_base(void)
{
    const WitU64 low = config_read(0, 0, 0, 0x60), high = config_read(0, 0, 0, 0x64);
    const WitU64 value = low | (high << 32);
    if (!(value & 1)) {
        return 0;
    }
    return value & 0x0000000FF0000000ULL;
}

/* Sizes the BARs of a type 0 header with decoding disabled; fills regions after the configuration region. */
static void describe_bars(WitU32 bus, WitU32 device, WitU32 function, WitDeviceDescriptor *d)
{
    const WitU32 command = config_read(bus, device, function, 0x04);
    config_write(bus, device, function, 0x04, command & ~3U);
    for (WitU32 bar = 0; bar < 6 && d->RegionCount < WIT_DEVICE_REGIONS;) {
        const WitU32 offset = 0x10 + bar * 4;
        const WitU32 original = config_read(bus, device, function, offset);
        config_write(bus, device, function, offset, 0xFFFFFFFFU);
        const WitU32 probe = config_read(bus, device, function, offset);
        config_write(bus, device, function, offset, original);
        if (probe == 0) {
            ++bar;
            continue;
        }
        WitDeviceRegion *r = &d->Regions[d->RegionCount];
        if (original & 1) {
            const WitU32 size = ~(probe & ~3U) + 1;
            r->Base = original & ~3U;
            r->Size = size & 0xFFFF;
            r->Flags = WIT_DEVICE_REGION_PORT;
            ++bar;
        } else {
            const int wide = (original & 6) == 4;
            WitU64 base = original & ~15U, mask = probe & ~15U;
            if (wide) {
                if (bar + 1 >= 6) {
                    break;
                }
                const WitU32 upper = config_read(bus, device, function, offset + 4);
                config_write(bus, device, function, offset + 4, 0xFFFFFFFFU);
                const WitU32 upper_probe = config_read(bus, device, function, offset + 4);
                config_write(bus, device, function, offset + 4, upper);
                base |= (WitU64)upper << 32;
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
    config_write(bus, device, function, 0x04, command);
}

static WitU32 describe_bus(
    WitU64 ecam, WitU32 bus, WitU32 depth, WitDeviceDescriptor *table, WitU32 count, WitU32 capacity)
{
    for (WitU32 device = 0; device < 32; ++device) {
        const WitU32 functions = (config_read(bus, device, 0, 0x0C) & 0x00800000U) ? 8 : 1;
        for (WitU32 function = 0; function < functions; ++function) {
            const WitU32 id = config_read(bus, device, function, 0x00);
            if ((id & 0xFFFF) == 0xFFFF || (id & 0xFFFF) == 0) {
                continue;
            }
            const WitU32 header = (config_read(bus, device, function, 0x0C) >> 16) & 0x7F;
            if (header == 1 && depth < BRIDGE_DEPTH) {
                const WitU32 secondary = (config_read(bus, device, function, 0x18) >> 8) & 0xFF;
                if (secondary > bus) {
                    count = describe_bus(ecam, secondary, depth + 1, table, count, capacity);
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
            d->Identity[1] = config_read(bus, device, function, 0x08);
            d->Identity[2] = header == 0 ? config_read(bus, device, function, 0x2C) : 0;
            d->Identity[3] = header;
            if (ecam) {
                WitDeviceRegion *r = &d->Regions[d->RegionCount++];
                r->Base = ecam + ((WitU64)bus << 20) + ((WitU64)device << 15) + ((WitU64)function << 12);
                r->Size = 4096;
                r->Flags = WIT_DEVICE_REGION_MEMORY | WIT_DEVICE_REGION_CONFIG;
            }
            if (header == 0) {
                describe_bars(bus, device, function, d);
            }
            const WitU32 interrupt = config_read(bus, device, function, 0x3C);
            const WitU32 pin = (interrupt >> 8) & 0xFF, line = interrupt & 0xFF;
            if (pin >= 1 && pin <= 4 && line < 16) {
                d->Lines[d->LineCount].Kind = WIT_DEVICE_LINE_LEVEL;
                d->Lines[d->LineCount].Line = line;
                ++d->LineCount;
            }
            ++count;
        }
    }
    return count;
}

WitU32 wit_platform_devices(const WitBootInfo *boot, WitDeviceDescriptor *table, WitU32 capacity)
{
    (void)boot; /* q35 is enumerated through its configuration ports; the ACPI tables are not needed. */
    if ((config_read(0, 0, 0, 0x00) & 0xFFFF) == 0xFFFF) {
        return 0; /* No host bridge: no PCI. */
    }
    return describe_bus(ecam_base(), 0, 0, table, 0, capacity);
}
