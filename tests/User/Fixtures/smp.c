#include "fixture.h"
#include "witos/processor.h"

/* Threads on every processor (plan step P1.3b), on each board with as many processors as the boot names. In the spin
 * mode the first thread and one it creates spin on PROCESSOR_QUERY until between them they saw every online processor,
 * two at most, or a deadline passed. In the shootdown mode a thread reads a page in a loop while the first thread,
 * spinning beside it, takes the page's access away: the reader's next read faults, which a reader on another processor
 * reaches only once that processor dropped its translation, and the fault ends the component. In the affinity mode
 * (P1.4) the thread pins itself to each of the first two online processors in turn and stays there across yields, as
 * PROCESSOR_QUERY and THREAD_QUERY's version 6 report, while version 5 is still served. The threads make raw calls
 * alone, since the counter of checks is the first thread's; their words lie in the data page. */

#define SEEN ((WitU64 *)(WIT_USER_DATA + 1400))
#define PAGE_ADDRESS (*(volatile WitU64 *)(WIT_USER_DATA + 1408))
#define READS ((WitU64 *)(WIT_USER_DATA + 1416))
#define WANTED (*(volatile WitU64 *)(WIT_USER_DATA + 1424))
#define DEADLINE (*(volatile WitU64 *)(WIT_USER_DATA + 1432))
#define SPIN_MILLISECONDS 40U /* within the fixture profile's budget of ten ticks, which both processors charge */
#define READS_BEFORE 1000U
#define READS_AFTER 16U /* what one read and increment in flight at the shootdown may still add */

static WitU64 now(void)
{
    WitU64 value = 0;
    wit_syscall(WIT_CALL_CLOCK_READ, WIT_CLOCK_MONOTONIC, 0, 0, &value);
    return value;
}

/* The processor the calling thread runs on, from PROCESSOR_QUERY; its count of online processors in *online. */
static WitU64 processor(WitU64 *online)
{
    WitProcessorInfo info;
    WitU64 copied = 0;
    info.Version = WIT_PROCESSOR_INFO_VERSION;
    info.Size = WIT_PROCESSOR_INFO_SIZE;
    if (wit_syscall(WIT_CALL_PROCESSOR_QUERY, (WitU64)&info, WIT_PROCESSOR_INFO_SIZE, 0, &copied) != WIT_STATUS_OK) {
        return 63;
    }
    if (online) {
        *online = info.Online;
    }
    return info.Current;
}

/* Spins until both threads together saw every processor wanted, or the deadline passed. */
static void spin(void)
{
    while ((__atomic_load_n(SEEN, __ATOMIC_ACQUIRE) & WANTED) != WANTED && now() < DEADLINE) {
        __atomic_fetch_or(SEEN, 1ULL << processor(0), __ATOMIC_ACQ_REL);
    }
}

static FIXTURE_THREAD void spinner(WitU64 stack)
{
    spin();
    fixture_thread_exit(0, stack);
}

static void spin_test(void)
{
    WitU64 online = 0, stack = 0;
    processor(&online);
    fixture_check(online >= 1, 10);
    WANTED = online >= 2 ? 3U : 1U;
    *SEEN = 0;
    DEADLINE = now() +
        fixture_expect(WIT_CALL_CLOCK_FREQUENCY, WIT_CLOCK_MONOTONIC, 0, 0, WIT_STATUS_OK) * SPIN_MILLISECONDS / 1000U;
    WitU64 thread = fixture_thread_create(spinner, &stack, 0, WIT_STATUS_OK);
    spin();
    fixture_wait(&thread, 1, WIT_WAIT_INFINITE, WIT_STATUS_OK);
    fixture_close(thread);
    fixture_check((*SEEN & WANTED) == WANTED, 11);
}

static FIXTURE_THREAD void reader(WitU64 stack)
{
    (void)stack;
    const volatile WitU64 *const page = (const volatile WitU64 *)PAGE_ADDRESS;
    for (;;) {
        (void)*page;
        __atomic_fetch_add(READS, 1, __ATOMIC_RELAXED);
    }
}

