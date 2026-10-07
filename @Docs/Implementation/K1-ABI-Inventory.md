# K1 — The ABI-1 inventory in code

Plan step K1 applies [RFC 0011 v3](../RFC-0011-Kernel-Architecture-and-ABI.md) sections 7 and 8 to the kernel: the
calls of the target set get their final numbers and names, the calls the RFC merges disappear, the calls it removes
go, and the calls that leave for the system layer at K8 move to a transitional range. The step has four slices; this
document records each as it lands. [ABI-Reference.md](ABI-Reference.md) describes the resulting ABI call by call and
is checked against the headers by a host test.

## K1.1 — The layout (ABI v52)

### What changed

**Numbers and names.** `user_abi.h` now carries the layout of RFC 0011 §7: kernel and handles at 0–4, memory at
10–17, threads at 30–40, events, waits, time and entropy at 50–57, faults at 60–63, processors at 93–94, with the
gaps the later steps fill (18–20 and 33 at K5, 41 at K7, 70–72 at K2, 80–85 at K3, 90–92 at K5). The calls of the
current implementation that RFC 0011 §8 merges, removes or moves later sit at 200–222 and are retired by the step the
header names beside each; a retired number is never reused. This is the last renumbering (RFC 0011 §10.1).

**Merged in this slice.** `WRITE` and `CONSOLE_WRITE` became `DEBUG_WRITE(handle, buffer, length)` with the 64 KiB
limit `WIT_DEBUG_WRITE_MAX`; `EVENT_CREATE` and `EVENT_CREATE_RIGHTS` became `EVENT_CREATE(flags, rights)` where
rights 0 means `WAIT` and `SIGNAL`; `MONOTONIC_READ` and `MONOTONIC_FREQUENCY` became `CLOCK_READ(clock)` and
`CLOCK_FREQUENCY(clock)` with `WIT_CLOCK_MONOTONIC` the one clock until K6 (`WIT_CLOCK_UTC` is `UNSUPPORTED`);
`EVENT_WAIT_UNTIL` and `EVENT_WAIT_ANY_UNTIL` folded into `OBJECT_WAIT`, the one wait; `EXCEPTION_UNWIND` folded into
`EXCEPTION_CONTINUE`, which takes the transfer request (`WitUserExceptionTransfer`, 736 bytes) whose
`RetireThroughToken` is the current token for a plain continuation; `THREAD_REFERENCE_DUPLICATE` is `HANDLE_DUPLICATE`
(thread handles today; events follow in K1.2), `CPU_CONTEXT_QUERY` is `CONTEXT_PROFILE`, `APC_QUEUE` is
`THREAD_ACTIVATE` (still queued; K1.3 changes the delivery), `THREAD_CREATE_REFERENCE` is `THREAD_CREATE`, `EXIT` is
`PROCESS_EXIT` and `CLOSE` is `HANDLE_CLOSE`.

**Removed in this slice.** The delivered-tick clock domain: the tick `CLOCK_READ` and `CLOCK_FREQUENCY`,
`THREAD_SLEEP` and `EVENT_WAIT`, with the per-thread `MonotonicWait` flag and the second expiry pass of the kernel.
Every deadline is now an absolute monotonic count and the timer tick only advances the one expiry pass.
`THREAD_CURRENT` went: the constant `WIT_THREAD_SELF` names the calling thread wherever a thread handle is taken, and
its identity comes from `THREAD_QUERY`.

**Query.** `QUERY` returns `WIT_ABI_VERSION` in the low 32 bits and the mask of the families present
(`WIT_ABI_FEATURE_*`) in the high 32 bits; the mask is zero until K2 adds channels.

**Statuses and rights.** `APC_PENDING` is `INTERRUPTED` (15), the status of a wait an activation ended;
`INITIALIZATION_FAILED` (17) stays until the library family leaves at K8; 18 is reserved for `PEER_CLOSED` (K2). The
rights bits are unchanged; `JOIN` retires with `THREAD_JOIN` in K1.2.

