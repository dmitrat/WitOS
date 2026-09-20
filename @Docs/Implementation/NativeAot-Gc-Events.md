# ADR 0009: Bounded GC events and yielding

**Status:** Implemented in WitOS 0.0.12; verified locally on 2026-09-17.
**Scope:** Native GCEvent methods and GCToOSInterface::YieldThread in the guest and source-built archive. This milestone introduced the event slice on ABI v6; the later [time extension](NativeAot-Gc-Time.md) uses ABI v7 and adds finite waits. The collector and managed runtime still do not execute in WitOS.

## Context and decision

GCEvent has an opaque implementation pointer and a trivial destructor. Its upstream implementations depend on native allocation plus Windows events or pthread synchronization. WitOS already has component-local events, but no general native heap or suitable runtime elapsed-time provider.

Use four component-private static slots containing an owner pointer and a kernel event handle. The slots require no native allocation, dynamic C++ initialization or destructor registration. Keep event state, wait queues, generation checks and cancellation in the existing kernel. The runtime adapter does not implement a second wait scheduler.

A native heap allocation per event would introduce another bootstrap dependency and consume scarce backing pages/reservations. Retaining leaked wrappers forever would make the prototype fail after a few lifetimes. A bounded reusable pool supports the current kernel's four-event quota and makes failure/reuse executable. A larger runtime profile will need larger tested quotas or a native allocator.

## Initial 0.0.12 contract (finite waits extended in 0.0.13)

The adapter compiles against the unchanged [upstream GCEvent declaration](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/gc/env/gcenv.os.h).

| Operation | Current behavior |
| --- | --- |
| Constructor / IsValid | Constructor clears the pointer; upstream inline IsValid reports whether it is non-null |
| Four Create methods | Manual/auto reset, chosen initial state; OS-prefixed variants have the same behavior because there is no hosting API; duplicate creation or pool/kernel exhaustion returns false |
| Set | Signals the kernel event: one waiter/stored signal for auto reset, all waiters/persistent state for manual reset |
| Reset | Clears the signal for either event kind |
| Wait(0, ...) | Poll; returns WAIT_OBJECT_0 or WAIT_TIMEOUT |
| Wait(INFINITE, ...) | Real kernel parking; returns WAIT_OBJECT_0 on signal, WAIT_FAILED on invalid/closed handle |
| Other finite waits | Return WAIT_FAILED before consuming a stored signal; millisecond timing is not implemented yet |
| CloseEvent | Closes the kernel handle, clears the object's pointer and releases its pool slot |
| YieldThread | Uses the real user-thread yield syscall; the upstream spin-count hint is unused |

Reset follows the actual pinned [Windows GC environment](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/gc/windows/gcenv.windows.cpp), which calls ResetEvent for either kind. The header's auto-reset no-op comment does not match that implementation. Microsoft's [ResetEvent contract](https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-resetevent) defines clearing the specified event; the guest tests cover this choice explicitly.

The alertable argument is ignored, as in both pinned upstream GC backends; this does not implement APCs or managed cancellation. The negative performance-clock link check remains. Delivered PIT ticks are not relabeled as milliseconds.

## Synchronization and lifetime

A component-private, nonrecursive gate serializes slot ownership, handle snapshots and nonblocking create/set/reset/close calls. Its acquire compare-exchange and release store live in `Kernel.Arch.X64/native_start.asm`, with the MSVC external-call compiler boundary and x64 ordering contract. No AVX/XSAVE or compiler TLS is introduced. Contenders call YieldThread, allowing a preempted owner to resume on one CPU.

Wait copies the generation-bearing handle while holding the gate, releases the gate before entering the kernel, then never dereferences its old slot again. Close can cancel a parked waiter and reuse that slot immediately. A wait that captured the old handle cannot target the new event. If a wait starts after recreation, it legitimately observes the new event; the overlap test accepts both linearizations without assuming a particular timer schedule.

Creation publishes the object pointer only after successful kernel creation. A failed kernel allocation leaves no owned slot. Closing clears both pointer and slot metadata. The private owner check rejects a shallow copy from operating another object's event, including after slot reuse. Event objects must not be copied or destroyed while live; callers must close them or terminate the component. IsValid is the unchanged plain inline pointer read, so creation/closure/destruction must be externally synchronized against that query. Concurrent normal Set/Reset/Wait and close-versus-wait use the internal gate.

Invalid Wait returns WAIT_FAILED. Invalid void Set/Reset/Close operations, or unexpected transport failure during a void operation, terminate the component with native fail-fast exit `0xFFFF0001`. They cannot silently report success. Kernel teardown closes remaining handles and destroys all component-owned memory, including live worker stacks. Other components continue.

## Validation and evidence

Release build, runtime-port, all 17 VM scenarios, source audit, hosted probes and the full runtime-source command passed locally. Successful boots now require 100 user check groups and 33 contained user faults. Seven new groups cover:

- Initial/manual/auto state, repeated Set coalescing, Reset on both kinds, unsupported finite waits preserving a signal, copied-object rejection, close/recreation and relocation to a second image base.
- Kernel event exhaustion while pool capacity remains, rollback of failed creation, pool exhaustion and repeated reuse without backing-page consumption.
- Two native waiters on a manual event, persistent signals and joined/reaped worker resources.
- Auto-reset handoff that permits exactly one completion per signal.
- Close/recreation overlapping an infinite wait without stale-handle retargeting.
- A forced native-lock contender that yields to its owner, followed by two threads concurrently creating, consuming and closing events.
- Fail-fast from a copied object's Set while live event/thread resources exist, followed by full teardown and a successful replacement component.

The local fixture is 12,800 bytes with 32 plain unwind records and no Windows/CRT imports. The source-built workstation archive contains 68 members; both adapter objects are verified byte-for-byte. The strict link has 150 unresolved symbols: 12 GC environment requirements, four already-implemented WitOS glue symbols deliberately not linked into that boundary test, and 134 other platform/runtime dependencies. GCEvent and YieldThread must resolve. These counts are diagnostics, not a compatibility percentage.

## Remaining work

The [monotonic clock and finite GCEvent waits](NativeAot-Gc-Time.md) are now implemented. Recursive [minipal/Crst locks](NativeAot-Mutexes.md) are now implemented. Next connect TLS/thread attachment, GC rendezvous and fault/unwind integration. The private gate is not a port of minipal_mutex or CLRCriticalSection. Pool/kernel quotas remain prototype limits. Full NativeAOT initialization and managed execution remain the M3 acceptance gate.
