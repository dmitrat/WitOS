# P1.8.e: compiler local unwind

Follow-up: [nested callback handling](P1-Nested-Seh-Callbacks.md) now passes in the guest; exceptions escaping callbacks and collided unwind remain open. The rejection statements below describe the earlier local-unwind checkpoint.

The real _local_unwind helper now executes compiler-generated local return/goto paths in the guest. It uses the existing target-unwind engine, not a return-success stub. Nested exceptions from filters/finalizers and collided unwind remain open; this does not complete P1.8.e or managed EH.

## ABI and transfer

MSVC assembly evidence shows RCX carries the actual establisher frame and RDX the compiler target address. The x64 helper captures the caller's GPRs, RIP/RSP, flags and x87/SSE state with real unwind metadata. The native adapter validates kernel-provided identity/bounds/CPU state, establishes a bounded software scope with STATUS_UNWIND, obtains a real current-stack lease and invokes phase-two target unwind.

The common target path now takes an explicit return value: exception handling supplies the exception code in RAX, while local unwind supplies zero as the helper's transfer value. The compiler landing pad then performs its own return expression. Before kernel continuation the walk lease is closed and the native dispatch pointer restored; no pending exception or lease remains after a completed root local unwind.

## Windows and guest evidence

A real Windows compiler oracle covers return, __leave and goto from try/finally. It confirms AbnormalTermination is true for return/goto and false for __leave. Compiler assembly output records the actual _local_unwind frame/target arguments, and the oracle enforces those observed abnormal values.

Guest MSVC fixtures exercise the same paths through the production helper and additionally return through two nested finally scopes, checking exact order and count. Every case runs at both supported image addresses. The earlier exceptional catch/filter/finally target tests remain intact. Passed: Release build, audit/probe/target/source/readiness, the Windows SEH oracle, all 20 boot scenarios and all four runtime profiles (276 User groups / 65 expected contained faults each). Minimal/broad link remains 3/9 unresolved; archive 137 members, config probe 51 objects.

Artifacts: artifacts/p1-local-unwind-config.log, p1-local-unwind-test.log, artifacts/runtime-seh-reference/reference.log and seh_reference.asm. Native archive/object hashes and fixture input hashes remain in the standard runtime-source/runtime-config reports.

## Remaining integration

Current dispatch leases still deliberately reject an unsafe software raise during a live filter/finally callback. Supporting that path requires explicit native/kernel scope suspension/reentry and collided-unwind orchestration, including cleanup of superseded dispatch frames and exact-once finally behavior. Do not remove those safety checks merely to make a nested call appear successful. Combined GS/SEH payloads and general-protection translation also need acceptance before P1.8.e can close.