**The frozen line's names.** `src/Kernel/include/witos/user_abi_frozen.h` defines the old names of the pure renames
(`WIT_CALL_EXIT`, `WIT_CALL_CLOSE`, `WIT_CALL_WRITE`, `WIT_CALL_THREAD_CREATE_REFERENCE`,
`WIT_CALL_THREAD_REFERENCE_DUPLICATE`, `WIT_CALL_CPU_CONTEXT_QUERY`, `WIT_CALL_APC_QUEUE`,
`WIT_CALL_EVENT_CREATE_RIGHTS`, `WIT_CALL_MONOTONIC_READ`, `WIT_CALL_MONOTONIC_FREQUENCY`, `WIT_STATUS_APC_PENDING`,
`WIT_THREAD_REFERENCE_CURRENT`) as aliases of the kernel's constants. The kernel never includes it; the frozen line's
own headers (`src/Runtime.Native/image.h`, `src/Runtime.NativeAot/unwind_scope.witos.h`, `tests/User/protocol.h`)
do, and the fixture builder resolves the aliases into `user_abi.inc` and `user_abi_a64.h`. Calls whose arguments or
meaning changed have no alias: their callers were rewritten. The header is removed with the frozen line at K8.

### Kernel

- `user_calls.c`: the table in the new layout, sparse from 200; `event_create` with the rights default,
  `clock_read` and `clock_frequency` with the clock argument, `debug_write`, `exception_continue` with the transfer
  form, `query` with the feature mask; the legacy handlers are gone.
- `user_wait.c`: one deadline domain. `wit_user_wait_expire(process, now)` completes parked waits and sleeps whose
  monotonic deadline has passed; `wit_user_wait_objects(process, handles, count, all, deadline, now, winner)` is the
  process-internal object wait on copied handles, used by the syscall and by the kernel's model tests;
  `wit_user_sleep_until` is the one sleep. The wait kinds are `Join`, `Sleep` and `Objects`.
- `user_objects.c`: `OBJECT_WAIT` copies and validates the request and the handles, returns `INTERRUPTED` for an
  alertable request with a queued activation (until K1.3) and parks through `wit_user_wait_objects`. A handle listed
  twice is invalid only in the all mode, as `NtWaitForMultipleObjects` has it; the removed any-wait rejected it in
  both, and the PAL wait-any fixture that expected that now follows Windows.
- `user_console.c`: `wit_user_debug_write` validates the handle and the whole source range, then copies in 256-byte
  steps after the `[USER] ` prefix.
- `user_exception.c`: `wit_user_exception_continue` is the former unwind: it validates the transfer request, applies
  the context and retires the current record or the selected suffix of ancestors. The diagnostic counter
  `ExceptionContinuations` now counts every continuation, the transfers to a landing pad included; the former unwind
  did not count. The runtime-boot expectations (0 and 36) are unchanged because those scenarios continue at the
  interrupted context only; the dynamic dispatch of the code fixture expects 7 instead of 3, its four `RtlUnwind`
  transfers now counted.
- `user.c`: one expiry pass per tick and per call.

### Fixtures and the frozen line

- `tests/User.X64` and `tests/User.A64`: `entry`, `image` and `threads` use the new names; `waits` is rewritten over
  `OBJECT_WAIT` and the monotonic clock. Delays are scheduler ticks of 10 ms converted through `CLOCK_FREQUENCY`
  (`deadline_ticks`), so the same fixture runs on the q35 HPET and the virt generic counter; the wait and the deadline
  arithmetic are subroutines so that the fixture stays within its one page of code. The clock scenario checks that
  the UTC clock is `UNSUPPORTED` and a third clock `INVALID_ARGUMENT`, and that a deadline beyond
  `WIT_MONOTONIC_MAX` is rejected.
- `tests/Kernel`: the wait model tests use `wit_user_wait_objects`; the former clock-domains test became the deadline
  test (`User.WaitDeadlines`); the suspend and wait-any tests follow the one domain.
- `src/Runtime.Native/bootstrap.h` gains the helpers of the frozen line over the new calls: `wit_native_wait_any`,
  `wit_native_wait_one`, `wit_native_clock_read`, `wit_native_tick_counts`, `wit_native_sleep_ticks`;
  `wit_native_thread_identity` reads the kernel record through `THREAD_QUERY`.
- The frozen PAL and its fixtures call those helpers where they used the removed calls; the exception dispatcher
  continues through the transfer request. `WriteFile` uses `DEBUG_WRITE`, which returns the count in the result
  register, so the binding validates the count's destination as a whole through `CODE_MEMORY VALIDATE` before it
  changes anything, as `CONSOLE_WRITE` validated its output pointer: the console fixture's readonly and partly
  unmapped destinations still fail with `ERROR_INVALID_ADDRESS` and keep their bytes.
