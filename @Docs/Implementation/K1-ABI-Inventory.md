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
