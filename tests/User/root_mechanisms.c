#include "witos/user_abi.h"
#include "witos/limits.h"
#include "witos/exception.h"
#include "witos/memory_info.h"
#include "witos/thread_context.h"
#include "witos/thread_info.h"
#include "witos/thread_reference.h"
#include "witos/wait_objects.h"
#include "witos/syscall.h"
#include "root.h"

/* The mechanisms of ABI-1 that layer 2 has no other consumer of yet (plan step K8.2), checked by the root task on both
 * ISAs: until K8.2 only the frozen line's PAL fixtures used them. A wait on several objects validates every handle
 * before it consumes anything, the first ready object wins and alone is consumed, a deadline ends it, a set wakes a
 * parked waiter with its index and a close ends the wait with CLOSED; a thread handle is an object of the same wait.
 * A suspended thread does not run, its context is read and replaced, and it resumes where the new context says. A
 * thread that has not run yet sits at the top of its empty stack: its context is read there, a context may put it
 * there, and an activation reaches it through the fault callback. The memory-pressure event signals once the
 * component's headroom falls to the low mark and resets above the high mark. MEMORY_RESET zeroes committed pages and
 * keeps them committed, and a range it cannot take whole changes nothing.
 * Workers are threads of the one form (version 2) on stacks of the task's own reservations, which their exit names
 * for the kernel to release; they make raw calls alone, since the counter of checks is the main thread's. */

#define PAGE 4096ULL
#define STACK_BYTES (4 * PAGE)
#define FOREIGN_HANDLE 0x7777000000000001ULL /* a value no table issued */
#define PATTERN 0x5A5A5A5A5A5A5A5AULL

#if defined(__x86_64__)
/* The fault callback is entered with the token, the vector and the address in the SysV argument registers (K8.4d) and
 * no return address. */
#define CALLBACK_ATTRIBUTES __attribute__((force_align_arg_pointer))
#else
#define CALLBACK_ATTRIBUTES
#endif

typedef void (*WorkerEntry)(WitU64 stack);

static volatile WitU64 spins, redirected, worker_status, worker_index, activated, activated_stack;
static WitU64 worker_handles[WIT_WAIT_ANY_CAPACITY];
static WitU32 worker_count;

static WitU64 now(void)
{
    return expect(WIT_CALL_CLOCK_READ, WIT_CLOCK_MONOTONIC, 0, 0, WIT_STATUS_OK);
}

static WitU64 later(WitU64 milliseconds)
{
    return now() + expect(WIT_CALL_CLOCK_FREQUENCY, WIT_CLOCK_MONOTONIC, 0, 0, WIT_STATUS_OK) * milliseconds / 1000;
}

static void request_wait(WitUserWaitRequest *request, const WitU64 *handles, WitU32 count, WitU64 deadline)
{
    request->Version = WIT_WAIT_OBJECTS_VERSION;
    request->Size = sizeof(*request);
    request->Handles = (WitU64)handles;
    request->Count = count;
    request->Flags = 0;
    request->Deadline = deadline;
}

/* OBJECT_WAIT whose status must be the expected one; the winner's index on success. */
static WitU64 wait_objects(const WitU64 *handles, WitU32 count, WitU64 deadline, WitU64 expected)
{
    WitUserWaitRequest request;
    request_wait(&request, handles, count, deadline);
    return expect(WIT_CALL_OBJECT_WAIT, (WitU64)&request, sizeof(request), 0, expected);
}

static WIT_NORETURN void exit_thread(WitU64 stack)
{
    WitU64 result;
    for (;;) {
        wit_syscall(WIT_CALL_THREAD_EXIT, 0, stack, 0, &result);
    }
}

/* Waits without a deadline on the objects the main thread named and reports the status and the winner. */
static ENTRY_ATTRIBUTES WIT_NORETURN void waiting_worker(WitU64 stack)
{
    WitUserWaitRequest request;
    WitU64 index = 0;
    request_wait(&request, worker_handles, worker_count, WIT_WAIT_INFINITE);
    worker_status = wit_syscall(WIT_CALL_OBJECT_WAIT, (WitU64)&request, sizeof(request), 0, &index);
    worker_index = index;
    exit_thread(stack);
}

