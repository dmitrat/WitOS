#include "fixture.h"

/* Event, sleep and wait fixture over the ABI-1 wait of RFC 0011 v3 (plan steps K1.3 and K8.3): one OBJECT_WAIT for every
 * object, absolute monotonic deadlines, EVENT_CREATE with rights 0 (WAIT and SIGNAL). Delays are counted in scheduler
 * ticks of 10 ms converted through CLOCK_FREQUENCY, never in a tick count of the clock itself. The other threads are of
 * the one form; a waiting thread's TLS base is the word of its record, and its wait keeps a vector register and its TLS
 * intact. The data page holds the shared counter at 0, the state at 0x80 and the threads' records from 0x100. */

typedef struct State {
    WitU64 Event, Second, Deadline, Spin, Mode;
} State;

#define COUNTER ((volatile WitU64 *)WIT_USER_DATA)
#define STATE ((volatile State *)(WIT_USER_DATA + 0x80))
#define RECORDS ((volatile FixtureThreadRecord *)(WIT_USER_DATA + 0x100))
#define UTC_2026 1767225600000000000ULL
#define HANDOFFS 32U

/* A system call with a vector register holding the mark across it (XMM6 on x64, D16 on ARM64): its status, or ~0 when the
 * register came back changed; the call's value lands in *value. */
__attribute__((visibility("hidden"))) WitU64 waits_preserved_call(
    WitU64 number, WitU64 a0, WitU64 a1, WitU64 mark, WitU64 *value);
#if defined(__x86_64__)
__asm__(".text\n"
        ".globl waits_preserved_call\n.hidden waits_preserved_call\n"
        "waits_preserved_call:\n"
        "    movq %rcx, %xmm6\n"
        "    mov %rcx, %r10\n"
        "    mov %r8, %r9\n"
        "    mov %rdi, %rax\n"
        "    mov %rsi, %rdi\n"
        "    mov %rdx, %rsi\n"
        "    xor %edx, %edx\n"
        "    syscall\n"
        "    mov %rdx, (%r9)\n"
        "    movq %xmm6, %rcx\n"
        "    cmp %rcx, %r10\n"
        "    je 1f\n"
        "    mov $-1, %rax\n"
        "1:\n"
        "    ret\n");
#else
__asm__(".text\n"
        ".globl waits_preserved_call\n.hidden waits_preserved_call\n"
        "waits_preserved_call:\n"
        "    fmov d16, x3\n"
        "    mov x8, x0\n"
        "    mov x0, x1\n"
        "    mov x1, x2\n"
        "    mov x2, #0\n"
        "    svc #0\n"
        "    str x1, [x4]\n"
        "    fmov x9, d16\n"
        "    cmp x9, x3\n"
        "    b.eq 1f\n"
        "    mov x0, #-1\n"
        "1:\n"
        "    ret\n");
#endif

/* The word at the thread's TLS base: FS on x64, TPIDRRO_EL0 on ARM64. */
static void tls_write(WitU64 value)
{
#if defined(__x86_64__)
    __asm__ volatile("mov %0, %%fs:0" ::"r"(value) : "memory");
#else
    WitU64 base;
    __asm__ volatile("mrs %0, tpidrro_el0" : "=r"(base));
    *(volatile WitU64 *)base = value;
#endif
}

static WitU64 tls_read(void)
{
#if defined(__x86_64__)
    WitU64 value;
    __asm__ volatile("mov %%fs:0, %0" : "=r"(value)::"memory");
    return value;
#else
    WitU64 base;
    __asm__ volatile("mrs %0, tpidrro_el0" : "=r"(base));
    return *(volatile WitU64 *)base;
#endif
}

/* The shared counter, incremented atomically: preemption may fall between the load and the store. */
static void count(void)
{
#if defined(__x86_64__)
    __asm__ volatile("lock incq %0" : "+m"(*COUNTER)::"memory");
#else
    WitU64 value;
    WitU32 failed;
    __asm__ volatile("1: ldaxr %0, [%2]\n"
                     "   add %0, %0, #1\n"
                     "   stlxr %w1, %0, [%2]\n"
                     "   cbnz %w1, 1b\n"
        : "=&r"(value), "=&r"(failed)
        : "r"(COUNTER)
        : "memory");
#endif
}

FIXTURE_CALL WitU64 wait_one(WitU64 handle, WitU64 deadline, WitU64 expected)
{
    return fixture_wait(&handle, 1, deadline, expected);
}