static void shootdown_test(void)
{
    WitU64 stack = 0;
    const WitU64 page = fixture_expect(WIT_CALL_MEMORY_RESERVE, 4096, 4096, 0, WIT_STATUS_OK);
    fixture_expect(WIT_CALL_MEMORY_COMMIT, page, 4096, WIT_MEMORY_READ | WIT_MEMORY_WRITE, WIT_STATUS_OK);
    *(volatile WitU64 *)page = 0x5A5A5A5A5A5A5A5AULL;
    PAGE_ADDRESS = page;
    *READS = 0;
    fixture_thread_create(reader, &stack, 0, WIT_STATUS_OK);
    /* Spinning beside the reader, which another processor runs when there is one, until its reads advance; on one
     * processor a tick hands it the processor. */
    while (__atomic_load_n(READS, __ATOMIC_RELAXED) < READS_BEFORE) {
    }
    fixture_expect(WIT_CALL_MEMORY_PROTECT, page, 4096, WIT_MEMORY_NONE, WIT_STATUS_OK);
    /* The reader's next read faults and the fault ends the component; reads that go on mean a stale translation. */
    const WitU64 after = __atomic_load_n(READS, __ATOMIC_RELAXED);
    for (;;) {
        fixture_check(__atomic_load_n(READS, __ATOMIC_RELAXED) <= after + READS_AFTER, 12);
    }
}

#define PINNED_YIELDS 64U

/* THREAD_QUERY of the caller in a version's record: the processor that version 6 reports. */
static WitU64 queried_processor(WitU32 version, WitU32 size)
{
    WitUserThreadInfo info;
    info.Version = version; /* the kernel writes the whole record */
    info.Size = size;
    const WitU64 copied = fixture_expect(WIT_CALL_THREAD_QUERY, WIT_THREAD_SELF, (WitU64)&info, size, WIT_STATUS_OK);
    fixture_check(copied == size && info.Version == version && info.Size == size, 30);
    return info.Processor;
}

static void affinity_test(void)
{
    WitProcessorInfo info;
    WitU64 mask = 0;
    info.Version = WIT_PROCESSOR_INFO_VERSION;
    info.Size = WIT_PROCESSOR_INFO_SIZE;
    fixture_expect(WIT_CALL_PROCESSOR_QUERY, (WitU64)&info, WIT_PROCESSOR_INFO_SIZE, 0, WIT_STATUS_OK);
    const WitU64 all = info.Count >= 64 ? ~0ULL : (1ULL << info.Count) - 1;
    fixture_expect(WIT_CALL_THREAD_AFFINITY, WIT_THREAD_SELF, (WitU64)&mask, WIT_THREAD_AFFINITY_GET, WIT_STATUS_OK);
    fixture_check(mask == all, 20); /* every processor by default */
    mask = 0;
    fixture_expect(
        WIT_CALL_THREAD_AFFINITY, WIT_THREAD_SELF, (WitU64)&mask, WIT_THREAD_AFFINITY_SET, WIT_STATUS_INVALID_ARGUMENT);
    mask = all + 1; /* the first processor beyond the table */
    fixture_expect(
        WIT_CALL_THREAD_AFFINITY, WIT_THREAD_SELF, (WitU64)&mask, WIT_THREAD_AFFINITY_SET, WIT_STATUS_INVALID_ARGUMENT);
    const WitU64 pinned = info.Online >= 2 ? 2 : 1;
    for (WitU64 target = 0; target < pinned; ++target) {
        mask = 1ULL << target;
        fixture_expect(
            WIT_CALL_THREAD_AFFINITY, WIT_THREAD_SELF, (WitU64)&mask, WIT_THREAD_AFFINITY_SET, WIT_STATUS_OK);
        for (WitU32 i = 0; i < PINNED_YIELDS; ++i) {
            fixture_check(processor(0) == target, 21 + target);
            fixture_expect(WIT_CALL_THREAD_YIELD, 0, 0, 0, WIT_STATUS_OK);
        }
        fixture_check(queried_processor(WIT_THREAD_INFO_VERSION, WIT_THREAD_INFO_SIZE) == target, 23 + target);
    }
    queried_processor(WIT_THREAD_INFO_VERSION_5, WIT_THREAD_INFO_SIZE_5); /* the record before the processor */
    mask = all;
    fixture_expect(WIT_CALL_THREAD_AFFINITY, WIT_THREAD_SELF, (WitU64)&mask, WIT_THREAD_AFFINITY_SET, WIT_STATUS_OK);
}

FIXTURE_ENTRY void wit_user_start(const WitRootStartup *startup)
{
    fixture_check(startup->AbiVersion == WIT_ABI_VERSION, 1);
    switch (fixture_config(startup)->Mode) {
    case WIT_SMP_TEST_SPIN:
        spin_test();
        break;
    case WIT_SMP_TEST_SHOOTDOWN:
        shootdown_test();
        break;
    case WIT_SMP_TEST_AFFINITY:
        affinity_test();
        break;
    default:
        fixture_failed(2);
    }
    fixture_exit(WIT_TEST_EXIT_CODE);
}
