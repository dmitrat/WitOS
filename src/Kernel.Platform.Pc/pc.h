#ifndef WITOS_PC_H
#define WITOS_PC_H

#include "witos/boot.h"

/* The PC platform's own declarations (plan step H1.1): what its sources share and the rest of the kernel never sees. */

/* A table the XSDT (or a revision 1 firmware's RSDT) names by its four-byte signature, mapped whole, its length and
 * checksum verified; zero when the firmware published no RSDP, no such table or one that fails verification. */
const WitU8 *wit_pc_acpi_table(const WitBootInfo *boot, const char *signature, WitU32 *length);

/* A table at a physical address the FADT names (the DSDT), mapped whole up to `limit` bytes, its length and checksum
 * verified; zero when it fails. */
const WitU8 *wit_pc_acpi_table_at(const WitBootInfo *boot, WitU64 physical, WitU32 limit, WitU32 *length);

/* ACPI's power without AML (power.c, ADR 0029): the FADT's PM1 control blocks, the DSDT's `_S5` when it is a static
 * package, the reset register; read at boot, used at the end. */
void wit_pc_power_initialize(const WitBootInfo *boot);
void wit_pc_power_off(void);
void wit_pc_reset(void);

/* Little-endian fields of an ACPI table. */
WitU32 wit_pc_le32(const WitU8 *bytes);
WitU64 wit_pc_le64(const WitU8 *bytes);

#endif
