#include "witos/processor.h"
#include "witos/cpu.h"
#include "witos/arch.h"
#include "witos/platform.h"
#include "self_test.h"

/* The processor table (RFC 0011 section 7.9, plan step K7.1) on both boards: the platform named at least the boot
 * processor, the kernel numbered it first with the running processor's identity, online and boot flags, every other
 * processor is present with a distinct identity and offline, nothing lies beyond the table, and the boot processor
 * alone is online. The affinity rule follows: the boot processor's bit is accepted, a bit beyond the table is not,
 * a mask without the online processor is unsupported. */

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

void wit_processor_self_test(void)
{
    WitProcessorRecord boot, other;
    const WitU32 count = wit_processors_count();
    require(count >= 1 && count <= WIT_PROCESSOR_CAPACITY, "Processor table is empty or oversized");
    require(wit_processors_online() == wit_cpus_online() &&
            wit_processors_current() == 0 &&
            wit_processors_scheduling() == 1,
        "The online count or the scheduling processor is wrong");
    require(wit_processors_record(0, &boot) &&
            boot.Flags == (WIT_PROCESSOR_ONLINE | WIT_PROCESSOR_BOOT) &&
            boot.HardwareId == wit_arch_processor_id() &&
            boot.Group == 0 &&
            boot.Number == 0 &&
            boot.Features == wit_arch_processor_features() &&
            boot.CacheBytes == wit_arch_cache_size(),
        "The boot processor's record is wrong");
    for (WitU32 i = 1; i < count; ++i) {
        require(wit_processors_record(i, &other) &&
                (other.Flags & WIT_PROCESSOR_BOOT) == 0 &&
                ((other.Flags & WIT_PROCESSOR_ONLINE) != 0) == wit_cpus_is_online(i) &&
                other.Number == i &&
                other.HardwareId != boot.HardwareId &&
                (wit_cpus_is_online(i) || (other.CacheBytes == 0 && other.Features == 0)),
            "A present processor's record is wrong");
    }
    require(!wit_processors_record(count, &other), "A record beyond the table was reported");
    require(wit_processors_affinity_status(1) == WIT_STATUS_OK, "The boot processor's affinity was refused");
    require(wit_processors_affinity_status(0) == WIT_STATUS_INVALID_ARGUMENT, "An empty affinity was accepted");
    require(wit_processors_affinity_status(1ULL << 63) == WIT_STATUS_INVALID_ARGUMENT,
        "An affinity beyond the table was accepted");
    if (count > 1) {
        require(wit_processors_affinity_status(2) == WIT_STATUS_UNSUPPORTED,
            "An affinity without an online processor was accepted");
    }
    wit_console_write("[TEST-PASS] Processors.Topology\n");
}