/* Counts until it is suspended; it never stops on its own. */
static ENTRY_ATTRIBUTES WIT_NORETURN void spinning_worker(WitU64 stack)
{
    (void)stack;
    for (;;) {
        ++spins;
    }
}

/* Where the replaced context resumes the spinning worker, with its stack's base as the argument. */
static ENTRY_ATTRIBUTES WIT_NORETURN void redirected_worker(WitU64 stack)
{
    redirected = stack;
    exit_thread(stack);
}

static WitU64 start_worker(WorkerEntry entry, WitU64 *stack, WitU32 flags)
{
    *stack = expect(WIT_CALL_MEMORY_RESERVE, STACK_BYTES, PAGE, 0, WIT_STATUS_OK);
    expect(WIT_CALL_MEMORY_COMMIT, *stack, STACK_BYTES, WIT_MEMORY_READ | WIT_MEMORY_WRITE, WIT_STATUS_OK);
    WitThreadCreateRequest2 request;
    request.Version = WIT_THREAD_CREATE_VERSION_2;
    request.Size = sizeof(request);
    request.Entry = (WitU64)entry;
    request.Argument = *stack;
    request.StackPointer = *stack + STACK_BYTES;
    request.TlsBase = 0;
    request.Flags = flags;
    request.Reserved = 0;
    return expect(WIT_CALL_THREAD_CREATE, (WitU64)&request, sizeof(request), 0, WIT_STATUS_OK);
}

static WitU32 thread_state(WitU64 thread)
{
    WitUserThreadInfo info;
    info.Version = WIT_THREAD_INFO_VERSION;
    info.Size = sizeof(info);
    expect(WIT_CALL_THREAD_QUERY, thread, (WitU64)&info, sizeof(info), WIT_STATUS_OK);
    return info.State;
}

/* Gives the worker the processor until it parks in its wait. */
static void until_parked(WitU64 thread)
{
    while (thread_state(thread) != WIT_THREAD_STATE_WAITING) {
        expect(WIT_CALL_THREAD_YIELD, 0, 0, 0, WIT_STATUS_OK);
    }
}

/* Joins a worker through its handle and checks that the kernel released the stack its exit named. */
static void join(WitU64 thread, WitU64 stack)
{
    check(wait_objects(&thread, 1, WIT_WAIT_INFINITE, WIT_STATUS_OK) == 0, 30);
    expect(WIT_CALL_HANDLE_CLOSE, thread, 0, 0, WIT_STATUS_OK);
    expect(WIT_CALL_MEMORY_RELEASE, stack, 0, 0, WIT_STATUS_NOT_RESERVED);
}

