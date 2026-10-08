#ifndef WITOS_CPU_H
#define WITOS_CPU_H
#include "types.h"
#include "limits.h"

/* The processors as the kernel runs them (RFC 0011 section 7.9, plan step K7.2): the boot processor runs the kernel
 * and every thread; the others are started at boot through the architecture, each on its own kernel stack, and idle
 * with interrupts enabled, serving the inter-processor interrupts of the boot processor: a fence (the remote half of
 * PROCESS_WRITE_BARRIER) and the invalidation of one translation. Every request is acknowledged by a counter the
 * boot processor waits for, bounded in monotonic time; a missing acknowledgement is a panic, never a silent return.
 * Scheduling on more than the boot processor arrives with phase P. */
#define WIT_IPI_FENCE 1U
#define WIT_IPI_INVALIDATE 2U

struct WitBootInfo;
/* Starts every present processor but the boot one and waits for each to idle; reports present/online. */
void wit_cpus_initialize(const struct WitBootInfo *boot);
WitU32 wit_cpus_online(void);
int wit_cpus_is_online(WitU32 index);
/* The kernel's number of the processor with a hardware identity, or WIT_PROCESSOR_CAPACITY when unknown. */
WitU32 wit_cpus_index_of(WitU64 hardware_id);
/* A started processor is ready: called by the processor itself once its interrupts are enabled. */
void wit_cpus_secondary_ready(WitU32 index);
/* An inter-processor interrupt arrived on the running (secondary) processor: serve it and acknowledge. */
void wit_cpus_ipi_received(WitU32 kind);
/* Every online secondary processor executes a fence, or invalidates the translation of an address; returns once all
 * acknowledged. */
void wit_cpus_fence_all(void);
void wit_cpus_invalidate_all(WitU64 address);
/* Acknowledgements a secondary processor has given so far, for the self-tests. */
WitU64 wit_cpus_fence_acks(WitU32 index);
WitU64 wit_cpus_invalidate_acks(WitU32 index);
#endif
