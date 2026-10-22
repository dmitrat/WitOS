#include "witos/cpu.h"
#include "witos/arch.h"
#include "witos/boot.h"
#include "witos/platform.h"
#include "witos/processor.h"

/* The processors as the kernel runs them (plan steps K7.2 and P1.2). The boot processor starts the others one at a
 * time through the architecture and waits for each to report itself idle. Afterwards any processor addresses any
 * other with a request: the kernel lock's holder writes the request's argument, sets its kind in the target's pending
 * set and rings the target's doorbell, an inter-processor interrupt that only says the set changed; the target serves
 * every kind its set holds — from the doorbell's interrupt, or while it spins for the kernel lock, so that a holder that
 * waits for an acknowledgement never waits for a processor that waits for it — and acknowledges each by a counter.
 * Waits are bounded in monotonic time and end in a panic: an unstarted processor or a lost request is a defect, not a
 * degraded mode. */

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
    volatile WitU32 Pending; /* the kinds of request sent and not yet served, a bit each */
    volatile WitU64 Acks[WIT_REQUEST_KINDS];
    volatile WitU64 InvalidateAddress; /* the argument of an invalidation, written before its kind */
    void (*volatile Run)(void); /* the function of a RUN request */
} Cpu;

static Cpu cpus[WIT_PROCESSOR_CAPACITY];
static WitU32 count, online;
static int ready;
static volatile WitU32 lock_owner; /* the kernel lock: its holder's number plus one, zero when free (P1.2) */

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
        cpus[i].Pending = 0;
        for (WitU32 k = 0; k < WIT_REQUEST_KINDS; ++k) {
            cpus[i].Acks[k] = 0;
        }
        cpus[i].InvalidateAddress = 0;
        cpus[i].Run = 0;
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
            wit_arch_spin_pause();
        }
        require(cpus[i].State == CpuIdle, "Secondary processor did not come online");
        ++online;
    }
    if (count > 1) {
        wit_platform_ipi_enable_boot(); /* the boot processor takes requests too (P1.2) */
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
    /* Its number, recorded at its entry (P1.1), names its hardware identity. */
    require(wit_arch_processor_index() == index && wit_arch_processor_id() == cpus[index].HardwareId,
        "A starting processor does not read its own number");
    wit_arch_process_write_barrier();
    cpus[index].State = CpuIdle;
    wit_arch_process_write_barrier();
}

/* Serves every kind of request the calling processor's pending set holds, each acknowledged once served; a RUN request
 * is acknowledged when taken, before its function runs, since the function may want the kernel lock its sender holds
 * until the acknowledgement. RESCHEDULE and STOP are acknowledged and leave the processor where it is: their actions
 * belong to the dispatcher of P1.3, while only the boot processor runs threads. */
WitU32 wit_cpus_serve(void)
{
    if (!ready) {
        return 0;
    }
    /* The processor knows its number from its own register (P1.1); a spin for the lock serves here, so nothing slower. */
    const WitU32 me = wit_arch_processor_index();
    require(me < count, "A request reached an unknown processor");
    Cpu *const cpu = &cpus[me];
    const WitU32 pending = __atomic_exchange_n(&cpu->Pending, 0, __ATOMIC_ACQ_REL);
    void (*run)(void) = 0;
    for (WitU32 kind = 0; kind < WIT_REQUEST_KINDS; ++kind) {
        if ((pending & (1U << kind)) == 0) {
            continue;
        }
        if (kind == WIT_REQUEST_INVALIDATE) {
            wit_arch_invalidate_local(cpu->InvalidateAddress);
        } else if (kind == WIT_REQUEST_PUBLISH) {
            wit_arch_publish_code();
        } else if (kind == WIT_REQUEST_RUN) {
            run = cpu->Run;
            cpu->Run = 0;
        }
        __atomic_fetch_add(&cpu->Acks[kind], 1, __ATOMIC_ACQ_REL); /* a fence for FENCE, and the ordering of the rest */
    }
    require((pending >> WIT_REQUEST_KINDS) == 0, "Unknown kind of request");
    if (run) {
        run();
    }
    return pending;
}

WitU32 wit_cpus_ipi_received(void)
{
    if (ready) {
        /* The processor's identity must be its own on whatever stack the doorbell interrupted: on riscv64 a hart on a
         * thread's kernel stack once took the boot hart's (2026-10-22), and read its pending kind. */
        const WitU32 me = wit_arch_processor_index();
        require(me < count && wit_arch_processor_id() == cpus[me].HardwareId,
            "A doorbell on a processor whose identity is not its own");
    }
    return wit_cpus_serve();
}

/* The kernel lock (P1.2): every entry into the kernel's state takes it and the processor gives it up only when it
 * leaves for a thread or halts; a processor that already holds it takes it again for nothing. While it spins it
 * serves its requests. A processor that waits marks itself, and one that released the lock while others waited takes
 * it again only after one of them took it (P1.3b): a thread that keeps entering the kernel otherwise keeps it from
 * the others, on hardware where nothing makes a compare-and-exchange fair and under QEMU's round-robin TCG, which
 * runs the waiter only once the holder's slice ends, mostly inside the kernel. A request served while waiting may take
 * the lock itself (a RUN of the self-tests), so the mark is counted per nesting. */
static volatile WitU32 lock_waiters; /* a bit per processor that waits */
static volatile WitU32 lock_yields_to; /* the waiters the last holder released the lock to, zero when nobody waited */

