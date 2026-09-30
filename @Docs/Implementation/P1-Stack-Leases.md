# P1.8.d: kernel-owned stack lifetime

Follow-up: [P1-Guest-Unwinder](P1-Guest-Unwinder.md) records actual production GS context/scope/unwinder execution and the explicit runtime PE profile. The results and limitations below describe this earlier implementation checkpoint; managed execution and full exception dispatch remain open.

This slice supplies an experimental stack-lifetime mechanism for the upcoming guest unwinder. It does not implement RtlVirtualUnwind, runtime attachment, a GC rendezvous or managed execution. The upstream checked unwinder remains a hosted/compile-tested adapter until its guest consumer is connected.

## Contract

User ABI v31 adds three private bring-up calls:

| Call | Arguments | Result |
| --- | --- | --- |
| 52: STACK_LEASE_ACQUIRE | thread reference/current pseudo, output, exact size | One atomic 48-byte WitStackLeaseInfo snapshot |
| 53: STACK_LEASE_QUERY | token, output, exact size | Fresh kernel-derived owner, target identity and fixed stack bounds |
| 54: STACK_LEASE_RELEASE | token, zero, zero | Release exactly the calling thread's token |

The version-1 snapshot contains Version, Size, Token, OwnerId, ThreadId, StackLow and StackHigh. Bounds are low-inclusive/high-exclusive and exclude guards. User copies are descriptive; the kernel registry owns authority. Tokens never repeat across thread/component slot reuse. An unsigned counter reaching zero is permanently exhausted, rather than wrapping into a previous token. Four live leases share a fixed component-private registry; acquisition allocates no pages, native heap or generic handles.

Acquisition requires GET_CONTEXT. For a foreign target it additionally requires SUSPEND_RESUME and an already suspended Ready/Waiting thread. It does not silently stop a running target. Current-thread acquisition needs no artificial self-suspension. The entire destination and complete fixed stack backing are validated before publishing a lease. Wrong rights, invalid buffers/sizes, insufficient quota or an unsuitable target leave caller output and registry unchanged.

All registry operations execute with interrupts disabled under the current single-online-CPU kernel contract. Query/release check the current kernel thread identity against the recorded owner. Copied raw FS/GS identities, modified snapshot fields and token possession in another thread confer no authority. The kernel uses target generation, not the reusable slot address.

## Lifetime and mutation

A foreign lease prevents decrementing the target's final suspension count. Additional suspend counts can still be added and balanced; the failure returns BUSY with zero result and leaves the counter unchanged. Self-owned leases do not prevent a legitimate external resumer from waking their owner after a self-suspend.

Explicit SetContext and RestoreContext are rejected while the target has any lease. Release the enclosing walk before transferring execution. A caller is still responsible for balancing its ordinary suspension counts; releasing a lease never resumes a thread.

Closing/reusing the original thread-reference handle leaves a published lease intact. Raw and orderly requester thread exit both drop all that requester's leases before exit publication/reaping. Foreign-held targets cannot reach thread exit because their final suspension cannot be removed. Reaping asserts that no lease still names the target. Whole-component finish/destroy clears the entire registry, including raw process exit and fatal termination paths.

Wait completion is independent of suspension: signals/close/deadlines can complete a parked service and change its saved syscall result while dispatch remains blocked. The lease guarantees fixed stack backing and prevents explicit context mutation; it does not claim immutable wait state or make an unsafe syscall frame a managed safepoint. As with existing shared component memory, it does not prevent another user thread from deliberately writing stack bytes.

## Guest acceptance

The new native guest fixture runs with compiler TLS at both supported image addresses and without compiler TLS. It checks:

- Whole-range rejection across an uncommitted guard boundary, readonly output, bad sizes and reserved arguments; rejected output bytes are preserved.
- Four nested acquisitions, quota failure without output mutation, unique tokens, accurate kernel-derived stack/owner identity and stale/double release rejection.
- Foreign GET-only/SUSPEND-only denial, rejection before suspension, and denied query/release by a different kernel thread without output disclosure.
- A signaled target remaining Ready but suspended; final Resume blocked, extra suspend/resume counts balanced, SetContext rejected with the entire saved context unchanged.
- Continued validity after closing the source reference, independent nested release, and cleanup of both foreign and self leases after a raw requester thread exit without releasing another owner's leases.
- Actual thread reaping and fixed-stack slot reuse with a different thread generation; stale tokens do not attach to the replacement.
- Unreleased current-thread leases cleaned on orderly completion and explicit raw process exit. Kernel assertions check empty lease/handle/event registries and exact page accounting after teardown.

Artifacts: `artifacts/p1-stack-lease-config.log`, `p1-stack-lease-config-final.log`, `p1-stack-lease-test.log`, `p1-stack-lease-probe.log`. The final strengthened runtime matrix passed all four profiles, each with 260 User groups and 55 expected contained faults. Release build, all 20 boot scenarios, audit/probe/target/source/readiness also passed. The configuration archive now has 44 exact source objects; the full native archive remains 126 members and minimal/broad unresolved counts remain 9/15.

## Integration still required

RtlVirtualUnwind has no thread-handle parameter. Foreign walks must therefore enter an explicit runtime-owned scope using the real Thread/reference relationship, then query the kernel token to supply stable stack bounds to the checked reader. Do not infer authority from RSP alone or retain an arbitrary copied interval.

The scope must outlive returned REGDISPLAY root locations and the subsequent hijack/root update: CoffNativeCodeManager and Thread::HijackReturnAddressWorker use original stack addresses after the unwind call returns. Releasing a token inside a per-call wrapper would lose that guarantee. Runtime suspension and safepoint protocols still need their own P1.8.g/P3 evidence; this primitive does not substitute for them.

Guest failure transport, actual native unwind objects, loader support for required metadata, GS/SEH consumers and production protected-context execution remain open in P1.8.d/e. Minimal/broad strict-link boundaries remain real, with no success stubs added.
