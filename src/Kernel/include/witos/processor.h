#ifndef WITOS_PROCESSOR_H
#define WITOS_PROCESSOR_H
#include "user_abi.h"
#include "limits.h"

/* Processors (RFC 0011 section 7.9, plan step K7.1): the topology the platform enumerated once at boot, descriptive.
 * PROCESSOR_QUERY(buffer, 280, 0) copies WitProcessorInfo, whose Version and Size the caller sets: the index of the
 * processor the caller runs on, the processors present and online, and one record per processor in the order the
 * kernel numbers them, the boot processor first. The 4-byte form of the same call (the current processor as
 * {group:u16, number:u8, reserved:u8}) is the frozen line's and leaves at K8. Until K7.2 the boot processor alone is
 * online; the others are present with their hardware identity and nothing else. */
#define WIT_PROCESSOR_INFO_VERSION 1U
#define WIT_PROCESSOR_INFO_SIZE 280U
#define WIT_PROCESSOR_ONLINE 1U /* runs threads */
#define WIT_PROCESSOR_BOOT 2U /* the processor the kernel booted on */

typedef struct WitProcessorRecord {
    WitU64 HardwareId; /* the APIC id on x64, the MPIDR affinity fields on ARM64 */
    WitU32 Flags;
    WitU16 Group, Number; /* the kernel's numbering: one group until K7.2 */
    WitU64 CacheBytes; /* the largest architecturally reported cache of an online processor; zero unknown */
    WitU64 Features; /* as the ISA reports them: x64 CPUID.1 ECX in the low word and EDX in the high; ARM64
                        ID_AA64ISAR0_EL1 in the low word and ID_AA64PFR0_EL1 in the high; zero when offline */
} WitProcessorRecord;

WIT_STATIC_ASSERT(sizeof(WitProcessorRecord) == 32, "Processor record ABI");

typedef struct WitProcessorInfo {
    WitU32 Version, Size;
    WitU32 Current, Count;
    WitU32 Online, Reserved;
    WitProcessorRecord Processors[WIT_PROCESSOR_CAPACITY];
} WitProcessorInfo;

WIT_STATIC_ASSERT(sizeof(WitProcessorInfo) == WIT_PROCESSOR_INFO_SIZE, "Processor info ABI");

/* THREAD_AFFINITY(thread handle or WIT_THREAD_SELF, mask pointer, flags): flags 0 reads the thread's mask of
 * processors (bit n is processor n) into the 8-byte buffer (QUERY), 1 sets it from the buffer (AFFINITY). A mask must
 * be nonzero and within the processors present (INVALID_ARGUMENT); one without an online processor is UNSUPPORTED,
 * since placement elsewhere than the boot processor arrives with phase P. */
#define WIT_THREAD_AFFINITY_GET 0U
#define WIT_THREAD_AFFINITY_SET 1U

/* Kernel-internal: a processor the platform enumerated (witos/platform.h): its hardware identity and whether the
 * firmware marks it usable; the kernel keeps the usable ones. */
typedef struct WitProcessorDescriptor {
    WitU64 HardwareId;
    WitU32 Enabled, Reserved;
} WitProcessorDescriptor;

struct WitBootInfo;
/* Reads the platform's table once, the boot processor first; a table without the boot processor is a panic. */
void wit_processors_initialize(const struct WitBootInfo *boot);
WitU32 wit_processors_count(void);
WitU32 wit_processors_online(void);
WitU32 wit_processors_current(void);
/* The record of a processor by the kernel's number; 0 beyond the table. */
int wit_processors_record(WitU32 index, WitProcessorRecord *record);
/* The processors threads run on: the boot processor alone until phase P. */
WitU32 wit_processors_scheduling(void);
/* A started processor reports the features and cache it read on itself (K7.2); the boot log line present/online. */
void wit_processors_set_features(WitU32 index, WitU64 features, WitU64 cache_bytes);
void wit_processors_report(void);
/* A valid affinity mask for a thread: nonzero, within the processors present, and with an online processor. */
WitU64 wit_processors_affinity_status(WitU64 mask);
#endif