static void waits(void)
{
    const WitU64 automatic = expect(WIT_CALL_EVENT_CREATE, WIT_EVENT_INITIAL_SIGNALED, 0, 0, WIT_STATUS_OK);
    const WitU64 second = expect(WIT_CALL_EVENT_CREATE, 0, 0, 0, WIT_STATUS_OK);
    const WitU64 manual = expect(WIT_CALL_EVENT_CREATE, WIT_EVENT_MANUAL_RESET, 0, 0, WIT_STATUS_OK);
    WitU64 objects[WIT_WAIT_ANY_CAPACITY + 1] = {automatic, FOREIGN_HANDLE, manual, second, automatic};

    /* The request and every handle are checked before anything is consumed: the signaled event stays signaled. */
    wait_objects(objects, 2, 0, WIT_STATUS_BAD_HANDLE);
    wait_objects(objects, 0, 0, WIT_STATUS_INVALID_ARGUMENT);
    wait_objects(objects, WIT_WAIT_ANY_CAPACITY + 1, 0, WIT_STATUS_INVALID_ARGUMENT);
    wait_objects(objects, 1, WIT_MONOTONIC_MAX + 1, WIT_STATUS_INVALID_ARGUMENT);
    check(wait_objects(objects, 1, 0, WIT_STATUS_OK) == 0, 31);

    /* The first ready object wins and alone is consumed; an auto-reset event is consumed, a manual one stays set. */
    objects[1] = second;
    objects[2] = manual;
    expect(WIT_CALL_EVENT_SET, second, 0, 0, WIT_STATUS_OK);
    expect(WIT_CALL_EVENT_SET, automatic, 0, 0, WIT_STATUS_OK);
    check(wait_objects(objects, 3, 0, WIT_STATUS_OK) == 0, 32);
    check(wait_objects(objects, 3, 0, WIT_STATUS_OK) == 1, 33);
    wait_objects(objects, 3, 0, WIT_STATUS_TIMED_OUT);
    expect(WIT_CALL_EVENT_SET, manual, 0, 0, WIT_STATUS_OK);
    check(wait_objects(objects, 3, 0, WIT_STATUS_OK) == 2, 34);
    check(wait_objects(objects, 3, 0, WIT_STATUS_OK) == 2, 35);
    expect(WIT_CALL_EVENT_RESET, manual, 0, 0, WIT_STATUS_OK);

    /* A deadline ends the wait, never before it. */
    const WitU64 deadline = later(20);
    wait_objects(objects, 3, deadline, WIT_STATUS_TIMED_OUT);
    check(now() >= deadline, 36);

    /* A parked waiter wakes with the index of the object set, which it consumed. */
    WitU64 stack = 0;
    worker_handles[0] = automatic;
    worker_handles[1] = second;
    worker_handles[2] = manual;
    worker_count = 3;
    worker_status = worker_index = ~0ULL;
    WitU64 thread = start_worker(waiting_worker, &stack, 0);
    until_parked(thread);
    expect(WIT_CALL_EVENT_SET, second, 0, 0, WIT_STATUS_OK);
    join(thread, stack);
    check(worker_status == WIT_STATUS_OK && worker_index == 1, 37);
    wait_objects(objects, 3, 0, WIT_STATUS_TIMED_OUT);

    /* A close ends a parked wait on the object with CLOSED; a thread's exit wins a wait beside unset events. */
    worker_count = 2;
    worker_status = worker_index = ~0ULL;
    thread = start_worker(waiting_worker, &stack, 0);
    until_parked(thread);
    expect(WIT_CALL_HANDLE_CLOSE, second, 0, 0, WIT_STATUS_OK);
    objects[1] = thread;
    check(wait_objects(objects, 3, WIT_WAIT_INFINITE, WIT_STATUS_OK) == 1, 38);
    check(worker_status == WIT_STATUS_CLOSED, 39);
    expect(WIT_CALL_HANDLE_CLOSE, thread, 0, 0, WIT_STATUS_OK);
    expect(WIT_CALL_MEMORY_RELEASE, stack, 0, 0, WIT_STATUS_NOT_RESERVED);
    expect(WIT_CALL_HANDLE_CLOSE, automatic, 0, 0, WIT_STATUS_OK);
    expect(WIT_CALL_HANDLE_CLOSE, manual, 0, 0, WIT_STATUS_OK);
}

