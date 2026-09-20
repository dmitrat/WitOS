# ADR 0019: Per-thread native last-error

**Status:** Implemented in WitOS 0.0.22; user ABI v13.
**Date:** 2026-09-20.
**Scope:** Real 32-bit per-thread last-error storage and diagnostics for the implemented PAL surface. PAL/runtime initialization, managed P/Invoke-error state, ThreadStore and guest .NET execution remain pending.

## Contract and placement

The pinned Windows PalInline.h implements PalGetLastError/PalSetLastError through GetLastError/SetLastError. NativeAOT thread and GC-transition code saves/restores this value. Microsoft's [last-error contract](https://learn.microsoft.com/windows/win32/debug/last-error-code) and [SetLastError declaration](https://learn.microsoft.com/windows/win32/api/errhandlingapi/nf-errhandlingapi-setlasterror) define state belonging to the calling thread.

Use the existing raw FS block, which is available before compiler TLS and C++ initialization. ABI v13 reserves offset 24 for a 32-bit native error value, leaves offsets 28-31 reserved, and moves application storage from offset 24 to offset 32. Self pointer, thread identity hint and initial argument remain at offsets 0, 8 and 16. New/reused threads start at zero because their raw pages are zero-filled. Existing binaries must honor the ABI version change.

The word is caller-writable user state. It grants no permissions, identifies no kernel object and does not alter syscall statuses. Kernel code neither reads nor writes it as part of ordinary syscalls. Separate thread FS bases provide independent values across scheduling; kernel supervision still uses zero FS/GS bases.

## Real x64 bindings

`Kernel.Arch.X64/native_error.asm` implements DWORD loads/stores against the kernel-selected FS base. All 32 bits are preserved, including application-defined and high-bit values. No allocation, dynamic TLS initialization, lock, syscall or Windows implementation is required.

Both direct GetLastError/SetLastError symbols and the __imp_GetLastError/__imp_SetLastError pointer symbols resolve locally. The pointer bindings are read-only image data targeting the same executable implementations; they do not create a PE import directory or permit general Windows DLL loading. Private native aliases exercise the direct code path alongside the actual unchanged upstream PAL inlines and SDK import-style calls.

The guest and source-build assembly includes derive the offset from user_abi.h. The source profile generates its include under its own build directory and verifies the exact MASM object inside Runtime.WorkstationGC. No upstream header or implementation file is edited.

## Implemented PAL error policy

Explicit SetLastError changes the value. Successful implemented PAL operations preserve it, as does a normal WAIT_TIMEOUT result. Unsupported alertable waits fail before consuming a signal. Failure paths record an error before returning null/false/WAIT_FAILED; invalid void free records the error before failing fast. Native output-pointer validity remains the caller's responsibility.

| Failure | Stored value |
| --- | --- |
| Unsupported mode, named event, security attributes or alertable wait | ERROR_NOT_SUPPORTED |
| Invalid size/overflow, output arguments or callback | ERROR_INVALID_PARAMETER |
| Bad/stale/wrong-kind/closed handle | ERROR_INVALID_HANDLE |
| Denied rights | ERROR_ACCESS_DENIED |
| Invalid/unreserved/uncommitted dynamic address | ERROR_INVALID_ADDRESS |
| Resource or start-record exhaustion | ERROR_NOT_ENOUGH_MEMORY |
| Busy handle | ERROR_BUSY |
| Kernel deadlock status | ERROR_POSSIBLE_DEADLOCK |
| Error-path timeout status | ERROR_TIMEOUT; normal event timeout preserves the old value |
| Unexpected kernel failure / invalid thread snapshot | ERROR_GEN_FAILURE |

Memory allocation rollback uses raw cleanup syscalls, preserving the original allocation failure. Worker creation errors affect only the creator; constructors, callbacks and TLS cleanup run with the worker's own error word. This policy applies to the implemented WitOS PAL subset, not to every Win32 API or every success-case convention.

## Guest evidence

Three additional required groups cover:

- NativeLastError: zero initial state, direct/native/import/PAL roundtrips for full-width values, preserved adjacent header/reserved bytes, two timer-preempted workers, parent isolation, idle wakeup and zero state after slot reuse. Runs without compiler TLS, with it and relocated.
- PalErrorCodes: invalid/unsupported memory requests, quota failure with rollback, valid memory/protection/free preservation, unsupported named/alertable events, signal preservation, normal timeout preservation, stale/wrong handles, busy current-thread handle, invalid stack-bound outputs and event-capacity recovery.
- LastErrorBindingProtection: a real write fault against the local import pointer binding, checked against the image mapping and hardware vector/error/address/selectors, followed by recovery.

The existing detached-worker fixture additionally checks zero error in real C++ TLS constructors, preserved callback values through waiting/destruction, parent success preservation and errors from invalid or capacity-limited worker creation. No error state is shared with the parent.

The supervisor requires real preemption, child joins/reaps, idle, unchanged owned memory and full reclamation. Successful boots require 150 user groups and 50 contained user faults. Release build and runtime-port passed, as did the 41-file audit, hosted probe and full runtime-source/runtime-target Windows reference. The complete sequential VM rerun passed all 18 scenarios, with 150 required user groups and 50 contained user faults in successful boots. An initial attempt stopped on a host cl.exe timeout while compiling unchanged exceptions.c; neither compiler/VM timeouts nor assertions were relaxed for the successful rerun.

## Remaining boundary

The Workstation archive has 76 members, including the verified native_error.asm and pal_error.witos.cpp objects. The strict link resolves all four GetLastError/SetLastError direct/import symbols and leaves 110 unresolved symbols: seven GC environment, 22 PAL, five deliberately excluded native transport/startup and 76 other runtime/platform requirements.

PalInit remains unresolved: the pinned implementation also initializes GC configuration and environment state, and the whole runtime bootstrap is not yet ported. This change supplies required diagnostics without synthetic successful initialization. Module/handle services, GC coordination, actual runtime startup and ThreadStore lifecycle remain work to do.