FIXTURE_CALL WitU64 event(WitU64 flags)
{
    return fixture_expect(WIT_CALL_EVENT_CREATE, flags, 0, 0, WIT_STATUS_OK);
}

FIXTURE_CALL void set(WitU64 handle)
{
    fixture_expect(WIT_CALL_EVENT_SET, handle, 0, 0, WIT_STATUS_OK);
}

static void sleep_ticks(WitU64 ticks)
{
    fixture_expect(WIT_CALL_SLEEP_UNTIL, fixture_deadline_ticks(ticks), 0, 0, WIT_STATUS_OK);
}

static void join(WitU64 thread)
{
    fixture_check(fixture_join(thread) == WIT_TEST_EXIT_CODE, 10);
}

static WitU64 start(FixtureThread entry, WitU32 index)
{
    volatile FixtureThreadRecord *record = &RECORDS[index];
    record->Index = index;
    record->Value = 0;
    return fixture_thread_start(entry, record, (WitU64)&record->Value, WIT_STATUS_OK);
}

/* Waits on the shared event without a deadline with its index in its TLS and a vector register; both survive the wait,
 * which a close ends with CLOSED. */
static FIXTURE_THREAD void wait_worker(WitU64 argument)
{
    volatile FixtureThreadRecord *record = (volatile FixtureThreadRecord *)argument;
    const WitU64 index = record->Index;
    WitUserWaitRequest request;
    WitU64 handle = STATE->Event, winner = ~0ULL;
    tls_write(index);
    request.Version = WIT_WAIT_OBJECTS_VERSION;
    request.Size = sizeof(request);
    request.Handles = (WitU64)&handle;
    request.Count = 1;
    request.Flags = 0;
    request.Deadline = WIT_WAIT_INFINITE;
    const WitU64 status = waits_preserved_call(WIT_CALL_OBJECT_WAIT, (WitU64)&request, sizeof(request), index, &winner);
    fixture_check(status == (STATE->Mode == WIT_WAIT_TEST_CLOSE ? WIT_STATUS_CLOSED : WIT_STATUS_OK), 20);
    fixture_check(winner == 0 && tls_read() == index, 21);
    count();
    fixture_thread_exit(WIT_TEST_EXIT_CODE, record->Stack);
}

static FIXTURE_THREAD void handoff_worker(WitU64 argument)
{
    volatile FixtureThreadRecord *record = (volatile FixtureThreadRecord *)argument;
    for (WitU32 i = 0; i < HANDOFFS; ++i) {
        wait_one(STATE->Event, WIT_WAIT_INFINITE, WIT_STATUS_OK);
        set(STATE->Second);
    }
    fixture_thread_exit(WIT_TEST_EXIT_CODE, record->Stack);
}

static FIXTURE_THREAD void deadline_signaler(WitU64 argument)
{
    volatile FixtureThreadRecord *record = (volatile FixtureThreadRecord *)argument;
    fixture_expect(WIT_CALL_SLEEP_UNTIL, STATE->Deadline, 0, 0, WIT_STATUS_OK);
    set(STATE->Event);
    fixture_thread_exit(WIT_TEST_EXIT_CODE, record->Stack);
}

static FIXTURE_THREAD void spin_worker(WitU64 argument)
{
    volatile FixtureThreadRecord *record = (volatile FixtureThreadRecord *)argument;
    while (!STATE->Spin) {
#if defined(__x86_64__)
        __asm__ volatile("pause");
#else
        __asm__ volatile("yield");
#endif
    }
    fixture_thread_exit(WIT_TEST_EXIT_CODE, record->Stack);
}

static FIXTURE_THREAD void delayed_signaler(WitU64 argument)
{
    volatile FixtureThreadRecord *record = (volatile FixtureThreadRecord *)argument;
    sleep_ticks(2);
    set(STATE->Event);
    fixture_thread_exit(WIT_TEST_EXIT_CODE, record->Stack);
}