static void suspension(void)
{
    WitThreadContext context;
    WitU64 stack = 0;
    spins = 0;
    redirected = 0;
    /* Created suspended, the thread has not run: its context is its entry at the top of its empty stack. */
    const WitU64 thread = start_worker(spinning_worker, &stack, WIT_THREAD_START_SUSPENDED);
    expect(WIT_CALL_THREAD_CONTEXT_GET, thread, (WitU64)&context, sizeof(context), WIT_STATUS_OK);
    check(context.SuspendCount == 1 && context.StackHigh == stack + STACK_BYTES, 58);
#if defined(__x86_64__)
    check(context.Rsp == stack + STACK_BYTES && context.Rip == (WitU64)spinning_worker, 59);
#else
    check(context.Sp == stack + STACK_BYTES && context.Pc == (WitU64)spinning_worker, 59);
#endif
    check(expect(WIT_CALL_THREAD_RESUME, thread, 0, 0, WIT_STATUS_OK) == 1, 60);
    while (!spins) {
        expect(WIT_CALL_THREAD_YIELD, 0, 0, 0, WIT_STATUS_OK);
    }
    /* A running thread's context is read, never replaced. */
    expect(WIT_CALL_THREAD_CONTEXT_GET, thread, (WitU64)&context, sizeof(context), WIT_STATUS_OK);
    expect(WIT_CALL_THREAD_CONTEXT_SET, thread, (WitU64)&context, sizeof(context), WIT_STATUS_BUSY);

    /* Suspension counts, and a suspended thread does not run while others sleep. */
    check(expect(WIT_CALL_THREAD_SUSPEND, thread, 0, 0, WIT_STATUS_OK) == 0, 40);
    check(expect(WIT_CALL_THREAD_SUSPEND, thread, 0, 0, WIT_STATUS_OK) == 1, 41);
    check(expect(WIT_CALL_THREAD_RESUME, thread, 0, 0, WIT_STATUS_OK) == 2, 42);
    check(thread_state(thread) == WIT_THREAD_STATE_SUSPENDED, 43);
    const WitU64 counted = spins;
    expect(WIT_CALL_SLEEP_UNTIL, later(30), 0, 0, WIT_STATUS_OK);
    check(spins == counted, 44);

    /* Its context: suspended and ready, on its own stack. */
    expect(WIT_CALL_THREAD_CONTEXT_GET, thread, (WitU64)&context, sizeof(context), WIT_STATUS_OK);
    check(context.State == WIT_THREAD_CONTEXT_READY &&
            context.SuspendCount == 1 &&
            (context.Flags & WIT_THREAD_CONTEXT_SUSPENDED),
        45);
    check(context.StackLow == stack && context.StackHigh == stack + STACK_BYTES, 46);
#if defined(__x86_64__)
    check(context.Rsp >= stack && context.Rsp <= stack + STACK_BYTES, 47);
    WitU64 *const sp = &context.Rsp;
    context.Rip = (WitU64)redirected_worker;
    context.Rdi = stack;
    context.Rcx = stack;
#else
    check(context.Sp >= stack && context.Sp <= stack + STACK_BYTES, 47); /* a leaf may stay at the top */
    WitU64 *const sp = &context.Sp;
    context.Pc = (WitU64)redirected_worker;
    context.X[0] = stack;
#endif
    /* A stack pointer above the thread's stack is refused and changes nothing; the top of the empty stack is applied. */
    *sp = stack + STACK_BYTES + 64;
    expect(WIT_CALL_THREAD_CONTEXT_SET, thread, (WitU64)&context, sizeof(context), WIT_STATUS_BAD_ADDRESS);
    *sp = stack + STACK_BYTES;
    expect(WIT_CALL_THREAD_CONTEXT_SET, thread, (WitU64)&context, sizeof(context), WIT_STATUS_OK);
    check(spins == counted && !redirected, 48);

    /* Resumed, it runs where the context points, with the argument the context gave it. */
    check(expect(WIT_CALL_THREAD_RESUME, thread, 0, 0, WIT_STATUS_OK) == 1, 49);
    join(thread, stack);
    check(redirected == stack && spins == counted, 50);
}

/* The fault callback: the activation the main thread raised reaches the worker, which ends there. */
static CALLBACK_ATTRIBUTES WIT_NORETURN void fault_callback(WitU64 token, WitU64 vector, WitU64 address)
{
    (void)token;
    activated = vector == WIT_EXCEPTION_ACTIVATION_VECTOR ? address : 1;
    exit_thread(activated_stack);
}

/* An activation of a thread that has not run yet: the callback frame goes below the top of its empty stack. */
static void empty_stack_activation(void)
{
    WitU64 stack = 0;
    activated = 0;
    expect(WIT_CALL_EXCEPTION_REGISTER, (WitU64)fault_callback, WIT_EXCEPTION_VERSION, 0, WIT_STATUS_OK);
    const WitU64 thread = start_worker(spinning_worker, &stack, WIT_THREAD_START_SUSPENDED);
    activated_stack = stack;
    expect(WIT_CALL_THREAD_ACTIVATE, thread, (WitU64)redirected_worker, 0, WIT_STATUS_OK);
    check(expect(WIT_CALL_THREAD_RESUME, thread, 0, 0, WIT_STATUS_OK) == 1, 61);
    join(thread, stack);
    check(activated == (WitU64)redirected_worker, 62);
    expect(WIT_CALL_EXCEPTION_REGISTER, 0, WIT_EXCEPTION_VERSION, 0, WIT_STATUS_OK);
}

