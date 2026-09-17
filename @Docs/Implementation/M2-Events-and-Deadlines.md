# ADR 0002: Events, deadlines and kernel idle

**Status:** Implemented in WitOS 0.0.7.
**Date:** 2026-09-17.
**Scope:** Bounded native M2 execution; guest .NET is not running.

## Context and decision

NativeAOT needs persistent waitable state, wakeups and deadlines. The prior slice could block only on consuming thread join. When every user thread waited for an external condition, the scheduler needed a safe kernel idle path.

Use component-local manual-reset and auto-reset events with absolute deadlines. Keep event state and handle authority in the common kernel; keep saved-context completion and scheduling in the x64 backend. All operations are serialized on the current single CPU with IF clear.

| Alternative | Assessment |
| --- | --- |
| Poll shared user flags or repeatedly yield | Cannot provide a blocking contract and wastes execution while nothing is ready |
| Typed events plus a bounded thread wait scan | Chosen: explicit signal persistence, close semantics and testable deadlines; four threads/events make scans bounded |
| General multi-object waits, mutexes and cancellation | Additional ownership/ordering rules; defer until an actual runtime adapter requires them |

## Event state and authority

Each component has up to four events, also charged to its eight-entry shared handle table. Handles carry the existing owner/generation/slot identity and a distinct Event type. Wait and Signal are separate rights. User-created events receive both rights; bootstrap tests grant restricted events to verify enforcement.

Create flags are bit 0 ManualReset and bit 1 InitiallySignaled. Zero selects an initially nonsignaled auto-reset event. Unknown bits, including high 64-bit bits, are rejected. Resource failure returns NoMemory without consuming a handle or event slot.

- Auto-reset: Set releases the oldest parked waiter, or retains one signal if no waiter exists. One successful wait consumes that signal. Repeated unclaimed sets coalesce.
- Manual-reset: Set releases every parked waiter and leaves the event signaled. Further waits succeed until Reset.
- Reset clears stored signal state; it cannot revoke a completion already published to a ready thread.
- Close invalidates the event and completes every still-parked wait on that exact handle with Closed. A previously published success/timeout is unchanged.
- Reusing a slot creates a new handle generation. Old completions do not follow the new event, and stale/foreign handles fail validation.

Checking stored state and publishing a wait record happen in one serialized syscall. Set, timeout and close clear the wait kind before marking the thread Ready, so exactly one completion wins. FIFO applies to parked auto-reset waiters; it does not promise CPU scheduling order.

## Experimental ABI v4

Startup and query report version 4. Existing calls 0–12 retain their meanings.

| Call | RCX | RDX | Result |
| --- | --- | --- | --- |
| 13 Clock read | Ignored | Ignored | Monotonic delivered-tick count |
| 14 Clock frequency | Ignored | Ignored | Nominal 100 ticks/second |
| 15 Thread sleep | Absolute deadline | Ignored | Zero at/after deadline |
| 16 Event create | Flags | Ignored | Event handle |
| 17 Event set | Event handle | Ignored | Zero |
| 18 Event reset | Event handle | Ignored | Zero |
| 19 Event wait | Event handle | Absolute deadline | Zero on success |

R8 is unused for these calls. Event close uses existing call 3. Event wait adds status 13 TimedOut and status 14 Closed; errors have a zero result. Sleep rejects the infinite sentinel. Sleep with a past deadline returns immediately.

Event deadlines are absolute unsigned tick counts. Zero polls; `0xFFFFFFFFFFFFFFFF` means infinite. A stored signal is checked before deadline comparison, so a signaled event succeeds even for a poll/past deadline. A nonsignaled event with a finite deadline at/before now returns TimedOut.

On every timer interrupt and at syscall entry, already parked waits whose deadline has arrived expire before later signal/close operations. At a shared deadline, a sleeping signaler therefore cannot retroactively satisfy an expired event wait. The later signal remains available for a future wait.

This ordering uses observed delivered ticks, not an unobservable real-time instant. The counter saturates at max-1 and never wraps into the infinite sentinel. Relative-duration conversion and overflow checking remain the caller/adapter's responsibility.

## Clock limitations

The counter is initialized before the kernel-worker test and remains monotonic across subsequent component activations. It advances on delivered PIT IRQ0, including interrupts during kernel idle. The PIT divisor provides approximately 100 Hz; the reported frequency is nominal.

