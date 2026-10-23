#ifndef WITOS_PC_H
#define WITOS_PC_H

#include "witos/boot.h"

/* The PC platform's own declarations (plan step H1.1): what its sources share and the rest of the kernel never sees. */

/* A table the XSDT (or a revision 1 firmware's RSDT) names by its four-byte signature, mapped whole, its length and
 * checksum verified; zero when the firmware published no RSDP, no such table or one that fails verification. */
const WitU8 *wit_pc_acpi_table(const WitBootInfo *boot, const char *signature, WitU32 *length);

/* Little-endian fields of an ACPI table. */
WitU32 wit_pc_le32(const WitU8 *bytes);
WitU64 wit_pc_le64(const WitU8 *bytes);

#endif