- The runtime overlay (`runtime-overlay.cmake`) generates the assembly include with `WIT_CALL_FATAL_ARM` and the
  last-error offset at configure time; the header is now a configure dependency and the include changes only with
  its content, so a renumbering rebuilds the dependent assembly objects instead of leaving a stale call number in an
  incremental build directory.
- The helpers that keep a kernel record on the stack (`wit_native_wait_any`, `wit_native_thread_identity`) and the
  PAL's `PalCompatibleWaitAny` are `__declspec(safebuffers)`: under MSVC's `/GS` a record without pointers is a GS
  buffer, and the runtime-config record probe links the PAL events binding without the security cookie objects. The
  functions had no GS buffer before this slice, so their protection is unchanged; the kernel validates every record.

### Evidence

Guest acceptance of every scenario on both ISAs: `test` (x64, QEMU q35, 20 scenarios) and `test --arch arm64` (QEMU
virt, 14 scenarios) pass with the expected markers of `tests/Expectations`, the contained-fault accounting and the
release kernel. The frozen line's guest chains pass over the new ABI: `runtime-config` (four boots, the record probe
linked without a security cookie), `runtime-boot-run` (four boots, the runtime-boot protocol), `coreclr-memory` and
`coreclr-storage` (two boots each) and `coreclr-host-guest` (no unresolved external). The host tests check the call
table and the ABI reference against the header, and `format-check` is clean.

### Not in this slice

Join as a wait, the unified `THREAD_QUERY`, the merge of `THREAD_COMPLETE` and `HANDLE_DUPLICATE` for events (K1.2);
activations delivered through the fault callback and the end of `ALERTABLE` and `APC_DEQUEUE` (K1.3); the ARM64
context block and fault delivery (K1.4). The transitional calls keep their current contracts until their steps.

## K1.2 — Threads (ABI v53)

### What changed

