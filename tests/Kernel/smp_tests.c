#include "witos/cpu.h"
#include "witos/arch.h"
#include "witos/platform.h"
#include "witos/processor.h"
#include "self_test.h"

/* The processors as the kernel runs them (RFC 0011 section 7.9, plan steps K7.2, P1.1, P1.2 and P1.3a): every present
 * processor came online and the table says so; a fence request reaches every other processor and is acknowledged,
 * twice in a row, and an invalidation request once; each processor reads its own number. Then the request fabric and
 * the kernel lock under contention (P1.2): the boot processor has the first secondary run a loop that takes the kernel
 * lock, counts and fences every other processor, the boot processor among them, while the boot processor runs the
 * same loop; the count comes out exact, and each processor acknowledged every fence the other sent, which it could
 * only do while it spun for the lock or waited. Last, a secondary's own tick (P1.3a). With one processor present the
 * mechanisms are exercised on nobody and the checks hold trivially. */

#define CONTENTION_ROUNDS 64U
#define DONE_SECONDS 2U
#define TICK_MILLISECONDS 100U /* ten ticks at 100 Hz */

static volatile WitU64 contended;
static volatile WitU32 secondary_done;
static volatile WitU64 ticks_started, ticks_stopped;

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

static void contend(void)
{
    for (WitU32 i = 0; i < CONTENTION_ROUNDS; ++i) {
        wit_cpus_lock();
        contended = contended + 1;
        wit_cpus_fence_all();
        wit_cpus_unlock();
    }
    __atomic_store_n(&secondary_done, 1U, __ATOMIC_RELEASE);
}

/* Each processor's tick (P1.3a), on the first secondary: its timer started and stopped by RUN requests, its ticks
 * counted by its own counter while it halts, which the boot processor's counter, whose timer is off, never sees. */
static void tick_start(void)
{
    ticks_started = wit_arch_clock_ticks();
    wit_platform_timer_start();
}

static void tick_stop(void)
{
    wit_platform_timer_stop();
    __atomic_store_n(&ticks_stopped, wit_arch_clock_ticks(), __ATOMIC_RELEASE);
}

static void wait_milliseconds(WitU64 milliseconds)
{
    const WitU64 deadline = wit_platform_monotonic_read() + wit_platform_monotonic_frequency() * milliseconds / 1000U;
    while (wit_platform_monotonic_read() < deadline) {
        wit_cpus_serve();
        wit_arch_spin_pause();
    }
}

static void ticks(WitU32 count)
{
    if (count < 2) {
        return;
    }
    const WitU64 boot_ticks = wit_arch_clock_ticks();
    ticks_stopped = ~0ULL;
    wit_cpus_lock();
    wit_cpus_run(1, tick_start);
    wit_cpus_unlock();
    wait_milliseconds(TICK_MILLISECONDS);
    wit_cpus_lock();
    wit_cpus_run(1, tick_stop);
    wit_cpus_unlock();
    const WitU64 deadline = wit_platform_monotonic_read() + wit_platform_monotonic_frequency() * DONE_SECONDS;
    while (__atomic_load_n(&ticks_stopped, __ATOMIC_ACQUIRE) == ~0ULL && wit_platform_monotonic_read() < deadline) {
        wit_arch_spin_pause();
    }
    const WitU64 counted = ticks_stopped - ticks_started;
    require(ticks_stopped != ~0ULL && counted >= TICK_MILLISECONDS / 20U && counted <= TICK_MILLISECONDS / 5U,
        "A secondary processor's tick did not keep its rate");
    require(wit_arch_clock_ticks() == boot_ticks, "A secondary processor's tick reached the boot processor's counter");
    wit_console_write("Secondary ticks in 100 ms: ");
    wit_console_write_u64(counted);
    wit_console_write("\n");
}

/* The boot processor and the first secondary contend for the kernel lock, each fencing the other while it holds it. */
static void contention(WitU32 count)
{
    if (count < 2) {
        return;
    }
    const WitU64 boot_fences = wit_cpus_acks(0, WIT_REQUEST_FENCE);
    const WitU64 secondary_fences = wit_cpus_acks(1, WIT_REQUEST_FENCE);
    contended = 0;
    secondary_done = 0;
    wit_cpus_lock();
    wit_cpus_run(1, contend);
    wit_cpus_unlock();
    for (WitU32 i = 0; i < CONTENTION_ROUNDS; ++i) {
        wit_cpus_lock();
        contended = contended + 1;
        wit_cpus_fence_all();
        wit_cpus_unlock();
    }
    const WitU64 deadline = wit_platform_monotonic_read() + wit_platform_monotonic_frequency() * DONE_SECONDS;
    while (!__atomic_load_n(&secondary_done, __ATOMIC_ACQUIRE) && wit_platform_monotonic_read() < deadline) {
        wit_cpus_serve(); /* the secondary's last fences reach the boot processor here */
        wit_arch_spin_pause();
    }
    require(__atomic_load_n(&secondary_done, __ATOMIC_ACQUIRE), "The contending secondary processor did not finish");
    wit_cpus_lock();
    require(contended == 2U * CONTENTION_ROUNDS, "The kernel lock let two processors count at once");
    wit_cpus_unlock();
    require(wit_cpus_acks(0, WIT_REQUEST_FENCE) - boot_fences == CONTENTION_ROUNDS &&
            wit_cpus_acks(1, WIT_REQUEST_FENCE) - secondary_fences == CONTENTION_ROUNDS,
        "A processor did not acknowledge every fence the other sent");
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
    wit_cpus_lock();
    wit_cpus_fence_all();
    wit_cpus_fence_all();
    wit_cpus_invalidate_all((WitU64)&count & ~4095ULL);
    wit_cpus_unlock();
    for (WitU32 i = 1; i < count; ++i) {
        require(wit_cpus_acks(i, WIT_REQUEST_FENCE) == 2 && wit_cpus_acks(i, WIT_REQUEST_INVALIDATE) == 1,
            "A secondary processor did not acknowledge every request once");
    }
    wit_console_write("[TEST-PASS] Smp.Ipi\n");
    /* Each processor reads its number from its own register (P1.1): the boot processor here, each secondary at its
     * start and in every request it served above, where cpus.c requires the number to name its hardware identity. */
    require(
        wit_processors_current() == 0 && wit_arch_processor_index() == 0, "The boot processor does not read number 0");
    wit_console_write("[TEST-PASS] Smp.Identity\n");
    contention(count);
    wit_console_write("[TEST-PASS] Smp.LockContention\n");
    ticks(count);
    wit_console_write("[TEST-PASS] Smp.Tick\n");
}
