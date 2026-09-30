# P3.8: coordinated completion and abrupt runtime termination

Status: **P3.8 complete (2026-09-29)** in the selected x64/UP NativeAOT profile. All mandatory final regressions and the [P3 completion audit](P3-Completion-Audit.md) passed.

## Reproduced defect

The baseline full-runtime fixture attached a real joinable worker to ThreadStore, entered a standard-CoreLib callback and left a populated allocation context. A raw THREAD_EXIT bypassed TLS/runtime notifications, but another thread resumed after observing that worker exit. The fixture deliberately failed with 0xFFFF0103. The remaining runtime could retain the dead thread record/TLS; the baseline is recorded in artifacts/p3-abrupt-baseline.log.

## Generic kernel policy, user-space cleanup

Experimental user ABI v36 adds THREAD_COMPLETE(code,0,0), call 63. System.Native invokes it only after wit_native_tls_leave and wit_native_thread_notify_exit. The kernel performs the existing thread-exit/reap operation; it does not invoke or emulate RuntimeThreadShutdown, ThreadStore detach, FixAllocContext or managed callbacks.

The kernel-selected full-runtime admission profile sets RequireThreadCompletion independently of memory quotas. Raw THREAD_EXIT in this coordinated profile records the kernel-owned caller identity and requested code, then terminates the entire component with WIT_PROCESS_ABRUPT_THREAD_EXIT (0xFFFF0002). Peers cannot resume while holding dead runtime records. The ordinary native profile retains its existing raw current-thread exit behavior.

THREAD_COMPLETE is a lifecycle assertion, not a security capability and not proof of managed cleanup against hostile native code in the same component. Native code can already corrupt its own runtime state. The distinction makes supported System.Native completion and explicit raw bypass unambiguous. Reserved completion arguments are rejected before mutation; successful completion cannot return. The boot ABI and public application compatibility target are unchanged.

An unhandled native fault in an attached worker follows the actual upstream handler/fail-fast path and terminates the whole component. Whole-process raw exit remains abrupt component teardown. No attempt is made to resume a partially damaged runtime or to synthesize managed detach in the kernel.

## Acceptance

A private native fixture is linked against actual upstream thread headers and the complete runtime. Four modes run at both image bases: joinable raw exit, detached raw exit, joinable native UD2 and detached native UD2. Each worker really attaches, performs a managed allocation callback and proves a populated allocation context before the abrupt operation.

The mapped image report is a test observation only, never kernel authority. Its four words record completed setup, C++ TLS destructor, platform cleanup and process atexit. Each abrupt case must report 1/0/0/0. The raw cases require the kernel-captured non-main thread identity and original exit code 0x1234; fault cases require actual illegal-instruction delivery, runtime fatal code 0xC000001D and a non-main fatal thread. All handles/events and component-owned backing/pages/tables must be reclaimed before the next case.

The normal workload additionally requires exactly thirteen THREAD_COMPLETE operations and retains all earlier real TLS/detach/FixAllocContext, GC, exception and OOM checks. Invalid reserved arguments return INVALID_ARGUMENT while the caller remains live. RuntimeBootProtocol validates each abrupt mode/base block, zero cleanup counters, expected cause and final containment marker; mutation tests reject missing cases, unwanted cleanup and missing orderly completions.

Across four profiles the final run completed 32 abrupt worker components, 104 orderly worker lifecycles, 144 managed hardware faults, 32 OOM recoveries and the existing startup/native-fault failure cases. The subsequent successful components demonstrate kernel supervision and resource reuse after termination.

## Evidence

- artifacts/p3-abrupt-baseline.log: failure before coordinated exit policy.
- artifacts/p3-abrupt-build.log and p3-abrupt-host-tests.log: Release, protocol regressions and PE corpus.
- artifacts/p3-abrupt-protocol.log: strict targeted guest matrix.
- artifacts/p3-abrupt-kernel-test.log and p3-abrupt-final-*.log: mandatory final gates.
- artifacts/p3-completion-evidence.json records final gate-log hashes and counts. Run 20260929T142615361-f06b2d108dc6462c854ac90ef26ec7a2 executed PE SHA-256 0d7a96b8c83460f2618d13d57a6c45effc4cab0eac912e167a5af6ea20d8261c; current-run, acceptance, immutable PE/input snapshot and serial hashes agree.
- Release passed with zero warnings/errors; 18 host groups, 527 PE corpus inputs, all 20 kernel scenarios and runtime-audit/probe/target/source/config/boot passed. Four native profiles retain 284 groups / 66 expected contained faults each; 75 source pins and 218 overlay hashes were verified.

Broader managed Thread APIs, finalizer queue behavior and combined managed exception/finalization/Thread acceptance remain P5; portable CoreCLR/JIT applications remain P6.
