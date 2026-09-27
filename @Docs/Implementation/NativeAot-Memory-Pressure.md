# Kernel memory-pressure notifications

**Status:** Implemented in WitOS 0.0.33, experimental ABI v17.
**Scope:** A kernel-controlled, wait-only low-memory event for the current component. This supplies the NativeAOT finalizer's notification dependency; it does not execute the finalizer or GC.

## Pressure policy

The kernel computes available backing headroom in pages as the smaller of eligible physical allocator free pages and the component's remaining owned-page quota. Fixed mappings, stacks, private page tables and committed no-access pages all contribute to ownership cost. Reserving virtual addresses alone does not reduce this budget.

The prototype enters low-pressure state at 16 or fewer available pages (64 KiB), and leaves it at 32 or more (128 KiB). Between those thresholds it retains its previous state. These are explicit prototype policy values for the current bounded allocator, not universal .NET thresholds. A large allocation may still fail while the event is clear; the event is advisory, not a promise that a later commit fits.

This combines global eligible-RAM pressure with component quota pressure. It deliberately differs from the [Windows memory-resource notification API](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-creatememoryresourcenotification), which describes a system-wide condition. WitOS does not claim a Win32 compatibility implementation or a high-memory notification/query API here.

## Kernel and PAL contracts

WIT_CALL_MEMORY_PRESSURE_EVENT (31) accepts three zero arguments and returns a generation-bearing event handle. The object is manual-reset and shares the existing four-event/eight-handle quotas. Its capability grants WAIT and close, with no SIGNAL right: PalSetEvent/PalResetEvent cannot override the kernel's resource state.

Each component tracks only its own notification handles. Closed generations are purged; reusing a slot for an ordinary event cannot cause the kernel to signal/reset that replacement. A notification created while pressure is already low starts signaled.

State refresh runs at serialized kernel supervision boundaries, including syscall entry/exit and scheduler/timer paths. Both deadline domains expire before notification publication. Memory operations publish only settled state, so failed commit rollback cannot produce a transient false notification. Kernel signaling uses the same Single/WaitAny completion path as ordinary events. Recovery clears the signal without changing an already-published wait result.

The common syscall return now also dispatches a caller that became Ready during the final expiration pass, instead of returning to user code with a stale thread state. Thread exit/reap reaches the same refresh through dispatch. All work remains bounded by the existing event/thread quotas and executes with interrupts disabled.

PalCreateLowMemoryResourceNotification calls the real kernel factory using the upstream declaration. It returns a handle or NULL with the existing native error mapping, preserving last-error on success. No compiler TLS, managed thread attachment, native heap allocation or user polling worker is required to create the notification.

## Validation

Four new required guest groups cover:

- Quota pressure and hysteresis: unchanged budget after reservation, an unsignaled event at 17 pages, signaling at 16, persistence at 24 and recovery at 32. Failed multi-page commit rolls back without falsely signaling. No-access protection and committed reset retain pressure because backing remains owned. User Set/Reset attempts are denied.
- A blocked WaitAny over an ordinary event and the pressure event wakes with the pressure index. Close/reuse preserves the old wait's cancellation; a recycled ordinary event remains user-controlled. Joins, reaps and event wake/close counters are checked.
- Event capacity, failure without a leaked capability, close/recreate and stale-handle rejection.
- Physical pressure in an isolated allocator backed by 80 real borrowed pages. This run verifies that physical availability, not the larger component quota, is the limiting budget; all borrowed frames are returned.

Notification creation during an existing low condition, repeat waits on a persistent signal, invalid syscall arguments, memory/handle/event accounting and full component teardown are also checked. The normal PAL image requires no compiler TLS. The ordinary suite now requires 174 user groups and 51 contained hardware faults across nineteen VM scenarios; runtime-config requires 189 groups including its fifteen dedicated groups.

Local Release build, 58-file source audit, hosted NativeAOT, runtime-target/source and the 128/512 MiB dedicated boots passed. All nineteen kernel regression scenarios also passed locally. The full runtime archive remains at 83 members and now has 93 strict-link unresolved symbols: five GC environment, fourteen PAL, five deliberately omitted transport and 69 other runtime/platform requirements.

## Remaining boundary

Actual finalizer startup still needs the runtime thread attachment/cleanup path and the remaining platform services. GC initialization, managed object allocation/finalization, exception integration and root enumeration remain unfinished. Memory pressure can now signal a real native waiter; that is not evidence of a running managed collector.
