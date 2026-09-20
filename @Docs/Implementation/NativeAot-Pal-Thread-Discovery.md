# ADR 0016: WitOS PAL boundary and current-thread discovery

**Status:** Implemented in WitOS 0.0.19; user ABI v10.
**Date:** 2026-09-20.
**Scope:** Four real NativeAOT PAL functions and an atomic current-thread snapshot. ThreadStore attachment, collector execution and managed .NET remain pending.

## Dependency finding

The pinned runtime's `Thread::Construct` needs OS identity, stack bounds and Windows thread-handle operations. `ThreadStore::DetachCurrentThread` removes the thread under its lock, then `Thread::Detach` calls the real GC through `FixAllocContext`. `RuntimeThreadShutdown` also observes process shutdown. Therefore a complete attach/detach demonstration cannot honestly be isolated from collector/runtime initialization using successful placeholder functions.

First implement the required thread facts and expose the remaining real PAL contract. Next supply PAL memory/event/wait/bootstrap behavior, bring up the actual GC/runtime initialization path, and validate ThreadStore lifecycle against it. The C++ TLS destructor mechanism is necessary but does not itself complete this lifecycle.

## ABI v10: ThreadQuery

Call 27, `ThreadQuery(buffer, exact size, version)`, returns a 56-byte `WitUserThreadInfo` v1 snapshot for the calling thread. No handle is allocated or duplicated.

| Field | Meaning |
| --- | --- |
| Version / Size | Schema 1, exactly 56 bytes |
| ThreadId | Kernel-selected generation-bearing thread handle, borrowed identity |
| StackLow / StackHigh | Dedicated stack range, low-inclusive/high-exclusive; guard pages excluded |
| RawTls | Calling thread's fixed FS storage address |
| CompilerTls | Optional static compiler TLS/GS page, zero for non-TLS images |
| ProcessId | Kernel-owned component identity |
| ProcessorCount | One online processor in the supported backend |

The kernel copies its own thread/process records with interrupts disabled. It validates the entire destination before any write. Unsupported version returns UNSUPPORTED; wrong size returns INVALID_ARGUMENT; inaccessible, readonly, overflowing or partly unmapped destinations return BAD_ADDRESS. The result register is 56 on success and zero on failure. Identity/bounds are independent of writable FS hints, GS control data and caller-supplied addresses.

Startup remains 24 bytes; image and allocator snapshot layouts are unchanged. ABI v10 exposes the added syscall rather than changing the existing fields.

## Actual PAL functions

`pal.witos.cpp` includes unchanged, hash-verified upstream `Pal.h` and its declaration dependencies. It implements:

- `PalGetCurrentOSThreadId`: uses the existing current-thread identity syscall; discovery failure is fatal.
- `PalGetMaximumStackBounds`: uses ThreadQuery; validates schema/bounds before assigning outputs. Null or aliased output pointers are rejected with false and no output mutation. Valid output pointers remain the native caller's responsibility.
- `PalGetCurrentProcessId`: obtains the kernel component ID.
- `PalGetProcessCpuCount`: returns the kernel's online processor count for this profile.

No Windows VirtualQuery, TEB stack fields, FLS, CRT imports or heap allocation are used. The source audit now pins 41 files, adding CommonTypes/CommonMacros/PalLimitedContext/rhassert/PalInline and minipal GUID declarations without changing the .NET commit/package pins. Windows SDK types remain compilation declarations only.

## Source-build platform boundary

The WitOS overlay removes `windows/PalCommon.cpp` and `windows/PalMinWin.cpp` from Runtime.WorkstationGC and adds the deliberately partial WitOS PAL. The independent Windows reference retains both upstream sources. Build checks verify both source/member selection and the exact adapter object in the archive.

Unimplemented PAL functions such as PalInit, PalAttachThread, virtual memory, event/wait, thread startup and context/fault operations remain unresolved. Windows inline/direct calls elsewhere in the runtime/CoreLib also remain; this change does not claim a complete PAL or remove every Windows assumption.

The Workstation archive has 70 members (two Windows objects removed, one WitOS object added). The strict link now exposes 125 unresolved symbols: seven GC environment requirements, 33 NativeAOT PAL methods, five deliberately excluded guest transport/startup functions and 80 other platform/runtime requirements. This inventory is not directly comparable with 0.0.18's 134 as a progress percentage: dependencies have moved from Windows implementation internals to the explicit PAL interface. Only four PAL functions were implemented here.

## Guest evidence

The fixtures compile against the same pinned upstream declarations as the source adapter. PalPlainFixture has no compiler TLS; PalThreadFixture has the supported static TLS metadata. Both link without OS/CRT imports; the TLS image also executes at a relocated guest base.

Four required groups cover:

- `PalThreadSnapshot`: supervisor compares all reported facts to its own thread/process records; a local stack object lies within the reported bounds; forged FS/GS fields cannot change the facts; repeated discovery preserves resources.
- `PalThreadBuffers`: full cross-page copy, wrong size/version, null/readonly/code/supervisor/overflow destinations, and an accessible prefix followed by an unmapped page with the prefix unchanged on failure; PAL output validation is also checked.
- `PalThreadSwitching`: two timer-preempted children see their own identities/bounds, the parent remains unchanged, reused stack slots receive fresh identities, and facts survive kernel idle/wakeup.
- `PalStackGuards`: real hardware faults immediately below the reported low bound and at the exclusive high bound, with checked vector/error/address/selectors and subsequent successful recovery.

The supervisor requires actual child creation, join/reap, timer switches, idle, unchanged frame ownership and complete physical-page reclamation. Successful boots require 134 user groups and 43 contained user faults. Kernel CI uploads both fixture images and pal-build.json with the pinned declaration/input evidence.

Release build, runtime-port, the 41-file audit, hosted probe and full runtime-source (including runtime-target) passed. The source-built Windows reference passed all four execution groups. The full VM suite passed all 18 scenarios, with 134 required user groups and 43 contained user faults in successful boots, including both RAM profiles and the expected timeout.

## Next work

Use the new explicit PAL inventory to implement memory allocation/protection and events/waits needed by runtime startup and the real collector. Then connect actual ThreadStore attachment/shutdown and GC allocation-context cleanup. Keep unsupported operations unresolved and preserve separate evidence for native guest adapters, hosted runtime execution and eventual managed guest execution.
