# Atomic event WaitAny for NativeAOT

**Status:** Implemented in WitOS 0.0.32, experimental ABI v16.
**Scope:** Non-alertable, non-reentrant wait-any over one to four component-owned events. Wait-all, other object types and managed finalization are not implemented by this change.

## Why this is on the startup path

The pinned NativeAOT FinalizerHelpers.cpp calls PalCompatibleWaitAny for its finalization event and optional low-memory event. The implementation now uses a genuine kernel wait, with the actual upstream PAL signature. No polling loop or successful placeholder was added to the runtime adapter.

The [Windows wait-any contract](https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-waitformultipleobjects) selects the lowest array index when several objects are already signaled. The WitOS event-only implementation follows that selection rule. Closing a pending event has a deliberate WitOS cancellation result; Windows does not define that race as a portable application behavior.

## Kernel contract

WIT_CALL_EVENT_WAIT_ANY_UNTIL (30) accepts a user handle-array address, count and absolute monotonic deadline. Count must be 1..4. Zero polls; all-ones is infinite; other deadlines must fit the existing monotonic domain. On success the result register contains the winning index. Failure/timeout results are zero.

With interrupts disabled, the kernel validates and copies the complete array, checks every handle's generation, event type and WAIT right, and rejects duplicate handles before consuming any signal. Readonly input arrays are accepted. An invalid tail, invalid later handle or duplicate cannot consume an earlier signaled auto-reset event.

If no event is ready and the deadline has not expired, the kernel copies the generation-bearing handles into the selected thread's own bounded wait record and publishes its waiting state. Caller memory is not retained across the wait. Later changes to the source array cannot redirect the wait.

Single-event and multi-event waiters share the same wait-order sequence. Setting an auto-reset event completes one oldest matching waiter; manual-reset signals complete all matching waiters. Completion publishes status/index and clears the whole wait record before making the thread ready. Closing a watched event cancels a still-pending wait; close/reuse cannot overwrite a completion that has already won.

Both deadline domains are expired before later signal/close syscalls. A parked monotonic WaitAny is not a join edge or a legacy tick wait. The existing idle/timer return path remains unchanged. No extra handles, backing pages or event objects are allocated by waiting.

## PAL behavior

PalCompatibleWaitAny delegates to the new syscall and returns WAIT_OBJECT_0 + index, WAIT_TIMEOUT or WAIT_FAILED. It reuses the existing upward-rounded/saturating millisecond-to-monotonic conversion. Success and ordinary timeout preserve native last-error; failures use the existing error mapping. Null/empty inputs are rejected; counts above four, alertable waits and reentrant waits explicitly return unsupported errors.

This supplies the one/two-event call shape needed by the finalizer. It does not start the finalizer, implement low-memory notifications, attach ThreadStore records or provide a collector.

## Validation

Seven new required user groups cover:

- Polling, lowest-index selection, manual-reset persistence, a four-event list, invalid/duplicate handles, bad counts/flags/deadlines and a readonly array crossing into an uncommitted page. A subsequent valid wait proves rejected calls did not consume the stored signal.
- Auto-reset handoff to two blocked waiters, one per signal.
- Manual-reset completion followed by close/reuse, with already-published winners preserved and later signals left available.
- Close cancellation, stale-generation protection and full join/reap cleanup.
- Monotonic timeout followed by a later signal. A deterministic kernel-state test using a real loaded component additionally proves equality at the deadline, separation from legacy ticks and preservation of the timed-out completion before a later signal. The VM timed-wait test accepts an immediate timeout if the deadline elapsed before parking.
- Snapshot isolation after the caller changes its input array while blocked.
- A shared queue between single-event and multi-event waiters, plus a blocked one-event WaitAny.

The tests run through actual guest PAL calls, with plain/static-TLS images and relocation coverage. They check wake/close/timeout counts, exact thread joins/reaps, resource accounting and complete page recovery. Ordinary boots now require 170 user groups and 51 contained hardware faults; runtime-config has 185 groups including its fifteen dedicated groups. The suite retains nineteen VM scenarios.

Local Release build, 58-file source audit, hosted NativeAOT, runtime-target/source, runtime-config at 128/512 MiB and all nineteen VM scenarios passed. A final normal boot also passed after adding the deterministic deadline-boundary check. The full native archive still has 83 members; its strict link now reports 94 unresolved symbols, including fifteen PAL requirements. The implemented WaitAny symbol is required to resolve.

## Remaining boundary

The prototype still has four event slots, eight handles and four threads per component. General mixed-object waits, wait-all, APC/reentrant behavior, low-memory event generation and a full Windows compatibility layer remain absent. The next work remains actual finalizer/ThreadStore lifecycle, GC startup and exception/root-enumeration integration above the tested native primitives.

Version 0.0.33 adds [real memory-pressure event generation](NativeAot-Memory-Pressure.md); actual finalizer/GC lifecycle remains pending.