The counter pauses while IRQ0 is disabled or masked, does not reconstruct missed interrupts and is not firmware uptime, UTC or a calibrated elapsed-time clock. This is sufficient to exercise deadline/wakeup semantics; a real NativeAOT time adapter still needs a clock source/profile that meets its elapsed-time and resolution requirements. Do not label these ticks as a complete Stopwatch implementation.

The existing ten-delivered-tick activation budget remains in force, including idle ticks. It can end a component before a long/infinite wait completes.

## Kernel idle and context safety

When no thread is Ready but at least one remains Waiting, the dispatcher stays on its current guarded kernel stack, clears FS base and invokes an adjacent `STI; HLT; CLI` sequence. STI's interrupt shadow covers HLT, avoiding a ready-check/enable/sleep gap; see the [Intel instruction reference](https://www.intel.com/content/dam/www/public/us/en/documents/manuals/64-ia-32-architectures-software-developer-vol-2b-manual.pdf).

An IRQ during idle saves a nested CPL0 frame below the existing C call stack. The handler validates its owning stack, CPL0 selectors, exact resume label and zero FS base. It expires deadlines and returns to that same kernel frame; it never replaces a saved user context with the idle context.

After CLI restores IF=0, the dispatcher scans ready threads and selects one normally, restoring its TLS and TSS.RSP0. Idle also works when the previous user thread has exited: kernel stacks remain in a fixed pool and are not freed by reaping user stack/TLS pages.

Join edges are distinguished from event/sleep waits. Cycle detection follows only Join edges, so joining an event waiter or sleeper is valid. Indirect application deadlocks involving events are not inferred; the bounded test budget still terminates a permanently blocked activation.

## Validation

The separate `WaitFixture.pe` is built from `tests/User.X64/waits.asm`, checked as one-page RX native code with no imports/relocations, embedded like the other fixtures and uploaded by CI.

The seventeen VM scenarios now require 56 M2 groups (21 isolation, 11 memory, 10 thread and 14 wait groups). The existing 21 contained user faults remain required.

| New group | Evidence |
| --- | --- |
| WaitQueueSemantics | Deterministic native checks of FIFO auto wake, claimed completion ownership, exact deadline, close/reuse and manual wake-all/reset |
| WaitResourceLimits | Event-slot and handle exhaustion without leaked state, followed by recovery |
| WaitSignalState | Ring-3 stored/coalesced auto signals, persistent/reset manual signals, invalid flags and stale handles |
| WaitClockAndIdle | Sleep and event deadlines, past/infinite sleep rules, monotonic results and real CPL0 idle IRQs |
| WaitAutoWake | Three parked workers, one released per signal, then consuming joins |
| WaitManualWake | Three parked workers released by one manual signal |
| WaitCloseAndReuse | Three waiters receive Closed; immediate event-slot reuse cannot change their completion |
| WaitHandoff | Thirty-two request/response exchanges between two threads without lost wakeups |
| WaitDeadlineOrder | Waiter and sleeping signaler share a deadline; timeout wins and the later signal stays available |
| WaitExitCleanup | Component exit removes parked waits, events and handles |
| WaitIdleBudget | Infinite event wait reaches the activation budget through the idle path |
| WaitRights | Wrong type, missing rights, closed handles and a live foreign event are rejected |
| WaitActiveTimeout | An event times out while another user thread spins; no idle required |
| WaitJoinChain | Parent join, child event wait and another child's timed signal all complete |

Native assertions check event/handle closure, park/wake/join counts, physical-page recovery, monotonicity and actual idle interrupts. Ring-3 workers verify TLS/SIMD state after wakeup. Synthetic wait records use kernel-owned contexts and no timer manipulation; the real VM cases exercise trap entry, idle and scheduling.

## Consequences and next work

Memory, threads, raw TLS, join and events now have bounded native evidence. They remain experimental primitives, not an implementation of CoreLib synchronization, managed exceptions or GC suspension.

Next use a pinned NativeAOT target/bootstrap experiment to choose the guest ABI and discover concrete module, compiler TLS, unwind and PAL dependencies. Add runtime context/rendezvous mechanisms and a suitable monotonic clock based on that evidence. Semaphores, mutexes, wait-any/all, timed join, alertable waits, cross-process event sharing, scalable queues and SMP are still absent.