static void signal_state_test(void)
{
    /* Unknown flag bits and rights outside WAIT and SIGNAL are refused with no handle. */
    fixture_check(fixture_expect(WIT_CALL_EVENT_CREATE, 1ULL << 32, 0, 0, WIT_STATUS_INVALID_ARGUMENT) == 0, 30);
    fixture_check(fixture_expect(WIT_CALL_EVENT_CREATE, 0, 16, 0, WIT_STATUS_INVALID_ARGUMENT) == 0, 31);
    const WitU64 automatic = event(0);
    wait_one(automatic, 0, WIT_STATUS_TIMED_OUT);
    set(automatic);
    set(automatic); /* auto signals coalesce while unclaimed */
    wait_one(automatic, 0, WIT_STATUS_OK);
    wait_one(automatic, 0, WIT_STATUS_TIMED_OUT);
    fixture_close(automatic);
    wait_one(automatic, 0, WIT_STATUS_BAD_HANDLE);
    const WitU64 manual = event(WIT_EVENT_MANUAL_RESET | WIT_EVENT_INITIAL_SIGNALED);
    fixture_check(manual != automatic, 32);
    wait_one(manual, 0, WIT_STATUS_OK);
    wait_one(manual, 0, WIT_STATUS_OK);
    fixture_expect(WIT_CALL_EVENT_RESET, manual, 0, 0, WIT_STATUS_OK);
    wait_one(manual, 0, WIT_STATUS_TIMED_OUT);
    set(manual);
    wait_one(manual, 0, WIT_STATUS_OK);
    fixture_close(manual);
}

static void clock_test(void)
{
    /* The monotonic clock reports its frequency; UTC is past 2026-01-01 on a board whose clock is set; no third clock. */
    fixture_check(fixture_expect(WIT_CALL_CLOCK_FREQUENCY, WIT_CLOCK_MONOTONIC, 0, 0, WIT_STATUS_OK) != 0, 40);
    fixture_check(fixture_expect(WIT_CALL_CLOCK_READ, WIT_CLOCK_UTC, 0, 0, WIT_STATUS_OK) >= UTC_2026, 41);
    fixture_expect(WIT_CALL_CLOCK_READ, 2, 0, 0, WIT_STATUS_INVALID_ARGUMENT);
    const WitU64 start = fixture_expect(WIT_CALL_CLOCK_READ, WIT_CLOCK_MONOTONIC, 0, 0, WIT_STATUS_OK);
    WitU64 deadline = fixture_deadline_ticks(2);
    fixture_expect(WIT_CALL_SLEEP_UNTIL, deadline, 0, 0, WIT_STATUS_OK);
    fixture_check(fixture_expect(WIT_CALL_CLOCK_READ, WIT_CLOCK_MONOTONIC, 0, 0, WIT_STATUS_OK) >= deadline, 42);
    fixture_expect(WIT_CALL_SLEEP_UNTIL, start, 0, 0, WIT_STATUS_OK); /* past deadlines complete immediately */
    fixture_expect(WIT_CALL_SLEEP_UNTIL, 1ULL << 63, 0, 0, WIT_STATUS_INVALID_ARGUMENT);
    const WitU64 handle = event(0);
    deadline = fixture_deadline_ticks(2);
    fixture_check(wait_one(handle, deadline, WIT_STATUS_TIMED_OUT) == 0, 43);
    fixture_check(fixture_expect(WIT_CALL_CLOCK_READ, WIT_CLOCK_MONOTONIC, 0, 0, WIT_STATUS_OK) >= deadline, 44);
    fixture_close(handle);
}

static void wake_test(WitU64 mode)
{
    WitU64 threads[3];
    *COUNTER = 0;
    STATE->Event = event(mode == WIT_WAIT_TEST_MANUAL ? WIT_EVENT_MANUAL_RESET : 0);
    for (WitU32 i = 0; i < 3; ++i) {
        threads[i] = start(wait_worker, i + 1);
    }
    sleep_ticks(2);
    if (mode == WIT_WAIT_TEST_EXIT) {
        return; /* the component exits with its waiters parked */
    }
    if (mode == WIT_WAIT_TEST_CLOSE) {
        /* A close wakes every waiter with CLOSED; the next event is another handle. */
        const WitU64 old = STATE->Event;
        fixture_close(old);
        const WitU64 replacement = event(WIT_EVENT_INITIAL_SIGNALED);
        fixture_check(replacement != old, 50);
        wait_one(replacement, 0, WIT_STATUS_OK);
        fixture_close(replacement);
    } else if (mode == WIT_WAIT_TEST_MANUAL) {
        set(STATE->Event);
    } else {
        /* One auto signal releases exactly one worker. */
        for (WitU64 released = 1; released <= 3; ++released) {
            set(STATE->Event);
            while (*COUNTER < released) {
                fixture_expect(WIT_CALL_THREAD_YIELD, 0, 0, 0, WIT_STATUS_OK);
            }
            fixture_check(*COUNTER == released, 51);
        }
    }
    for (WitU32 i = 0; i < 3; ++i) {
        join(threads[i]);
    }
    fixture_check(*COUNTER == 3, 52);
    if (mode != WIT_WAIT_TEST_CLOSE) {
        fixture_close(STATE->Event);
    }
}

