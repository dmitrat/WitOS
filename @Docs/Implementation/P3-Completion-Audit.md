# P3 completion audit

Status: **P3 complete in the tested x64/UP NativeAOT profile (2026-09-29).** All final gates and artifact consistency checks passed. Scope is all P3.1-P3.8 in root PLAN.md, including the previously completed P1 integrations and the remaining fault/failure/abrupt-exit requirements.

| Requirement | Actual implementation/evidence | Acceptance |
| --- | --- | --- |
| P3.1 context/thread capabilities | Kernel-owned references, capture/get/set/restore, x87/SSE and bounds/rights validation | Native matrices plus actual GC context redirection; no writable-TLS identity authority |
| P3.2 rendezvous | Actual ThreadStore suspension/trap protocol, PalHijack and counted kernel suspend/resume | Managed spin-loop with live roots, native parked thread, service-frame refusal and GC during TLS cleanup |
| P3.3 attachment/TLS | PalAttachThread and complete upstream ThreadStore creation/attachment; compiler TLS and exit notification | Main/finalizer startup, normal/detached workers, repeated attach, list membership, reuse and fresh TLS |
| P3.4 walks/code manager/GC metadata | Actual archived checked unwinder; GcScanRoots retains kernel-owned stack lifetime through root-pointer use | Main/foreign stack walks, real RAX/RBX roots and native/managed transitions through collector execution |
| P3.5 faults/context profile | Actual kernel upcalls, pinned RhpVectoredExceptionHandler/RhpThrowHwEx and standard CoreLib | 144 read/write/divide CPU faults with catch/finally/GC; native UD2 is runtime fail-fast and component teardown; unsupported CET/XState modes are rejected |
| P3.6 collector/failure/accounting | Full upstream workstation collector and real kernel memory operations | 32 hard-limit/backing-pressure OOM recoveries; 8 actual g_pGCHeap initialization failures; atomic failed-commit accounting and complete teardown |
| P3.7 collection/roots | Actual compacting and ordinary collections, write barriers and allocation contexts | Local/static/interthread and volatile/nonvolatile register roots survive; suspended/parked/returning workers resume without deadlock |
| P3.8 orderly/abrupt exit | Complete RuntimeThreadShutdown/FixAllocContext precedes THREAD_COMPLETE; coordinated raw exit is component-fatal | 104 orderly lifecycles, 32 joinable/detached raw/fault cases, no abrupt TLS/platform/atexit cleanup, captured kernel identity/cause and complete resource reuse |

## Evidence sources

- [Runtime rendezvous](P1-Runtime-Rendezvous.md) and [worker lifecycle](P1-Worker-Lifecycle.md): original actual GC/thread integration.
- [Runtime fault delivery](P3-Runtime-Fault-Delivery.md): P3.5, including the reproduced/fixed host cleanup regression.
- [GC failure/recovery](P3-Gc-Failure-Recovery.md): P3.6 heap-limit and real backing allocation failures.
- [Abrupt runtime exit](P3-Abrupt-Runtime-Exit.md): P3.8 baseline failure, generic ABI v36 policy and user-space cleanup boundary.
- artifacts/p3-fault-validation.json and p3-memory-validation.json retain the completed earlier gates.
- artifacts/p3-abrupt-build.log, p3-abrupt-host-tests.log, p3-abrupt-kernel-test.log and p3-abrupt-final-*.log record the successful final gates.
- artifacts/p3-completion-evidence.json records the final counts and gate-log SHA-256 hashes. Final runtime-boot current-run/acceptance, per-run PE/input snapshot, linked source/object inputs, disk and serial hashes agree.

Release passed with zero warnings/errors, alongside 18 host groups, 527 hosted PE inputs, all 20 kernel scenarios and runtime-audit/probe/target/source/config/boot. Four native profiles preserve 284 groups / 66 expected contained faults each. The final managed matrix covers all four RAM/CPU profiles at both image bases: 8 positive executions, 104 orderly worker lifecycles, 144 managed hardware faults, 32 OOM recoveries, 8 GC initialization failures, 8 native main fatal components and 32 abrupt worker components. All 75 canonical source pins and 218 overlay hashes were verified; the upstream tree is clean and source/package pins remain unchanged. Earlier failed/partial runs are retained as diagnostic evidence, not substituted for final success.

Final run: `20260929T142615361-f06b2d108dc6462c854ac90ef26ec7a2`. Executed PE SHA-256: `0d7a96b8c83460f2618d13d57a6c45effc4cab0eac912e167a5af6ea20d8261c`. Image size: 1,011,712 bytes; 2,842 unwind entries; 648 TLS bytes; user ABI v36 / boot ABI v3. The executable has no OS imports, delay imports or PE TLS callbacks. Minimal/broad unresolved inventory remains 0/6; all six intentionally excluded transport/TLS symbols are resolved in the executable.

PLAN.md marks every P3.1-P3.8 criterion complete. The next implementation stage is P5.1; no P5/P6 criterion is closed by this audit.

## Explicit limits

The profile is x64, one online CPU, baseline x87/SSE, static NativeAOT and standard upstream CoreLib with workstation non-concurrent GC. The tests prove the selected actual context-redirection path, not forced execution of every alternative return-address fallback. THREAD_COMPLETE is a user-space lifecycle assertion, not an unforgeable security capability or kernel proof of managed detach.

P5 still covers broader managed throw/filter/nested unwind, finalizer-queue and managed Thread/monitor APIs, combined application stress and additional failure locations. P6 remains upstream CoreCLR/JIT and unchanged portable managed binaries. P3 completion does not imply these later milestones or full .NET API compatibility.
