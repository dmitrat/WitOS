#include "witos/cpu.h"
#include "witos/arch.h"
#include "witos/boot.h"
#include "witos/platform.h"
#include "witos/processor.h"

/* The processors as the kernel runs them (plan step K7.2). The boot processor starts the others one at a time
 * through the architecture and waits for each to report itself idle; afterwards it addresses them with
 * inter-processor interrupts and waits for their acknowledgements. Waits are bounded in monotonic time and end in a
 * panic: an unstarted processor or a lost interrupt is a defect, not a degraded mode. */

#define START_SECONDS 2U
#define ACKNOWLEDGE_SECONDS 1U

typedef enum {
    CpuOffline,
    CpuStarting,
    CpuIdle,
    CpuBoot
} CpuState;

typedef struct Cpu {
    volatile WitU32 State;
    WitU32 Index;
    WitU64 HardwareId;
    volatile WitU64 FenceAcks;
    volatile WitU64 InvalidateAcks;
    volatile WitU64 InvalidateAddress; /* the request of the next invalidation, written before the interrupt */
} Cpu;

static Cpu cpus[WIT_PROCESSOR_CAPACITY];
static WitU32 count, online;
static int ready;

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

static WitU64 deadline_after(WitU32 seconds)
{
    return wit_platform_monotonic_read() + wit_platform_monotonic_frequency() * seconds;
}

void wit_cpus_initialize(const WitBootInfo *boot)
{
    WitProcessorRecord record;
    require(!ready && !wit_arch_interrupts_enabled(), "Invalid processor start context");
    count = wit_processors_count();
    for (WitU32 i = 0; i < count; ++i) {
        require(wit_processors_record(i, &record), "Processor table shrank");
        cpus[i].Index = i;
        cpus[i].HardwareId = record.HardwareId;
        cpus[i].State = i == 0 ? CpuBoot : CpuOffline;
        cpus[i].FenceAcks = 0;
        cpus[i].InvalidateAcks = 0;
        cpus[i].InvalidateAddress = 0;
    }
    online = 1;
    ready = 1;
    for (WitU32 i = 1; i < count; ++i) {
        wit_arch_secondary_prepare(boot, i, cpus[i].HardwareId);
        cpus[i].State = CpuStarting;
        require(wit_arch_secondary_start(i, cpus[i].HardwareId), "Secondary processor start was refused");
        const WitU64 deadline = deadline_after(START_SECONDS);
        while (cpus[i].State != CpuIdle && wit_platform_monotonic_read() < deadline) {
            wit_arch_process_write_barrier();
        }
        require(cpus[i].State == CpuIdle, "Secondary processor did not come online");
        ++online;
    }
    wit_processors_report();
}

WitU32 wit_cpus_online(void)
{
    return ready ? online : 1;
}

int wit_cpus_is_online(WitU32 index)
{
    return index < count && (index == 0 || cpus[index].State == CpuIdle);
}

WitU32 wit_cpus_index_of(WitU64 hardware_id)
{
    for (WitU32 i = 0; i < count; ++i) {
        if (cpus[i].HardwareId == hardware_id) {
            return i;
        }
    }
    return WIT_PROCESSOR_CAPACITY;
}

void wit_cpus_secondary_ready(WitU32 index)
{
    require(index != 0 && index < count && cpus[index].State == CpuStarting, "A processor reported ready twice");
    wit_arch_process_write_barrier();
    cpus[index].State = CpuIdle;
    wit_arch_process_write_barrier();
}

void wit_cpus_ipi_received(WitU32 kind)
{
    const WitU32 me = wit_cpus_index_of(wit_arch_processor_id());
    require(me != 0 && me < count, "An inter-processor interrupt reached an unknown or the boot processor");
    wit_arch_process_write_barrier();
    if (kind == WIT_IPI_FENCE) {
        ++cpus[me].FenceAcks;
    } else if (kind == WIT_IPI_INVALIDATE) {
        wit_arch_invalidate_local(cpus[me].InvalidateAddress);
        ++cpus[me].InvalidateAcks;
    } else {
        wit_panic("Unknown inter-processor interrupt");
    }
    wit_arch_process_write_barrier();
}

static void request(WitU32 index, WitU32 kind, WitU64 address)
{
    Cpu *cpu = &cpus[index];
    const WitU64 before = kind == WIT_IPI_FENCE ? cpu->FenceAcks : cpu->InvalidateAcks;
    cpu->InvalidateAddress = address;
    wit_arch_process_write_barrier();
    wit_arch_ipi(cpu->HardwareId, kind);
    const WitU64 deadline = deadline_after(ACKNOWLEDGE_SECONDS);
    while ((kind == WIT_IPI_FENCE ? cpu->FenceAcks : cpu->InvalidateAcks) == before &&
        wit_platform_monotonic_read() < deadline) {
        wit_arch_process_write_barrier();
    }
    require((kind == WIT_IPI_FENCE ? cpu->FenceAcks : cpu->InvalidateAcks) != before,
        "Inter-processor interrupt was not acknowledged");
}

void wit_cpus_fence_all(void)
{
    wit_arch_process_write_barrier();
    for (WitU32 i = 1; i < count; ++i) {
        if (cpus[i].State == CpuIdle) {
            request(i, WIT_IPI_FENCE, 0);
        }
    }
}

void wit_cpus_invalidate_all(WitU64 address)
{
    for (WitU32 i = 1; i < count; ++i) {
        if (cpus[i].State == CpuIdle) {
            request(i, WIT_IPI_INVALIDATE, address);
        }
    }
}

WitU64 wit_cpus_fence_acks(WitU32 index)
{
    return index < count ? cpus[index].FenceAcks : 0;
}

WitU64 wit_cpus_invalidate_acks(WitU32 index)
{
    return index < count ? cpus[index].InvalidateAcks : 0;
}