**One creation.** `THREAD_CREATE(WitThreadCreateRequest, 48, 0)` is the one form (`WIT_THREAD_CREATE_VERSION` 1,
flags `START_SUSPENDED` and, for the frozen line's DLL lifecycle, `LIBRARY_NOTIFICATIONS`). It returns a thread handle
with every thread right; closing the handle detaches the thread, and a thread's stack, TLS and private identity are
reclaimed when it exits whatever handles remain. `THREAD_CREATE_SIMPLE` (200) and its `DETACHED` flag are retired.

**Join is a wait.** `THREAD_JOIN` (201) is retired: a join is `OBJECT_WAIT` on the thread handle, `THREAD_QUERY` for
the exit code and `HANDLE_CLOSE`. The kernel keeps no join edges, so the join-cycle check and its `DEADLOCK` result
leave with it (`WIT_STATUS_DEADLOCK` stays for the DLL lifecycle until K8); two threads waiting on each other park
until the component's budget ends, as two POSIX threads joining each other would.

**One exit.** `THREAD_COMPLETE` (202) is retired and `THREAD_EXIT(code, 0, 0)` is the one exit. The kernel no longer
ends a full-runtime component whose thread exits "raw" (RFC 0011 §8: the coordinated-profile distinction is layer
2's lifecycle); the DLL lifecycle's own check (a thread that still owes its library notifications, or owns the
lifecycle, ends the component with `WIT_PROCESS_ABRUPT_THREAD_EXIT`) stays until K8. The counter of orderly
completions became the count of thread exits (`ThreadExits`; the runtime-boot log line is `Runtime thread exits: N`),
and the capacity-failure counter covers every creation: the runtime-boot scenario reports six (the four managed
`Thread.Start` failures of the quota test and the two native creations the runtime attempts while the quota is
exhausted) where it reported the four managed ones.

**One query.** `THREAD_QUERY(thread handle or WIT_THREAD_SELF, buffer, 96)` copies `WitUserThreadInfo` version 4:
identity, stack bounds, state (`WIT_THREAD_STATE_RUNNING`, `READY`, `WAITING`, `SUSPENDED`, `EXITED`), exit code,
suspend count, the rights of the handle used, the context flags a context of the thread carries, and the frozen
line's TLS fields (they leave at K8). The handle needs the `QUERY` right or a context right: a context carries the
same prefix, and a handle that may set a thread's context must be able to build one from the record. The caller writes `Version` and `Size` into the buffer; the kernel validates the
whole destination before it reads the header, rejects a foreign version (`UNSUPPORTED`) or size (`INVALID_ARGUMENT`)
and copies one atomic snapshot. It absorbs `THREAD_REFERENCE_QUERY` (203), `THREAD_NATIVE_ID` (204) and
`THREAD_CONTEXT_METADATA` (205): the frozen line builds a context prefix from the record (`wit_native_context_prefix`).

**Rights and kinds.** The rights are declared once in `user_abi.h` (`WIT_RIGHT_WRITE` 1, `WAIT` 4, `SIGNAL` 8, `QUERY`
16, `GET_CONTEXT` 32, `SET_CONTEXT` 64, `SUSPEND_RESUME` 128, `WIT_RIGHT_THREAD_ALL` 244); bit 2, the join right of the
retired join capability, is never reused. The kernel-internal `THREAD` handle kind became `THREAD_IDENTITY`: a
thread's private generation-bearing identity that user space never receives as a capability and cannot close
(`BUSY`).

**Events.** `HANDLE_DUPLICATE` duplicates event handles with the same or fewer rights (`WAIT`, `SIGNAL`): a handle
entry now names its object, several handles refer to one event, and the event ends with its last handle. Closing one
handle completes only the waits parked on that handle.

**Diagnostics.** The kernel's counters of parked and woken waits split by the wait set: `EventParks` and
`EventWakes` count waits that include an event, `ThreadWaitParks` and `ThreadWaitWakes` waits on thread handles alone
(joins), so the event expectations of the kernel tests stay what they were. A thread whose handle was closed is
reaped at its exit, and so is a main thread that leaves through `THREAD_EXIT` before its workers: the tests that
measured owned pages after such an exit account for the reclaimed stack and TLS.

### Kernel

- `handles.c`: `Object` in the handle entry, `wit_handle_grant_object` and `wit_handle_describe`.
- `events.c`: events live while a handle refers to them; `wit_event_duplicate`.
- `user_thread.c`: `wit_user_thread_create` is the one form; `wit_user_prepare_thread` grants the private identity.
- `user_reference.c`: `wit_user_thread_query` by handle or self, `wit_user_handle_duplicate` for threads and events,
  `wit_user_reference_describe` for the record behind a handle.
- `user.c`: a thread is reaped at its exit after the handles that observe it learn the exit code; the join path, the
  join wait kind and the counters `ThreadJoins`, `DetachedCreates`, `DetachedReaps` and `ThreadDeadlocks` are gone.
- `user_calls.c`: one `thread_exit` (the DLL lifecycle check kept, the runtime-profile policy removed); the six
  handlers of 200–205 removed.
- `user_thread_context.c`: `wit_user_context_flags` shared by the context snapshot and the thread query.

### Fixtures and the frozen line

- `bootstrap.h`: `wit_native_thread_query`, `wit_native_thread_start` (flags wider than the request's 32 bits are
  `INVALID_ARGUMENT`), `wit_native_thread_join` (wait, query, close; a failed wait leaves the handle and a zero code)
  and `wit_native_context_prefix`; `wit_native_thread_info` is the query of `WIT_THREAD_SELF`. The query helper is
  always inlined and the self query is a macro: the default-profile probe images sit at the kernel's 128-unwind-record
  quota, and at `/Od` every static helper with a call costs a record per translation unit, so only the helpers that
  hold a request record of their own (start, join, context prefix, identity) have frames. The COM probe image stood
  exactly at the quota of 128 records and the one-form creation and the join each add a frame to it, so
  `WIT_PE_MAX_UNWIND_ENTRIES` is 160: the loader's unwind validation stays bounded by the quota, which only sizes a
  loop; the PE loader itself leaves at K8.
- Fixtures that compared a worker's identity with its handle compare it with the handle's record instead: the handle
  is a capability, the identity is what the thread itself reports, and the two differ. Waiting on or closing an
  identity is `WRONG_TYPE` or `BUSY`; it never was a capability.
- `thread.c`: the one creation; detached creation closes the handle; the exit is `THREAD_EXIT`.
- `patches/runtime/threadstore.witos.cpp.patch`: the DAC TLS metadata reads the thread record through the self query
  of `bootstrap.h`; the patch's `After` hash follows the new text (the startup object slice is cut from the patched
  file, so it changes with it).
- The PAL and the fixtures that queried references, the native id or the context metadata use the record; the
  thread and wait fixtures of both ISAs create through the request and join through `OBJECT_WAIT`, `THREAD_QUERY` and
  `HANDLE_CLOSE` in subroutines.
- The kernel tests assert `ThreadReaps` where they asserted joins; the thread fixture's join-cycle mode is gone
  (`User.ThreadJoinCycle`), `User.ThreadJoinAndReuse` became `User.ThreadWaitAndReuse`; the runtime-boot abrupt
  scenarios expect the frozen runtime's own fail-fast (`0xFFFF0103`) where the kernel used to end the component.

### Evidence

Guest acceptance on both ISAs: `test` (x64, QEMU q35, 20 scenarios) and `test --arch arm64` (QEMU virt, 14 scenarios
including the thread and wait suites) pass. The frozen line's chains pass over the new ABI: `runtime-config` (four boots),
`runtime-boot-run` (four boots, the runtime-boot protocol with the new abrupt expectations), `coreclr-memory` and
`coreclr-storage` (two boots each) and `coreclr-host-guest` (no unresolved external). The host tests check the call
table and the ABI reference against the header, and `format-check` is clean.

## K1.3 — Activations (ABI v54)

### What changed

**Push delivery.** `THREAD_ACTIVATE(thread handle or WIT_THREAD_SELF, callback, argument)` marks the target thread; at
the target's next return to user mode (from a call, a tick or a wait) the kernel enters the process's fault callback
(`EXCEPTION_REGISTER`) with a record whose `Vector` is `WIT_EXCEPTION_ACTIVATION_VECTOR` (~1), whose `Address` is the
callback, whose `Error` is the argument and whose `Context` is the interrupted one; the handler runs the callback and
`EXCEPTION_CONTINUE` resumes the context (RFC 0011 §7.5). The transitional pull queue is gone: `APC_DEQUEUE` (206) is
retired and never reused, and a self-activation is delivered at the return of the activating call itself.

**Interruptible waits.** A wait or sleep the target is parked in ends with `INTERRUPTED` after the handler continues
the interrupted context; `SLEEP_UNTIL` returns it too. Flag 1 of `WitUserWaitRequest` (`ALERTABLE`) is retired and
never reused; `ALL` stays. The kernel counts the interruptions (`WaitInterruptions`) and the deliveries
(`ActivationDeliveries`).

**Validation and quota.** Everything is checked before the target is marked: the handle's `ACTIVATE` right (the new
bit 256; `WIT_RIGHT_THREAD_ALL` is 500), an executable and not writable callback (`BAD_ADDRESS`), a registered fault
callback (`NOT_FOUND`) and the per-thread quota `WIT_ACTIVATION_CAPACITY` of four pending activations (`NO_MEMORY`),
delivered in order, one per return to user mode. A thread inside a delivery (a fault's or an activation's handler) or
suspended keeps its activations pending; `EXCEPTION_REGISTER` answers `BUSY` while one is pending, so the callback
cannot be dropped under it. A stack with no room for the callback frame ends the component as a fault of the thread
with the activation vector, as a POSIX process ends when its signal frame does not fit. A fault inside an activation's
handler is reported as that fault, not as the activation. On ARM64 the call is `UNSUPPORTED` until the context block of
K1.4 exists.

