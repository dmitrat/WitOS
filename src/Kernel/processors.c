#include "witos/processor.h"
#include "witos/arch.h"
#include "witos/boot.h"
#include "witos/platform.h"
#include "witos/cpu.h"

/* The processor table (RFC 0011 section 7.9, plan step K7.1): what the platform enumerated once at boot, kept in
 * the kernel's numbering with the boot processor first. The others come online through cpus.c (K7.2); their
 * features and caches are what they report on themselves. The common kernel reads no firmware table; the platform does. */

static WitProcessorDescriptor table[WIT_PROCESSOR_CAPACITY];
static WitU64 features[WIT_PROCESSOR_CAPACITY], caches[WIT_PROCESSOR_CAPACITY];
static WitU32 count;
static int ready;

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

void wit_processors_initialize(const WitBootInfo *boot)
{
    WitProcessorDescriptor found[WIT_PROCESSOR_CAPACITY];
    const WitU64 self = wit_arch_processor_id();
    WitU32 enumerated, boot_at;
    require(!ready && !wit_arch_interrupts_enabled(), "Invalid processor table initialization context");
    enumerated = wit_platform_processors(boot, found, WIT_PROCESSOR_CAPACITY);
    require(enumerated <= WIT_PROCESSOR_CAPACITY, "Platform reported more processors than the table holds");
    boot_at = enumerated;
    for (WitU32 i = 0; i < enumerated; ++i) {
        if (found[i].Enabled && found[i].HardwareId == self) {
            boot_at = i;
        }
    }
    require(boot_at < enumerated, "Processor table does not name the boot processor");
    table[0] = found[boot_at];
    count = 1;
    for (WitU32 i = 0; i < enumerated; ++i) {
        if (i == boot_at || !found[i].Enabled) {
            continue;
        }
        for (WitU32 k = 0; k < count; ++k) {
            require(table[k].HardwareId != found[i].HardwareId, "Processor table repeats a hardware identity");
        }
        table[count++] = found[i];
    }
    features[0] = wit_arch_processor_features();
    caches[0] = wit_arch_cache_size();
    ready = 1;
}

void wit_processors_report(void)
{
    require(ready, "Processor table read before initialization");
    wit_console_write("Processors present/online: ");
    wit_console_write_u64(count);
    wit_console_write("/");
    wit_console_write_u64(wit_cpus_online());
    wit_console_write("\n");
}

void wit_processors_set_features(WitU32 index, WitU64 processor_features, WitU64 cache_bytes)
{
    require(ready && index < count, "Features reported for a processor outside the table");
    features[index] = processor_features;
    caches[index] = cache_bytes;
}

WitU32 wit_processors_count(void)
{
    require(ready, "Processor table read before initialization");
    return count;
}

WitU32 wit_processors_online(void)
{
    require(ready, "Processor table read before initialization");
    return wit_cpus_online();
}

/* Threads run on the boot processor alone until phase P. */
WitU32 wit_processors_scheduling(void)
{
    return 1;
}

WitU32 wit_processors_current(void)
{
    return 0;
}

int wit_processors_record(WitU32 index, WitProcessorRecord *record)
{
    require(ready, "Processor table read before initialization");
    if (index >= count) {
        return 0;
    }
    record->HardwareId = table[index].HardwareId;
    record->Flags = (wit_cpus_is_online(index) ? WIT_PROCESSOR_ONLINE : 0) | (index == 0 ? WIT_PROCESSOR_BOOT : 0);
    record->Group = 0;
    record->Number = (WitU16)index;
    record->CacheBytes = wit_cpus_is_online(index) ? caches[index] : 0;
    record->Features = wit_cpus_is_online(index) ? features[index] : 0;
    return 1;
}

WitU64 wit_processors_affinity_status(WitU64 mask)
{
    require(ready, "Processor table read before initialization");
    if (!mask || (count < 64 && (mask >> count) != 0)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    return (mask & 1) ? WIT_STATUS_OK : WIT_STATUS_UNSUPPORTED; /* The boot processor is the one online. */
}