void wit_cpus_lock(void)
{
    const WitU32 me = wit_arch_processor_index() + 1U;
    const WitU32 bit = 1U << (me - 1U);
    if (lock_owner == me) {
        return;
    }
    const WitU32 marked = __atomic_fetch_or(&lock_waiters, bit, __ATOMIC_ACQ_REL) & bit;
    for (;;) {
        const WitU32 yields = __atomic_load_n(&lock_yields_to, __ATOMIC_ACQUIRE);
        WitU32 expected = 0;
        if ((yields == 0 || (yields & bit) != 0) &&
            __atomic_compare_exchange_n(&lock_owner, &expected, me, 0, __ATOMIC_ACQUIRE, __ATOMIC_RELAXED)) {
            __atomic_store_n(&lock_yields_to, 0, __ATOMIC_RELEASE);
            if (!marked) {
                __atomic_fetch_and(&lock_waiters, ~bit, __ATOMIC_ACQ_REL);
            }
            return;
        }
        if (yields != 0 && (__atomic_load_n(&lock_waiters, __ATOMIC_ACQUIRE) & yields) == 0) {
            /* the processors it yielded to stopped waiting: nobody is owed the lock any more */
            WitU32 owed = yields;
            __atomic_compare_exchange_n(&lock_yields_to, &owed, 0, 0, __ATOMIC_ACQ_REL, __ATOMIC_RELAXED);
        }
        wit_cpus_serve();
        wit_arch_spin_pause();
    }
}

void wit_cpus_unlock(void)
{
    const WitU32 me = wit_arch_processor_index() + 1U;
    require(lock_owner == me, "Releasing a kernel lock the processor does not hold");
    const WitU32 others = __atomic_load_n(&lock_waiters, __ATOMIC_ACQUIRE) & ~(1U << (me - 1U));
    __atomic_store_n(&lock_yields_to, others, __ATOMIC_RELEASE);
    __atomic_store_n(&lock_owner, 0, __ATOMIC_RELEASE);
}

int wit_cpus_lock_held(void)
{
    return lock_owner == wit_arch_processor_index() + 1U;
}

/* The return to a frame gives the lock up once the processor runs on the frame's stack (P1.3b): until then it ran on a
 * kernel stack another processor may take as soon as the lock is free, the stack of a thread it left Ready or waiting.
 * A free lock is read first, so that a return before the processor knows its number reads no register for it. */
void wit_cpus_release(void)
{
    if (__atomic_load_n(&lock_owner, __ATOMIC_RELAXED) != 0 && wit_cpus_lock_held()) {
        wit_cpus_unlock();
    }
}

/* A request of one kind to another processor, by the kernel lock's holder, which waits for its acknowledgement. */
static void request(WitU32 index, WitU32 kind, WitU64 argument)
{
    require(wit_cpus_lock_held(), "A request sent without the kernel lock");
    require(
        index < count && index != wit_arch_processor_index() && wit_cpus_is_online(index) && kind < WIT_REQUEST_KINDS,
        "A request to no other online processor");
    Cpu *const cpu = &cpus[index];
    const WitU64 before = cpu->Acks[kind];
    if (kind == WIT_REQUEST_INVALIDATE) {
        cpu->InvalidateAddress = argument;
    } else if (kind == WIT_REQUEST_RUN) {
        cpu->Run = (void (*)(void))argument;
    }
    __atomic_fetch_or(&cpu->Pending, 1U << kind, __ATOMIC_ACQ_REL);
    wit_arch_ipi(cpu->HardwareId, WIT_IPI_DOORBELL);
    const WitU64 deadline = deadline_after(ACKNOWLEDGE_SECONDS);
    while (__atomic_load_n(&cpu->Acks[kind], __ATOMIC_ACQUIRE) == before && wit_platform_monotonic_read() < deadline) {
        wit_cpus_serve();
        wit_arch_spin_pause();
    }
    require(__atomic_load_n(&cpu->Acks[kind], __ATOMIC_ACQUIRE) != before, "A request was not acknowledged");
}

static void request_all(WitU32 kind, WitU64 argument)
{
    wit_arch_process_write_barrier();
    for (WitU32 i = 0; i < count; ++i) {
        if (i != wit_arch_processor_index() && wit_cpus_is_online(i)) {
            request(i, kind, argument);
        }
    }
}

void wit_cpus_fence_all(void)
{
    request_all(WIT_REQUEST_FENCE, 0);
}

void wit_cpus_invalidate_all(WitU64 address)
{
    request_all(WIT_REQUEST_INVALIDATE, address);
}

void wit_cpus_invalidate(WitU32 index, WitU64 address)
{
    wit_arch_process_write_barrier(); /* the changed entry before the target's walk */
    request(index, WIT_REQUEST_INVALIDATE, address);
}

/* Code published on the calling processor becomes visible to every other's instruction fetch (P1.3b). */
void wit_cpus_publish_all(void)
{
    request_all(WIT_REQUEST_PUBLISH, 0);
}

void wit_cpus_request(WitU32 index, WitU32 kind)
{
    require(kind == WIT_REQUEST_FENCE || kind == WIT_REQUEST_RESCHEDULE || kind == WIT_REQUEST_STOP,
        "A request with an argument sent without it");
    request(index, kind, 0);
}

void wit_cpus_run(WitU32 index, void (*function)(void))
{
    request(index, WIT_REQUEST_RUN, (WitU64)function);
}

WitU64 wit_cpus_acks(WitU32 index, WitU32 kind)
{
    return index < count && kind < WIT_REQUEST_KINDS ? __atomic_load_n(&cpus[index].Acks[kind], __ATOMIC_ACQUIRE) : 0;
}