static void pressure(void)
{
    WitUserMemoryInfo info;
    const WitU64 event = expect(WIT_CALL_MEMORY_PRESSURE_EVENT, 0, 0, 0, WIT_STATUS_OK);
    expect(WIT_CALL_EVENT_SET, event, 0, 0, WIT_STATUS_DENIED); /* the kernel's alone: wait-only */
    wait_objects(&event, 1, 0, WIT_STATUS_TIMED_OUT);

    /* Commit the headroom down to below the low mark: physical memory or the page quota, whichever is less, and the
     * page tables the commit takes from it, one for every 512 pages and at most three above them (K9). */
    expect(WIT_CALL_MEMORY_QUERY, (WitU64)&info, sizeof(info), WIT_MEMORY_INFO_VERSION, WIT_STATUS_OK);
    const WitU64 physical = info.PhysicalAvailableBytes / PAGE;
    const WitU64 quota = (info.OwnedLimitBytes - info.OwnedBytes) / PAGE;
    const WitU64 headroom = physical < quota ? physical : quota;
    check(headroom > WIT_PRESSURE_HIGH_PAGES + WIT_PRESSURE_LOW_PAGES, 51);
    const WitU64 tables = headroom / 512 + 3;
    const WitU64 bytes = (headroom - tables - (WIT_PRESSURE_LOW_PAGES - 8)) * PAGE;
    const WitU64 base = expect(WIT_CALL_MEMORY_RESERVE, bytes, PAGE, 0, WIT_STATUS_OK);
    expect(WIT_CALL_MEMORY_COMMIT, base, bytes, WIT_MEMORY_READ | WIT_MEMORY_WRITE, WIT_STATUS_OK);
    check(wait_objects(&event, 1, 0, WIT_STATUS_OK) == 0, 52);
    check(wait_objects(&event, 1, 0, WIT_STATUS_OK) == 0, 53); /* manual: a wait consumes nothing */

    /* Released, the headroom is back above the high mark and the event resets. */
    expect(WIT_CALL_MEMORY_RELEASE, base, 0, 0, WIT_STATUS_OK);
    wait_objects(&event, 1, 0, WIT_STATUS_TIMED_OUT);
    expect(WIT_CALL_HANDLE_CLOSE, event, 0, 0, WIT_STATUS_OK);
}

static void reset(void)
{
    WitUserMemoryInfo before, after;
    const WitU64 base = expect(WIT_CALL_MEMORY_RESERVE, 3 * PAGE, PAGE, 0, WIT_STATUS_OK);
    expect(WIT_CALL_MEMORY_COMMIT, base, 2 * PAGE, WIT_MEMORY_READ | WIT_MEMORY_WRITE, WIT_STATUS_OK);
    volatile WitU64 *const first = (volatile WitU64 *)base;
    volatile WitU64 *const second = (volatile WitU64 *)(base + PAGE);
    *first = PATTERN;
    *second = PATTERN;
    expect(WIT_CALL_MEMORY_QUERY, (WitU64)&before, sizeof(before), WIT_MEMORY_INFO_VERSION, WIT_STATUS_OK);

    /* A range with a page that is not committed is refused whole. */
    expect(WIT_CALL_MEMORY_RESET, base, 3 * PAGE, 0, WIT_STATUS_NOT_COMMITTED);
    check(*first == PATTERN && *second == PATTERN, 54);

    /* The committed pages read zero, stay committed and writable, and the task owns as much as before. */
    expect(WIT_CALL_MEMORY_RESET, base, 2 * PAGE, 0, WIT_STATUS_OK);
    check(!*first && !*second, 55);
    *second = PATTERN;
    check(*second == PATTERN, 56);
    expect(WIT_CALL_MEMORY_QUERY, (WitU64)&after, sizeof(after), WIT_MEMORY_INFO_VERSION, WIT_STATUS_OK);
    check(after.OwnedBytes == before.OwnedBytes && after.DynamicCommittedBytes == before.DynamicCommittedBytes, 57);
    expect(WIT_CALL_MEMORY_RELEASE, base, 0, 0, WIT_STATUS_OK);
}

void root_mechanisms(void)
{
    waits();
    suspension();
    empty_stack_activation();
    pressure();
    reset();
}
