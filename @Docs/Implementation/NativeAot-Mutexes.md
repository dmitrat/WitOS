# ADR 0011: Recursive native runtime mutexes and thread identity

**Status:** Implemented in WitOS 0.0.14.
**Date:** 2026-09-20.
**Scope:** Native minipal mutexes, Release Crst and user ABI v8. The collector and managed runtime do not execute in the guest.

## Upstream contract and decision

The pinned [minipal header](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/native/minipal/mutex.h) requires recursive blocking locks with boolean initialization and void enter/leave/destroy operations. Its Windows implementation uses critical sections. The actual GC CLRCriticalSection and runtime Crst wrappers depend on this interface.

Implement these functions in src/Runtime.NativeAot/mutex.witos.cpp using a component-private registry and the tested kernel thread/event mechanisms. Keep the upstream declaration and its layout unchanged. A mutex's address identifies its registry entry; the Windows CRITICAL_SECTION fields are not interpreted or used as an OS object. The adapter needs no native heap, compiler TLS or dynamic static initialization.

A spin-only mutex could prevent a preempted owner from making progress on one CPU. A new kernel mutex object is unnecessary for this slice: existing auto-reset events provide blocking and persistent wake signals. A registry also permits duplicate initialization and copied-object misuse to be rejected without reading uninitialized object bytes.

## ABI v8: current-thread identity

Call 25, ThreadCurrent, takes three zero arguments and returns the calling thread's existing generation-bearing handle. It does not allocate or duplicate a handle, extend lifetime or grant new rights. Nonzero arguments return INVALID_ARGUMENT and a zero result.

The kernel selects identity from its current component/thread records, independent of the writable raw FS TLS hint. A live thread cannot close its own handle; the existing exit/join/close rules remain. Reusing a thread slot produces a different token. Mutex owners use the full token, never a slot number or a caller-controlled TLS field.

Startup remains 24 bytes; ImageInfo and the 96-byte memory snapshot retain their existing layouts. This remains an experimental contract.

## Ownership, waiting and lifetime

There are sixteen live registry entries per component. Initialization of a null pointer, an already initialized address or a seventeenth mutex returns false. A successful initialization clears the object bytes and publishes an empty entry. An uncontended mutex consumes no kernel event or backing allocation.

A private gate protects owner, recursion depth, waiter count and event handle. It uses the existing x64 atomic helpers; gate contenders yield so the owner can resume. This is the existing one-CPU, baseline x87/SSE profile, without new AVX/XSAVE assumptions.

Enter acquires an unowned mutex, increments depth for its current owner, or lazily creates an auto-reset event and registers a contender. It captures the generation-bearing event handle and releases the gate before parking indefinitely. Final Leave clears ownership and signals a contender. Recursive Leave does not release the mutex early.

An auto-reset event retains a signal if release precedes parking. Waiter accounting includes a signaled thread until it reacquires the gate. After waking, the adapter revalidates the entry/handle and keeps the gate through acquisition or re-registration. Destroy cannot reuse the entry in that interval. The gate is never held across an event wait.

Destroy requires no owner, recursion or outstanding entrants. It closes the lazily created event and frees the registry slot. The event is retained until Destroy, even after contention stops. Contended mutexes therefore share the component's four-event/eight-handle quotas with GC and other user events.

Wrong-owner Leave, uninitialized/copied-object use, destruction while active, depth/waiter overflow, and unexpected syscall or lazy-event allocation failure terminate the component through the native fail-fast path. The void upstream operations have no recoverable error return. The gate and all component-private state disappear on component teardown.

Callers must externally serialize initialization/destruction against use, keep the object at a stable address, and release owned locks before exiting a thread. There is no abandoned-owner recovery, timeout/cancellation, lock-rank checking or FIFO/fairness guarantee. A newly arriving thread may acquire before a signaled contender. These are bounded native runtime locks, not managed Monitor or multi-object waits.

## Checked Release Crst

Pinned [Crst.cpp](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/nativeaot/Runtime/Crst.cpp) ignores the boolean result of minipal_mutex_init. Its unchanged [Crst.h](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/nativeaot/Runtime/Crst.h) implements InitNoThrow by calling void Init and then returning true.

crst.witos.cpp checks initialization and fails fast on exhaustion, so InitNoThrow cannot report success for a missing lock. Its existing signature cannot return a recoverable failure through this wrapper. Crst, CrstStatic and the actual upstream holder classes otherwise retain their Release behavior. Debug/DAC compilation is explicitly rejected; this is not a port of debug owner/rank diagnostics.

## Source archive integration

The clean pinned .NET 10.0.8 source tree is unchanged. The WitOS CMake overlay replaces Crst.cpp in Runtime.WorkstationGC and mutex.c in aotminipal, alongside the existing GC environment adapters. Windows reference and WitOS builds remain in separate directories. The source audit now verifies 35 files, including mutex.c, Crst.h and Crst.cpp; source/package revisions remain unchanged.

The local Workstation archive contains 69 members and aotminipal contains 11. Compile-command and member inventories verify source selection and exclusion of the old objects. All four Workstation adapter objects and the minipal mutex object occur exactly once, verified byte-for-byte against their compiled inputs.

Strict linking still fails for the incomplete port: 142 unresolved symbols, including eight GC requirements, four guest transport symbols deliberately excluded from this broad link, and 130 remaining platform/runtime requirements. The four Windows critical-section imports and minipal mutex entry points must no longer be unresolved. The 146-to-142 change is dependency evidence, not a compatibility percentage.

The source-built Windows reference still executes real GC/exception/TLS checks. Neither the WitOS source archive nor these native guest tests establish managed execution.

## Validation

Release build, runtime-audit, runtime-probe, runtime-source (which also runs runtime-target), and the full test command passed locally. The VM suite has eighteen scenarios and requires 113 user groups and 34 contained hardware user faults in successful boots. Seven new groups cover:

| Group | Evidence |
| --- | --- |
| GcThreadIdentity | Writable TLS hint cannot change identity; repeated queries consume no handles; reused slots receive different tokens |
| GcMutexRecursive | Recursion, duplicate/null initialization, destroy/reinitialize, actual minipal holder and GC critical-section wrappers; relocated image also passes |
| GcMutexBlocking | Contender really parks/wakes; partial recursive release preserves ownership; protected data survives handoff |
| GcMutexStress | Two workers yield while recursively owning one mutex; protected state remains exclusive and all increments survive |
| GcMutexCapacity | Sixteen live mutexes, rejected seventeenth, repeated reuse and unchanged allocator/event resources |
| GcCrst | Actual upstream Crst/CrstStatic/holder classes execute through the checked adapter |
| GcMutexFailFast | Wrong-owner release, active destruction, shallow copy, Crst pool exhaustion and contention with no event capacity terminate at the intended boundary; a fresh component recovers |

Kernel checks require real child creation/join/reap and event park/wake counters, expected exit outcomes and complete physical-page recovery. The local native fixture is 18,944 bytes with 54 plain unwind entries and no OS/CRT imports. Sizes and symbol counts may vary with the compiler.

## Next work

Use the remaining source-link inventory to connect runtime TLS/thread attachment and native allocation, complete memory/reset semantics, then runtime GC coordination and fault/unwind support. Raise quotas and image/stack limits against actual runtime requirements. M3 still requires a real NativeAOT component executing managed allocations, collection, exceptions and thread activity inside WitOS.
