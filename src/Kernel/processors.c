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
    /* The boot processor is number 0 from here on (P1.1); each secondary records its number as it starts (cpus.c). */
    wit_arch_processor_set_index(0);
    require(wit_arch_processor_index() == 0, "The boot processor does not read its own number");
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

/* Threads run on every online processor (P1.3b). */
WitU32 wit_processors_scheduling(void)
{
    return wit_cpus_online();
}

/* The calling processor's number, from its own register (P1.1). */
WitU32 wit_processors_current(void)
{
    return wit_arch_processor_index();
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

/* Any mask within the table that holds an online processor (P1.4); one of present processors alone is unsupported,
 * since none of them runs a thread. */
WitU64 wit_processors_affinity_status(WitU64 mask)
{
    require(ready, "Processor table read before initialization");
    if (!mask || (count < 64 && (mask >> count) != 0)) {
        return WIT_STATUS_INVALID_ARGUMENT;
    }
    for (WitU32 i = 0; i < count; ++i) {
        if ((mask & (1ULL << i)) && wit_cpus_is_online(i)) {
            return WIT_STATUS_OK;
        }
    }
    return WIT_STATUS_UNSUPPORTED;
}

/* A new thread's affinity: every processor of the table (P1.4), as Linux gives a thread every CPU. */
WitU64 wit_processors_all(void)
{
    require(ready, "Processor table read before initialization");
    return count >= 64 ? ~0ULL : (1ULL << count) - 1;
}
