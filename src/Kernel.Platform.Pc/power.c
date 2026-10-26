#include "witos/arch.h"
#include "witos/boot.h"
#include "witos/platform.h"
#include "witos/x64_instructions.h"
#include "pc.h"

/* ACPI's power without AML (plan step H1.2a, ADR 0029): the PC reads at boot what its end needs — the FADT's PM1
 * control blocks, its SMI command and ACPI enable value, its reset register and boot architecture flags, and the
 * DSDT's `_S5` object when it is a static package — and logs it, so that a board's power is measured. At the end a
 * success writes `_S5`'s sleep types with SLP_EN, and a reset the reset register's value, the 8042's pulse after it
 * where the board has an 8042. Nothing is evaluated: anything but a static `_S5`, or a hardware-reduced FADT, gives no
 * soft-off, and the end halts with a line that says so. VMware's and QEMU's FADTs (H0.1) are what this reads. */

#define FADT_DSDT 40U
#define FADT_SMI_CMD 48U
#define FADT_ACPI_ENABLE 52U
#define FADT_PM1A_CNT_BLK 64U
#define FADT_PM1B_CNT_BLK 68U
#define FADT_IAPC_BOOT_ARCH 109U
#define FADT_FLAGS 112U
#define FADT_RESET_REG 116U
#define FADT_RESET_VALUE 128U
#define FADT_X_DSDT 140U
#define FADT_X_PM1A_CNT_BLK 172U
#define FADT_X_PM1B_CNT_BLK 184U
#define FLAG_RESET_REG_SUP (1U << 10)
#define FLAG_HW_REDUCED_ACPI (1U << 20)
#define BOOT_ARCH_8042 (1U << 1)
#define ACPI_REVISION 8U /* the header's revision byte */
#define FADT_BOOT_ARCH_REVISION 3U /* ACPI 2.0's FADT, the first to define IAPC_BOOT_ARCH */
#define DSDT_LIMIT (512U * 1024U)
#define SPACE_MEMORY 0U
#define SPACE_IO 1U
#define SCI_EN 1U
#define SLP_TYP_SHIFT 10U
#define SLP_TYP_MASK (7U << SLP_TYP_SHIFT)
#define SLP_EN (1U << 13)
#define SPIN_LIMIT 1000000U

/* A register the FADT names: system I/O or system memory, the address; zero when absent. */
typedef struct Register {
    WitU32 Space;
    WitU64 Address;
} Register;

static Register pm1a, pm1b, reset_register;
static WitU32 smi_command, acpi_enable, reset_value, has_reset, has_8042, has_s5, slp_typ_a, slp_typ_b;
static WitU32 legacy_8042; /* the FADT predates IAPC_BOOT_ARCH, as QEMU's pc machine's does (H5.2) */
static const char *no_soft_off = "no FADT";

/* A generic address structure at an offset of the FADT, kept when the FADT reaches it and its address is set. */
static int read_register(const WitU8 *fadt, WitU32 length, WitU32 offset, Register *r)
{
    if (length < offset + 12 ||
        !wit_pc_le64(fadt + offset + 4) ||
        (fadt[offset] != SPACE_IO && fadt[offset] != SPACE_MEMORY)) {
        return 0;
    }
    r->Space = fadt[offset];
    r->Address = wit_pc_le64(fadt + offset + 4);
    return 1;
}

static void map_register(const WitBootInfo *boot, const Register *r)
{
    if (r->Address && r->Space == SPACE_MEMORY) {
        wit_arch_map_device_page(boot, r->Address & ~4095ULL);
    }
}

static WitU16 read16(const Register *r)
{
    return r->Space == SPACE_IO ? wit_x64_in16((WitU16)r->Address) : *(volatile WitU16 *)r->Address;
}

static void write16(const Register *r, WitU16 value)
{
    if (r->Space == SPACE_IO) {
        wit_x64_out16((WitU16)r->Address, value);
    } else {
        *(volatile WitU16 *)r->Address = value;
    }
}

