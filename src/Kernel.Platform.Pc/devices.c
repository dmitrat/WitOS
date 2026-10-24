#include "witos/boot.h"
#include "witos/device.h"
#include "witos/platform.h"
#include "witos/x64_instructions.h"
#include "pc.h"

/* The PC's device enumeration (plan steps K3.1 and H1.1): the PCI functions of the board through configuration
 * mechanism #1 (ports 0xCF8 and 0xCFC), which needs no mapping and every PC chipset serves; the ECAM window the ACPI
 * MCFG names for segment 0 is published as the configuration region of each function on a bus it covers but never
 * read here (q35's MCFG names its PCIEXBAR window, VMware's 440BX has no PCIEXBAR, QEMU's pc machine no MCFG). The
 * firmware assigned the BARs and routed INTx through the PIC: the Interrupt Line register holds the PIC input, except
 * on q35's root bus, whose lines the platform routes itself (route_pirqs, plan step N4.3). A line beyond the PIC's
 * sixteen names no routing (0xFF in every function of VMware's, which routes no INTx), and neither does 0, the
 * timer's line on every PC, which a firmware that leaves the register cleared would otherwise give a driver. Nothing here
 * knows a device class; the descriptors carry the identity words as the bus reports them. */

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

/* The ECAM window of segment 0 from the MCFG: after the header and eight reserved bytes, entries of 16 bytes, each the
 * window's base (the address of bus 0, whatever its first bus), the segment, the first and the last bus. */
#define MCFG_ENTRIES 44U
#define MCFG_ENTRY_BYTES 16U

static WitU64 ecam;
static WitU32 ecam_first_bus, ecam_last_bus;

static void find_ecam(const WitBootInfo *boot)
{
    WitU32 length = 0;
    const WitU8 *mcfg = wit_pc_acpi_table(boot, "MCFG", &length);
    for (WitU32 offset = MCFG_ENTRIES; mcfg && offset + MCFG_ENTRY_BYTES <= length; offset += MCFG_ENTRY_BYTES) {
        const WitU8 *entry = mcfg + offset;
        const WitU64 base = wit_pc_le64(entry);
        if (base && !(base & 0xFFFFFULL) && entry[8] == 0 && entry[9] == 0 && entry[10] <= entry[11]) {
            ecam = base;
            ecam_first_bus = entry[10];
            ecam_last_bus = entry[11];
            return;
        }
    }
}

/* INTx lines of the root bus (plan step N4.3). A root-bus function in slot s asserts pin p on the LPC bridge's
 * (00:1f.0) PIRQ input E–H number (s + p) % 4 (q35's rotation; PIRQ A–D serve the chipset's own functions, devices
 * 25 to 31, through its DxxIR registers), and the bridge routes each PIRQ to a PIC line through its routing registers
 * (0x68 to 0x6B for E–H); the firmware routes the four to two lines, 10 and 11, so that a third device shares a
 * line, and the kernel binds a line once (K3.2). The platform routes PIRQ E–H to four lines of its own, each marked
 * level-triggered in the ELCR, and gives every root-bus function below the chipset's slots the line of its pin and
 * slot, after verifying the rotation against the firmware's own assignments: when one of the two rotations
 * reproduces every Interrupt Line register from the firmware's routing, the lines are recomputed and written back
 * to the registers; otherwise the firmware's lines stay. The chipset's functions (SATA, SMBus) keep the firmware's
 * lines; nothing binds them. Measured on OVMF: E–H to 10, 10, 11, 11, slots 2 and 3 on G and H (11), rotation 0;
 * 31.2 and 31.3 on 10 through A–D. */
#define LPC_DEVICE 31U
#define ICH9_LPC_ID 0x29188086U /* q35's LPC bridge; PIIX boards (QEMU's pc, VMware) keep the firmware's lines */
#define LPC_PIRQ_ROUTING 0x68U /* PIRQE..H, a byte each: bits 3:0 the line, bit 7 the routing disabled */
#define ELCR_MASTER 0x4D0U
#define ELCR_SLAVE 0x4D1U
#define NO_ROTATION 2U
#define CHIPSET_SLOT \
    25U /* ICH9's own functions, devices 25 to 31, are routed by its DxxIR registers, not by the slot */