static void handoff_test(void)
{
    STATE->Event = event(0);
    STATE->Second = event(0);
    const WitU64 thread = start(handoff_worker, 0);
    for (WitU32 i = 0; i < HANDOFFS; ++i) {
        set(STATE->Event);
        wait_one(STATE->Second, WIT_WAIT_INFINITE, WIT_STATUS_OK);
    }
    join(thread);
    fixture_close(STATE->Event);
    fixture_close(STATE->Second);
}

static void deadline_test(void)
{
    /* The deadline wins over a signal that comes after it, and the timeout does not consume that signal. */
    STATE->Event = event(0);
    STATE->Deadline = fixture_deadline_ticks(2);
    const WitU64 thread = start(deadline_signaler, 0);
    wait_one(STATE->Event, STATE->Deadline, WIT_STATUS_TIMED_OUT);
    join(thread);
    wait_one(STATE->Event, 0, WIT_STATUS_OK);
    fixture_close(STATE->Event);
}

static void active_timeout_test(void)
{
    /* A deadline expires while another thread runs: the timer, not the idle path, ends the wait. */
    STATE->Event = event(0);
    STATE->Spin = 0;
    const WitU64 thread = start(spin_worker, 0);
    wait_one(STATE->Event, fixture_deadline_ticks(2), WIT_STATUS_TIMED_OUT);
    STATE->Spin = 1;
    join(thread);
    fixture_close(STATE->Event);
}

static void chain_test(void)
{
    /* A join waits for a waiter that a sleeping thread's signal wakes. */
    *COUNTER = 0;
    STATE->Event = event(0);
    const WitU64 waiter = start(wait_worker, 1);
    const WitU64 signaler = start(delayed_signaler, 2);
    join(waiter);
    join(signaler);
    fixture_close(STATE->Event);
}

static void rights_test(const WitUserTestConfig *config)
{
    wait_one(config->Startup.Handles[WIT_ROOT_HANDLE_LOG], 0, WIT_STATUS_WRONG_TYPE);
    fixture_expect(WIT_CALL_EVENT_SET, config->ReadOnlyHandle, 0, 0, WIT_STATUS_DENIED);
    fixture_expect(WIT_CALL_EVENT_RESET, config->ReadOnlyHandle, 0, 0, WIT_STATUS_DENIED);
    wait_one(config->ReadOnlyHandle, 0, WIT_STATUS_TIMED_OUT);
    wait_one(config->SelfHandle, 0, WIT_STATUS_DENIED);
    fixture_expect(WIT_CALL_EVENT_SET, config->ForeignHandle, 0, 0, WIT_STATUS_BAD_HANDLE);
    wait_one(config->ForeignHandle, 0, WIT_STATUS_BAD_HANDLE);
    fixture_close(config->ReadOnlyHandle);
    wait_one(config->ReadOnlyHandle, 0, WIT_STATUS_BAD_HANDLE);
}

FIXTURE_ENTRY void wit_user_start(const WitRootStartup *startup)
{
    const WitUserTestConfig *config = fixture_config(startup);
    fixture_check(startup->AbiVersion == WIT_ABI_VERSION, 1);
    STATE->Mode = config->Mode;
    switch (config->Mode) {
    case WIT_WAIT_TEST_SIGNAL_STATE:
        signal_state_test();
        break;
    case WIT_WAIT_TEST_CLOCK:
        clock_test();
        break;
    case WIT_WAIT_TEST_AUTO:
    case WIT_WAIT_TEST_MANUAL:
    case WIT_WAIT_TEST_CLOSE:
    case WIT_WAIT_TEST_EXIT:
        wake_test(config->Mode);
        break;
    case WIT_WAIT_TEST_HANDOFF:
        handoff_test();
        break;
    case WIT_WAIT_TEST_DEADLINE:
        deadline_test();
        break;
    case WIT_WAIT_TEST_BUDGET:
        /* An infinite wait in the idle path: the tick budget ends the component. */
        wait_one(event(0), WIT_WAIT_INFINITE, WIT_STATUS_OK);
        fixture_failed(60);
    case WIT_WAIT_TEST_RIGHTS:
        rights_test(config);
        break;
    case WIT_WAIT_TEST_ACTIVE_TIMEOUT:
        active_timeout_test();
        break;
    case WIT_WAIT_TEST_JOIN_CHAIN:
        chain_test();
        break;
    default:
        fixture_failed(2);
    }
    fixture_exit(WIT_TEST_EXIT_CODE);
}
