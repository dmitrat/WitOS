#ifndef WITOS_CPU_H
#define WITOS_CPU_H
#include "types.h"
#include "limits.h"

/* The processors as the kernel runs them (RFC 0011 section 7.9, plan steps K7.2 and P1.2): the boot processor runs the
 * kernel and every thread; the others are started at boot through the architecture, each on its own kernel stack, and
 * idle with interrupts enabled. Any processor addresses any other with a request, sent by the kernel lock's holder:
 * the kind goes into the target's pending set and a doorbell, the one inter-processor interrupt the architecture
 * sends, tells the target to serve its set. Every request is acknowledged by a counter its sender waits for, bounded
 * in monotonic time; a missing acknowledgement is a panic, never a silent return. Scheduling on more than the boot
 * processor arrives with P1.3. */
#define WIT_IPI_DOORBELL 1U /* the inter-processor interrupt: the target's pending set changed */

/* The kinds of request, a bit each in a pending set (P1.2). A fence is the remote half of PROCESS_WRITE_BARRIER, an
 * invalidation drops one translation (a shootdown, P1.3b), RESCHEDULE has a processor that runs a thread enter its
 * dispatcher and an idle one look again (P1.3b), STOP is reserved, RUN calls a function on the target (the
 * self-tests), and PUBLISH makes published code visible to the target's instruction fetch (P1.3b). */
#define WIT_REQUEST_FENCE 0U
#define WIT_REQUEST_INVALIDATE 1U
#define WIT_REQUEST_RESCHEDULE 2U
#define WIT_REQUEST_STOP 3U
#define WIT_REQUEST_RUN 4U
#define WIT_REQUEST_PUBLISH 5U
#define WIT_REQUEST_KINDS 6U

struct WitBootInfo;
/* Starts every present processor but the boot one and waits for each to idle; reports present/online. */
void wit_cpus_initialize(const struct WitBootInfo *boot);
WitU32 wit_cpus_online(void);
int wit_cpus_is_online(WitU32 index);
/* The kernel's number of the processor with a hardware identity, or WIT_PROCESSOR_CAPACITY when unknown. */
WitU32 wit_cpus_index_of(WitU64 hardware_id);
/* A started processor is ready: called by the processor itself once its interrupts are enabled. */
void wit_cpus_secondary_ready(WitU32 index);
/* The doorbell rang on the running processor: it serves its pending set; wit_cpus_serve does so anywhere. Both return
 * the set they served, a bit per kind. */
WitU32 wit_cpus_ipi_received(void);
WitU32 wit_cpus_serve(void);
/* The kernel lock (P1.2): taken at every entry into the kernel's state, given up when the processor leaves for a
 * thread or halts; taking it again on the holding processor does nothing, and a processor serves its requests while
 * it spins. */
void wit_cpus_lock(void);
void wit_cpus_unlock(void);
int wit_cpus_lock_held(void);
/* The architectures' return to a frame (P1.3b): the lock goes, when the processor holds it, once it runs on the
 * frame's stack. */
void wit_cpus_release(void);
/* Requests, by the kernel lock's holder, each returning once acknowledged: every other online processor fences or
 * invalidates the translation of an address; one processor takes a request without an argument, or runs a function. */
void wit_cpus_fence_all(void);
void wit_cpus_invalidate_all(WitU64 address);
void wit_cpus_invalidate(WitU32 index, WitU64 address);
void wit_cpus_publish_all(void);
void wit_cpus_request(WitU32 index, WitU32 kind);
void wit_cpus_run(WitU32 index, void (*function)(void));
/* The acknowledgements a processor has given for a kind so far, for the self-tests. */
WitU64 wit_cpus_acks(WitU32 index, WitU32 kind);
#endif