static const WitU8 PIRQ_LINES[4] = {10, 11, 5, 6};
static WitU32 rotation = NO_ROTATION; /* 0: (pin - 1 + slot) % 4; 1: (pin - 1 + slot - 1) % 4 */
static WitU32 firmware_routing; /* the PIRQ E..H routing the firmware left, logged */
static int ich9; /* whether the board has ICH9's LPC bridge, whose PIRQ E..H the platform routes */

static WitU32 pirq_index(WitU32 slot, WitU32 pin, WitU32 r)
{
    return (pin - 1 + slot + (r == 1 ? 3 : 0)) % 4;
}

static int function_present(WitU32 device, WitU32 function)
{
    const WitU32 id = config_read(0, device, function, 0x00);
    return (id & 0xFFFF) != 0xFFFF && (id & 0xFFFF) != 0;
}

static void route_pirqs(void)
{
    if (config_read(0, LPC_DEVICE, 0, 0x00) != ICH9_LPC_ID) {
        return; /* no ICH9 LPC bridge: the firmware's lines stay */
    }
    ich9 = 1;
    const WitU32 firmware = config_read(0, LPC_DEVICE, 0, LPC_PIRQ_ROUTING);
    firmware_routing = firmware;
    for (WitU32 i = 0; i < 4; ++i) {
        if ((firmware >> (8 * i)) & 0x80) {
            return; /* a PIRQ the firmware left unrouted: keep what it did */
        }
    }
    for (WitU32 r = 0; r < 2 && rotation == NO_ROTATION; ++r) {
        int consistent = 1, seen = 0;
        for (WitU32 device = 0; device < CHIPSET_SLOT && consistent; ++device) {
            if (!function_present(device, 0)) {
                continue;
            }
            const WitU32 functions = (config_read(0, device, 0, 0x0C) & 0x00800000U) ? 8 : 1;
            for (WitU32 function = 0; function < functions && consistent; ++function) {
                if (!function_present(device, function)) {
                    continue;
                }
                const WitU32 interrupt = config_read(0, device, function, 0x3C);
                const WitU32 pin = (interrupt >> 8) & 0xFF, line = interrupt & 0xFF;
                if (pin < 1 || pin > 4) {
                    continue;
                }
                seen = 1;
                if (((firmware >> (8 * pirq_index(device, pin, r))) & 0x0F) != line) {
                    consistent = 0;
                }
            }
        }
        if (consistent && seen) {
            rotation = r;
        }
    }
    if (rotation == NO_ROTATION) {
        return;
    }
    config_write(0, LPC_DEVICE, 0, LPC_PIRQ_ROUTING,
        (WitU32)PIRQ_LINES[0] |
            ((WitU32)PIRQ_LINES[1] << 8) |
            ((WitU32)PIRQ_LINES[2] << 16) |
            ((WitU32)PIRQ_LINES[3] << 24));
    WitU8 master = wit_x64_in8(ELCR_MASTER), slave = wit_x64_in8(ELCR_SLAVE);
    for (WitU32 i = 0; i < 4; ++i) {
        if (PIRQ_LINES[i] < 8) {
            master |= (WitU8)(1U << PIRQ_LINES[i]);
        } else {
            slave |= (WitU8)(1U << (PIRQ_LINES[i] - 8));
        }
    }
    wit_x64_out8(ELCR_MASTER, master);
    wit_x64_out8(ELCR_SLAVE, slave);
}

/* The line of a function's pin: the platform's routing on the root bus, the firmware's elsewhere; each one logged,
 * so that a board's routing is measured, never reasoned. */
static WitU32 line_of(WitU32 bus, WitU32 device, WitU32 function, WitU32 pin, WitU32 firmware_line)
{
    WitU32 line = firmware_line;
    if (bus == 0 && device < CHIPSET_SLOT && rotation != NO_ROTATION) {
        line = PIRQ_LINES[pirq_index(device, pin, rotation)];
        const WitU32 interrupt = config_read(bus, device, function, 0x3C);
        config_write(bus, device, function, 0x3C, (interrupt & ~0xFFU) | line); /* the register stays truthful */
    }
    wit_console_write("PCI INTx: ");
    wit_console_write_u64(bus);
    wit_console_write(":");
    wit_console_write_u64(device);
    wit_console_write(".");
    wit_console_write_u64(function);
    wit_console_write(" pin ");
    wit_console_write_u64(pin);
    wit_console_write(" firmware line ");
    wit_console_write_u64(firmware_line);
    wit_console_write(" line ");
    wit_console_write_u64(line);
    wit_console_write("\n");
    return line;
}

