#ifndef WITOS_X64_LAPIC_H
#define WITOS_X64_LAPIC_H

#include "witos/types.h"

struct WitBootInfo;

/* The boot processor's local APIC for message interrupts (ADR 0029, plan step H1.2b): the architecture owns the APIC,
 * the board prepares it at boot, whatever the number of processors, allocates the vectors below to message targets and
 * completes each message the architecture receives on one of them. A message's address names the APIC by its id, its
 * data the vector; the architecture ends a message at the APIC and delivers it as the token
 * WIT_PLATFORM_MESSAGE_BASE plus the vector, which the board's allocation names. */
#define WIT_X64_MESSAGE_VECTOR_FIRST 0x30U
#define WIT_X64_MESSAGE_VECTOR_COUNT 32U

/* Maps and enables the boot processor's APIC (xAPIC mode, the PIC's line through LINT0) once; later calls do nothing. */
void wit_x64_lapic_boot_prepare(const struct WitBootInfo *boot);
/* The boot processor's APIC id, valid once prepared. */
WitU32 wit_x64_lapic_boot_id(void);
/* Ends the interrupt the running processor takes (its EOI register). */
void wit_x64_lapic_end_of_interrupt(void);

#endif