/* One integer of a package: ZeroOp, OneOp, a byte, a word or a double word constant; nonzero when it is one. */
static int package_integer(const WitU8 *body, WitU32 length, WitU32 *at, WitU32 *value)
{
    if (*at >= length) {
        return 0;
    }
    const WitU8 op = body[*at];
    if (op == 0x00 || op == 0x01) {
        *value = op;
        *at += 1;
        return 1;
    }
    const WitU32 bytes = op == 0x0A ? 1 : op == 0x0B ? 2 : op == 0x0C ? 4 : 0;
    if (!bytes || *at + 1 + bytes > length) {
        return 0;
    }
    *value = 0;
    for (WitU32 i = 0; i < bytes; ++i) {
        *value |= (WitU32)body[*at + 1 + i] << (8 * i);
    }
    *at += 1 + bytes;
    return 1;
}

/* `Name (_S5, Package (n) {a, b, ...})` in the DSDT's AML: NameOp (0x08), the name, PackageOp (0x12), its length's
 * encoding, the element count and the first two integers, which are SLP_TYPa and SLP_TYPb. */
static int find_s5(const WitU8 *dsdt, WitU32 length)
{
    for (WitU32 i = 38; i + 4 < length; ++i) {
        if (dsdt[i] != '_' || dsdt[i + 1] != 'S' || dsdt[i + 2] != '5' || dsdt[i + 3] != '_') {
            continue;
        }
        if (dsdt[i - 1] != 0x08 && !(dsdt[i - 1] == '\\' && dsdt[i - 2] == 0x08)) {
            continue;
        }
        WitU32 at = i + 4;
        if (at >= length || dsdt[at] != 0x12) {
            continue;
        }
        at += 1;
        if (at >= length) {
            return 0;
        }
        at += 1 + (dsdt[at] >> 6); /* PkgLength: the lead byte names the bytes that follow it */
        if (at >= length || dsdt[at] < 1) {
            return 0;
        }
        const WitU32 elements = dsdt[at];
        at += 1;
        if (!package_integer(dsdt, length, &at, &slp_typ_a)) {
            return 0;
        }
        slp_typ_b = slp_typ_a;
        if (elements >= 2 && !package_integer(dsdt, length, &at, &slp_typ_b)) {
            return 0;
        }
        slp_typ_a &= 7;
        slp_typ_b &= 7;
        return 1;
    }
    return 0;
}

static void write_register(const char *name, const Register *r)
{
    wit_console_write(name);
    wit_console_write(r->Space == SPACE_IO ? " I/O " : " memory ");
    wit_console_write_hex(r->Address);
}

void wit_pc_power_initialize(const WitBootInfo *boot)
{
    WitU32 length = 0, dsdt_length = 0;
    const WitU8 *fadt = wit_pc_acpi_table(boot, "FACP", &length);
    if (!fadt || length < FADT_FLAGS + 4) {
        wit_console_write("ACPI power: no FADT\n");
        return;
    }
    const WitU32 flags = wit_pc_le32(fadt + FADT_FLAGS);
    smi_command = wit_pc_le32(fadt + FADT_SMI_CMD);
    acpi_enable = fadt[FADT_ACPI_ENABLE];
    has_8042 = (fadt[FADT_IAPC_BOOT_ARCH] & BOOT_ARCH_8042) != 0;
    legacy_8042 = fadt[ACPI_REVISION] < FADT_BOOT_ARCH_REVISION;
    if (!read_register(fadt, length, FADT_X_PM1A_CNT_BLK, &pm1a) && wit_pc_le32(fadt + FADT_PM1A_CNT_BLK)) {
        pm1a.Space = SPACE_IO;
        pm1a.Address = wit_pc_le32(fadt + FADT_PM1A_CNT_BLK);
    }
    if (!read_register(fadt, length, FADT_X_PM1B_CNT_BLK, &pm1b) && wit_pc_le32(fadt + FADT_PM1B_CNT_BLK)) {
        pm1b.Space = SPACE_IO;
        pm1b.Address = wit_pc_le32(fadt + FADT_PM1B_CNT_BLK);
    }
    has_reset = (flags & FLAG_RESET_REG_SUP) &&
        length > FADT_RESET_VALUE &&
        read_register(fadt, length, FADT_RESET_REG, &reset_register);
    reset_value = has_reset ? fadt[FADT_RESET_VALUE] : 0;
    const WitU64 dsdt_address = length >= FADT_X_DSDT + 8 && wit_pc_le64(fadt + FADT_X_DSDT)
        ? wit_pc_le64(fadt + FADT_X_DSDT)
        : wit_pc_le32(fadt + FADT_DSDT);
    const WitU8 *dsdt = wit_pc_acpi_table_at(boot, dsdt_address, DSDT_LIMIT, &dsdt_length);
    if (flags & FLAG_HW_REDUCED_ACPI) {
        no_soft_off = "a hardware-reduced FADT";
    } else if (!pm1a.Address) {
        no_soft_off = "no PM1a control block";
    } else if (!dsdt) {
        no_soft_off = "no DSDT";
    } else if (!(has_s5 = find_s5(dsdt, dsdt_length))) {
        no_soft_off = "no static _S5 package";
    }
    map_register(boot, &pm1a);
    map_register(boot, &pm1b);
    if (has_reset) {
        map_register(boot, &reset_register);
    }
    wit_console_write("ACPI power: ");
    if (has_s5) {
        write_register("PM1a_CNT", &pm1a);
        if (pm1b.Address) {
            write_register(", PM1b_CNT", &pm1b);
        }
        wit_console_write(", _S5 SLP_TYP ");
        wit_console_write_u64(slp_typ_a);
        wit_console_write("/");
        wit_console_write_u64(slp_typ_b);
    } else {
        wit_console_write(no_soft_off);
    }
    if (has_reset) {
        write_register(", RESET_REG", &reset_register);
        wit_console_write(" value ");
        wit_console_write_u64(reset_value);
    } else {
        wit_console_write(", no reset register");
    }
    wit_console_write(has_8042 ? ", an 8042\n" : ", no 8042\n");
}