### Kernel

- `user_activation.c` (replaces `user_apc.c`): `wit_user_thread_activate`, `wit_user_activation_deliver` and the
  per-thread pending list.
- `user_exception.c`: `wit_user_exception_enter`, the one entry into the fault callback for faults and activations;
  `EXCEPTION_REGISTER` is `BUSY` while an activation is pending.
- `user.c`: `resume_current`, the one point where a frame about to return to user mode delivers a pending activation
  (the scheduler's dispatch, the plain return of a call and the frame a call restored); `wit_user_fault_state`.
- `user_wait.c`: `wit_user_wait_interrupt`; `user_objects.c`: the `ALERTABLE` path is gone; `user_calls.c`: the
  `APC_DEQUEUE` handler is gone.

### Fixtures and the frozen line

- `native_activation.witos.cpp`: the activation-only fault callback and the registry of the process's one callback;
  it yields to the exception dispatcher where one is linked (`native_exception.witos.cpp` registers through it and
  dispatches the activation vector before anything that needs compiler TLS). It is linked wherever `native_wait` or
  the dispatcher is: the overlay archive, the CPU and thread probe images (the COM probe takes every platform object)
  and the guest support objects of the CoreCLR images.
- `native_wait.witos.cpp`: `QueueUserAPC` installs the activation callback and calls `THREAD_ACTIVATE`; an alertable
  `WaitForMultipleObjectsEx` reports a delivered activation as `WAIT_IO_COMPLETION`, a non-alertable one restarts at
  its absolute deadline. `bootstrap.h` restarts `wit_native_wait_any` and the new `wit_native_sleep_until` the same
  way, which covers the PAL's events, mutexes, sleeps and the GC's and minipal's sleeps. `THREAD_SET_CONTEXT` maps to
  `SET_CONTEXT | ACTIVATE` (QueueUserAPC needs it on Windows).
- `runtime_object_wait.cpp`: the push semantics (a self-activation runs before the activating call returns, a parked
  alertable wait reports it, a non-alertable wait restarts, a suspended thread holds four and refuses the fifth, the
  rights of query and get-context handles); `runtime_exception.cpp`: the raw contract (the record, `NOT_FOUND`,
  `BAD_ADDRESS`, `BAD_HANDLE`, `DENIED`, `CLOSED`, `NO_MEMORY`, `BUSY` of `EXCEPTION_REGISTER`, an interrupted wait and
  sleep, order on a resumed thread). `User.AlertableObjectWaits` became `User.ActivationsAndObjectWaits` and
  `User.ApcWithoutCompilerTls` `User.ActivationsWithoutCompilerTls`; the ARM64 thread fixture checks `UNSUPPORTED`.

### Not in this slice

Delivery on ARM64 (K1.4, with the context block); the alternate stack of `EXCEPTION_REGISTER` (RFC 0011 §7.5 lists it
for K1; it waits for a consumer); the frozen PAL's suspend-and-context hijack path stays as it is, and
`PalWaitForSingleObjectEx` keeps refusing alertable waits.

### Evidence

Guest acceptance on both ISAs: `test` (x64, QEMU q35, 20 scenarios) and `test --arch arm64` (QEMU virt, 14 scenarios,
the thread fixture's `UNSUPPORTED` check included) pass. The frozen line's chains pass over the new ABI:
`runtime-config` (four boots; `User.ActivationsAndObjectWaits`, `User.ActivationsWithoutCompilerTls` and the
exception scenarios with the raw activation contract), `runtime-boot-run` (four boots, the runtime-boot protocol
unchanged), `coreclr-memory` and `coreclr-storage` (two boots each) and `coreclr-host-guest` (no unresolved
external). The host tests check the call table and the ABI reference against the header and the platform object
groups, and `format-check` is clean.

## K1.4 — ARM64 contexts (ABI v55)

### What changed

**The AArch64 register block.** `WitThreadContext` keeps its common prefix and gains the AArch64 block beside the
x64 one (RFC 0011 §7.3: only the block differs between ISAs): `X0`–`X30`, `SP`, `PC`, `PSTATE`, the 32 vector
registers as 16-byte lanes of `V`, `FPCR` and `FPSR`, 848 bytes against x64's 720. `thread_context.h` selects the
block of the compiling ISA, names both sizes (`WIT_THREAD_CONTEXT_SIZE_X64`, `WIT_THREAD_CONTEXT_SIZE_ARM64`) and
derives `WIT_THREAD_CONTEXT_SIZE`, from which the exception record (+48), the transfer (+16) and the frozen line's
fatal record (+152) follow. `TPIDR_EL0` is per-thread frame state outside the context; x18 is reported but reset to
the compiler TLS on every return to EL0, so a context cannot change it.

**Validation.** The user profile of a context on ARM64: `PSTATE` carries the condition flags NZCV alone (EL0t, every
exception unmasked), `FPCR` stays within the bits the hardware implements, which the kernel probes once at boot as
x64 probes the MXCSR mask (`context.c`), and `FPSR` within the architectural bits; a context outside the profile is
`INVALID_ARGUMENT`. Hardware debug must be off (MDSCR_EL1 without MDE and KDE), which the boot check enforces and
`CONTEXT_PROFILE` reports as `WIT_CPU_DEBUG_DISABLED`.

**CONTEXT_PROFILE per ISA.** `EnabledState` is `LEGACY` (x87 and SSE, the FXSAVE64 image) on x64 and the new
`FPSIMD` on ARM64; `LegacySaveBytes` is the floating-point block (512 on both); the selectors are zero on ARM64; the
field `MxcsrMask` became `FloatControlMask`, the MXCSR mask on x64 and the implemented FPCR bits on ARM64. The
exception record's `RawRflags` became `RawState`: the RFLAGS or SPSR of the interrupted frame as saved.

**Delivery on ARM64.** The fault callback receives the EL0 exception classes undefined instruction (0x00), trapped
system register access (0x18), instruction abort (0x20), PC alignment (0x22), data abort (0x24), SP alignment (0x26)
and `BRK` (0x3C); `Vector` is the class, `Error` the syndrome, `Address` the fault address of aborts and alignment
faults. Activations and `EXCEPTION_CONTINUE` work as on x64; `THREAD_ACTIVATE` no longer answers `UNSUPPORTED`.

### Kernel

- `thread_context.h`, `exception.h`, `fatal_info.h`, `cpu_context_info.h`: the per-ISA block and sizes, `RawState`,
  `FloatControlMask`, `WIT_CPU_CONTEXT_FPSIMD`, `WIT_THREAD_CONTEXT_FPSIMD`.
- `Kernel.Arch.A64/frame.c`: the context functions of `witos/arch.h` (capture, validation, apply, the profile, the
  exception records and the fault state from a context) and the deliverable classes; `context.c`: the FPCR probe and
  the debug check at boot; `entry.asm`: `fpcr` and `mdscr_el1` access.
- Nothing in `src/Kernel`: the common kernel delivers, validates and continues through `witos/arch.h` as before.

### Fixtures and the frozen line

- `tests/User.X64/exceptions.asm` and `tests/User.A64/exceptions.asm`, driven by the common
  `tests/Kernel/user_exception_tests.c`: `EXCEPTION_REGISTER`, a read fault at address zero delivered with the
  interrupted context, the record checked field by field (class, syndrome or error code, address, PC, a marked
  register, PSTATE or RFLAGS), a wrong size, a foreign token and a privileged PSTATE or IOPL refused,
  `EXCEPTION_CONTINUE` to a landing with the marked register changed, `THREAD_ACTIVATE` of the own thread through the
  same callback, `CONTEXT_PROFILE` and `THREAD_CONTEXT_GET` of the own thread, `THREAD_CONTEXT_SET` of the running
  thread `BUSY`; the second mode rejects the fault and the kernel reports the original one. Markers
  `User.ExceptionCallbackAndContext` and `User.ExceptionReject` on both ISAs; the ARM64 thread fixture's
  `UNSUPPORTED` check of K1.3 is gone. The fixture headers `thread_context.h`, `exception.h` and
  `cpu_context_info.h` join the ABI constants of the assembly fixtures.
- The frozen line renames `MxcsrMask` to `FloatControlMask` in the context conversion, the dispatcher and the
  context storage adapter; nothing else changes for x64.

### Not in this slice

ARM64 unwind metadata (PE unwind validation is K8 policy; `wit_test_exception_directory_status` stays
`WitPeUnsupportedImage`), the alternate stack of `EXCEPTION_REGISTER`, and the frozen Windows-form user space on
ARM64, which never existed.

### Evidence

Guest acceptance on both ISAs: `test` (x64, QEMU q35, 20 scenarios) and `test --arch arm64` (QEMU virt, 14 scenarios)
pass with the exception suite on both (`User.ExceptionCallbackAndContext`, `User.ExceptionReject`; the rejected
fault's line on ARM64 carries the class, the syndrome and the EL0 PSTATE the host validator checks). The frozen
line's chains pass over the renamed profile field: `runtime-config` (four boots), `runtime-boot-run` (four boots),
`coreclr-memory` and `coreclr-storage` (two boots each) and `coreclr-host-guest` (no unresolved external). The host
tests check the call table and the ABI reference against the header, and `format-check` is clean.
