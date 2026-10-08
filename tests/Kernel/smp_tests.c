#include "witos/cpu.h"
#include "witos/arch.h"
#include "witos/platform.h"
#include "witos/processor.h"
#include "self_test.h"

/* The processors as the kernel runs them (RFC 0011 section 7.9, plan step K7.2): every present processor came online
 * and the table says so; a fence request reaches every secondary processor and is acknowledged, twice in a row; an
 * invalidation request is acknowledged too. With one processor present the mechanisms are exercised on nobody and
 * the checks hold trivially. */

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

void wit_smp_self_test(void)
{
    WitProcessorRecord record = {0};
    const WitU32 count = wit_processors_count();
    require(wit_cpus_online() == count && wit_processors_online() == count, "A present processor is not online");
    for (WitU32 i = 0; i < count; ++i) {
        require(wit_cpus_is_online(i) && wit_processors_record(i, &record) && (record.Flags & WIT_PROCESSOR_ONLINE),
            "An online processor is not reported online");
        require(wit_cpus_index_of(record.HardwareId) == i, "A processor's identity does not find its number");
    }
    wit_console_write("[TEST-PASS] Smp.Online\n");
    wit_cpus_fence_all();
    wit_cpus_fence_all();
    wit_cpus_invalidate_all((WitU64)&count & ~4095ULL);
    for (WitU32 i = 1; i < count; ++i) {
        require(wit_cpus_fence_acks(i) == 2 && wit_cpus_invalidate_acks(i) == 1,
            "A secondary processor did not acknowledge every request once");
    }
    wit_console_write("[TEST-PASS] Smp.Ipi\n");
}