/* The routing as decided: the firmware's PIRQ E..H table and the rotation, logged once. */
static void log_routing(void)
{
    if (!ich9) {
        wit_console_write("PCI INTx: no ICH9 LPC bridge, the firmware's lines kept\n");
        return;
    }
    wit_console_write("PCI INTx: firmware PIRQ E-H routing ");
    wit_console_write_hex(firmware_routing);
    wit_console_write(rotation == NO_ROTATION ? ", the firmware's lines kept\n" : ", rotation ");
    if (rotation != NO_ROTATION) {
        wit_console_write_u64(rotation);
        wit_console_write(", PIRQ E-H to 10 11 5 6\n");
    }
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

/* Every function found, described or not, so that a board whose functions overflow the table says so. */
static WitU32 functions_found;

static WitU32 describe_bus(WitU32 bus, WitU32 depth, WitDeviceDescriptor *table, WitU32 count, WitU32 capacity)
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
                    count = describe_bus(secondary, depth + 1, table, count, capacity);
                }
            }
            ++functions_found;
            if (header != 0) {
                continue; /* no driver drives a bridge, and the table describes none (ADR 0029, H1.2a) */
            }
            if (count >= capacity) {
                continue;
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
            if (ecam && bus >= ecam_first_bus && bus <= ecam_last_bus) {
                WitDeviceRegion *r = &d->Regions[d->RegionCount++];
                r->Base = ecam + ((WitU64)bus << 20) + ((WitU64)device << 15) + ((WitU64)function << 12);
                r->Size = 4096;
                r->Flags = WIT_DEVICE_REGION_MEMORY | WIT_DEVICE_REGION_CONFIG;
            }
            if (header == 0) {
                describe_bars(bus, device, function, d);
            }
            const WitU32 interrupt = config_read(bus, device, function, 0x3C);
            const WitU32 pin = (interrupt >> 8) & 0xFF, firmware_line = interrupt & 0xFF;
            if (pin >= 1 && pin <= 4 && firmware_line < 16) {
                const WitU32 line = line_of(bus, device, function, pin, firmware_line);
                if (line) {
                    d->Lines[d->LineCount].Kind = WIT_DEVICE_LINE_LEVEL;
                    d->Lines[d->LineCount].Line = line;
                    ++d->LineCount;
                }
            }
            ++count;
        }
    }
    return count;
}

WitU32 wit_platform_devices(const WitBootInfo *boot, WitDeviceDescriptor *table, WitU32 capacity)
{
    WitU32 count = 0;
    WitDeviceDescriptor framebuffer;
    /* The firmware's framebuffer (C2.1) is a device of the boot contract, described after the bus's functions; its slot
     * is kept, so that a board with more functions than the table holds (VMware's 49) still has its screen. */
    const int has_framebuffer = capacity && wit_device_describe_framebuffer(boot, &framebuffer);
    const WitU32 bus_capacity = capacity - (has_framebuffer ? 1U : 0U);
    wit_pc_power_initialize(boot);
    /* No host bridge: no PCI. */
    if ((config_read(0, 0, 0, 0x00) & 0xFFFF) != 0xFFFF) {
        find_ecam(boot);
        route_pirqs();
        log_routing();
        count = describe_bus(0, 0, table, 0, bus_capacity);
        wit_console_write("PCI: ");
        wit_console_write_u64(functions_found);
        wit_console_write(" functions, ");
        wit_console_write_u64(count);
        wit_console_write(" described without bridges, ECAM ");
        wit_console_write_hex(ecam);
        wit_console_write("\n");
    }
    if (has_framebuffer) {
        table[count++] = framebuffer;
    }
    return count;
}
