#include "witos/processor.h"
#include "witos/arch.h"
#include "witos/boot.h"
#include "witos/platform.h"

/* The processor table (RFC 0011 section 7.9, plan step K7.1): what the platform enumerated once at boot, kept in
 * the kernel's numbering with the boot processor first. Descriptive only until K7.2: the boot processor is online,
 * the others are present. The common kernel reads no firmware table; the platform does. */

static WitProcessorDescriptor table[WIT_PROCESSOR_CAPACITY];
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
    ready = 1;
    wit_console_write("Processors present/online: ");
    wit_console_write_u64(count);
    wit_console_write("/1\n");
}

WitU32 wit_processors_count(void)
{
    require(ready, "Processor table read before initialization");
    return count;
}

/* The boot processor alone runs threads until K7.2 starts the others. */
WitU32 wit_processors_online(void)
{
    require(ready, "Processor table read before initialization");
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
    record->Flags = index == 0 ? WIT_PROCESSOR_ONLINE | WIT_PROCESSOR_BOOT : 0;
    record->Group = 0;
    record->Number = (WitU16)index;
    record->CacheBytes = index == 0 ? wit_arch_cache_size() : 0;
    record->Features = index == 0 ? wit_arch_processor_features() : 0;
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