void wit_pc_power_off(void)
{
    if (!has_s5) {
        wit_console_write("[POWER] no ACPI soft-off (");
        wit_console_write(no_soft_off);
        wit_console_write("); halted\n");
        return;
    }
    wit_console_write("[POWER] off through PM1a_CNT\n");
    /* A firmware that left the SCI off hands over through its SMI command (VMware's ACPI_ENABLE is zero: already on). */
    if (smi_command && acpi_enable && !(read16(&pm1a) & SCI_EN)) {
        wit_x64_out8((WitU16)smi_command, (WitU8)acpi_enable);
        for (WitU32 spin = 0; spin < SPIN_LIMIT && !(read16(&pm1a) & SCI_EN); ++spin) {
        }
    }
    const WitU16 a = (WitU16)((read16(&pm1a) & ~(SLP_TYP_MASK | SLP_EN)) | (slp_typ_a << SLP_TYP_SHIFT));
    if (pm1b.Address) {
        const WitU16 b = (WitU16)((read16(&pm1b) & ~(SLP_TYP_MASK | SLP_EN)) | (slp_typ_b << SLP_TYP_SHIFT));
        write16(&pm1b, b);
        write16(&pm1b, (WitU16)(b | SLP_EN));
    }
    write16(&pm1a, a);
    write16(&pm1a, (WitU16)(a | SLP_EN));
    for (WitU32 spin = 0; spin < SPIN_LIMIT; ++spin) {
        wit_arch_spin_pause();
    }
    wit_console_write("[POWER] the machine is still on after SLP_EN; halted\n");
}

void wit_pc_reset(void)
{
    if (has_reset) {
        write_register("[POWER] reset through RESET_REG", &reset_register);
        wit_console_write(" value ");
        wit_console_write_u64(reset_value);
        wit_console_write("\n");
        if (reset_register.Space == SPACE_IO) {
            wit_x64_out8((WitU16)reset_register.Address, (WitU8)reset_value);
        } else {
            *(volatile WitU8 *)reset_register.Address = (WitU8)reset_value;
        }
        for (WitU32 spin = 0; spin < SPIN_LIMIT; ++spin) {
            wit_arch_spin_pause();
        }
    }
    if (has_8042) {
        wit_console_write("[POWER] reset through the 8042\n");
        for (WitU32 spin = 0; spin < SPIN_LIMIT && (wit_x64_in8(0x64) & 2); ++spin) {
        }
        wit_x64_out8(0x64, 0xFE);
        for (WitU32 spin = 0; spin < SPIN_LIMIT; ++spin) {
            wit_arch_spin_pause();
        }
    }
}

/* Whether the board has an i8042 for its keyboard (H5.2, ADR 0031): the FADT's IAPC_BOOT_ARCH names one, or the FADT
 * predates the field, as a PC of ACPI 1.0's time had one whatever its tables said. */
int wit_pc_has_8042(void)
{
    return has_8042 || legacy_8042;
}
