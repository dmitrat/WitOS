# ADR 0017: PAL memory, events and waits

**Status:** Implemented in WitOS 0.0.20; user ABI v11.
**Date:** 2026-09-20.
**Scope:** Ten additional NativeAOT PAL functions execute in the guest against unchanged pinned Pal.h declarations. No managed runtime, collector or ThreadStore lifecycle is claimed.

## Memory contract

PalVirtualAlloc reserves a 64 KiB-aligned dynamic range and commits its page-rounded extent. Zero length, arithmetic overflow and unsupported protection values return null. A failed commit rolls back additions in the kernel, and the adapter releases its unpublished reservation. Successful allocations are zero-filled.

PAGE_READWRITE, PAGE_READONLY and PAGE_NOACCESS map to the kernel's RW/NX, RO/NX and committed no-access policies. Executable, copy-on-write, guard and cache modifiers are explicitly rejected. No W+X mapping or executable-memory fallback is introduced.

PalVirtualProtect accepts a byte range and rounds its start down/end up, with overflow checks. The complete rounded range must lie in one committed dynamic reservation. Kernel validation preserves all pages/protections on failure. Protection changes retain bytes and commitment. Fixed image/stack protection remains unsupported, including future readonly runtime-cookie/thunk use cases.

PalVirtualFree ignores size, matching the selected Windows PAL's whole-reservation release contract, and requires the exact live base. Its void signature cannot report failure: invalid/null/interior/stale release fails the component rather than hiding ownership errors. Clients must not release another subsystem's reservation merely because it shares the component address space.

## Events and waits

PalCreateEventW supports unnamed events without Win32 security/inheritance attributes. Nonnull names/attributes fail. Boolean arguments use ordinary nonzero truth. HANDLE values are opaque generation-bearing WitOS tokens, never dereferenced pointers.

PalSetEvent and PalResetEvent use the kernel's typed event validation. PalWaitForSingleObjectEx supports one event and non-alertable waits: zero polls, INFINITE parks indefinitely, and finite millisecond waits use absolute HPET deadlines with upward rounding and saturation. It returns WAIT_OBJECT_0, WAIT_TIMEOUT or WAIT_FAILED. Alertable waits fail before consuming a signal. Thread handles and other object kinds cannot be waited through this event-only function.

PalCloseHandle closes supported WitOS handle types through existing ownership checks. Closing a waited event cancels its parked waits; those calls return WAIT_FAILED. Reusing the slot creates a new token and cannot complete an old wait. The adapter does not implement Win32 GetLastError/SetLastError, named objects, APC delivery or multi-object/reentrant waits.

PalSleep uses monotonic deadlines; zero yields and INFINITE parks. PIT still limits wake scheduling. These operations share the existing four-event/eight-handle component quota and may fail under resource pressure.

## ABI v11: truthful yield result

ThreadYield, call 10, now returns result 1 if that invocation selected a different thread, otherwise 0. Status remains OK for a successful yield operation. The kernel writes the result into the yielding thread's saved context before returning to the selected context. Earlier callers that ignore the result retain their behavior.

PalSwitchToThread reports that real result. It does not return a synthetic success simply because the syscall succeeded. The startup structure, thread-information schema and other syscall layouts remain unchanged.

## Guest evidence

Eight new required groups cover:

- PalMemory: protection modes, rounding, byte preservation, output accounting, overflow/unsupported rejection and whole-allocation release; also runs relocated.
- PalMemoryRollback: commit-quota failure and reservation exhaustion leave snapshots unchanged; live data survives and later allocation recovers.
- PalEventState: manual/auto signals, reset/poll, unsupported parameters, alertable rejection without signal consumption, wrong/stale handles, quota exhaustion and repeated reuse.
- PalEventHandoff: two real waiters demonstrate single-consumer auto reset and all-waiter manual reset. A controlled runnable-worker handshake verifies true yield, and no-peer yield returns false.
- PalWaitTime: finite sleep and wait use monotonic lower bounds and kernel idle; an already signaled event handles the maximum finite timeout.
- PalCloseCancellation: close wakes a genuinely parked waiter with failure, immediate slot reuse cannot revive the old token, and the replacement remains usable.
- PalMemoryProtection: hardware-enforced RO write, no-access read, NX execution and read-after-free faults, followed by recovery.
- PalFreeFailFast: an interior release fails at the intended boundary and a fresh component recovers.

The supervisor verifies actual park/wake/close/timeout counts, thread creation/join/reap and full resource reclamation. Successful boots require 142 user groups and 47 contained user faults. Release build, initial runtime-port, the 41-file audit, hosted probe and full source-build/runtime-target checks passed. The full VM suite passed all 18 scenarios, with 142 required user groups and 47 contained user faults in successful boots, including both RAM profiles and the expected timeout.

## Source-build boundary

The Workstation archive has 72 members with pal_memory.witos.cpp and pal_events.witos.cpp verified byte-for-byte. Both share the actual pinned Pal.h declarations. The Windows reference remains separate and passed all four execution groups.

The diagnostic workload now leaves 116 unresolved symbols: seven GC environment, 24 PAL, five deliberately excluded guest transport/startup and 80 other runtime/platform requirements. Nine previously unresolved PAL entries resolve; PalVirtualProtect is implemented/tested too but was not rooted as an unresolved requirement by that workload. This remains an incomplete port.

Next complete the startup/handle/thread/last-error and GC coordination requirements exposed by the PAL inventory, then initialize the real runtime/collector and validate ThreadStore attachment and GC allocation-context cleanup. Executable allocation, fixed-image reprotection, multi-object waits, managed exceptions and arbitrary Windows compatibility remain outside this slice.
