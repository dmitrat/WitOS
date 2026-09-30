# P1 completion audit

Status: **P1 complete in the tested x64/UP NativeAOT bring-up profile (2026-09-29).** All final gates and artifact consistency checks passed. Scope is the P1 checklist in root PLAN.md, including P1.8.a-h and the explicit guest integration criteria; later P3/P5/P6 requirements are not silently treated as complete.

| Requirement | Implementation / evidence | Completion evidence |
| --- | --- | --- |
| P1.1 minimal standard-CoreLib workload and strict inventory | experiments/NativeAotBoot, RuntimeReadiness, locked package/source audit | Actual ILC object, normal executable bootstrap, standard CoreLib; minimal link 0 unresolved and no OS imports |
| P1.2 platform dependency decisions | [startup boundary](P1-Startup-Platform-Boundary.md), runtime-readiness platform inventory | Every selected dependency classified; broad transport-free inventory retains exactly six supplied transport/TLS symbols |
| P1.3 CRT/compiler protections | [math](P1-Native-Math.md), [security cookie](P1-Security-Cookie.md), formatting and GS/SEH reference gates | Actual guest archive objects, compiler frames and negative cookie cases; no fake CRT/throwing-new implementations |
| P1.4 entropy | [entropy](P1-Entropy.md), boot no-rng scenario | UEFI entropy and kernel RNG feed actual cookie/random services; failure profile rejects absent entropy |
| P1.5 CoreLib/native bindings | [memory](P1-Native-Memory.md), [services](P1-Native-Services.md), [thread references](P1-Thread-References.md), native runtime profiles | Real kernel handles, validation, lifetimes, clocks, waits and direct/import bindings; actual runtime allocation/collection/lifecycle workload |
| P1.6 module/encoding/diagnostics and Windows boundary | [UTF conversion](P1-Utf-Conversion.md), [COM](P1-Com-Lifecycle.md), startup boundary | Explicit static-image/MTA/profile decisions, Windows references and actual guest bindings; no Windows implementation libraries linked |
| P1.7 GC OS policy | runtime-gc-policy and full native archive audit | Workstation profile and excluded write-watch path verified in actual objects; real guest collector executes |
| P1.8.a contexts/suspension | [capture](P1-Context-Capture.md), [suspension](P1-Thread-Suspension.md), [mutation](P1-Context-Mutation.md) | Kernel-owned whole-context operations and rights checks; selected x87/SSE state, contained negative cases |
| P1.8.b/c hosted checked unwind | [reference](P1-Unwind-Reference.md), [checked algorithm](P1-Checked-Unwinder.md) | Pinned algorithms, Windows differential cases, malformed metadata and unchanged outputs on refusal |
| P1.8.d actual guest unwind | [guest unwinder](P1-Guest-Unwinder.md), [walk scopes](P1-Native-Unwind-Scope.md) | Actual archived production objects, current/foreign stack walks, scope lifetime and readonly image metadata |
| P1.8.e selected native exception profile | [collided unwind](P1-Collided-Unwind.md), [GS/SEH and GP](P1-Gs-Seh-and-Gp.md) | Native kernel delivery, compiler frames, nested/collided transfer, fail-fast/GS and contained faults; managed EH suite remains P5 |
| P1.8.f runtime attach/shutdown | [worker lifecycle](P1-Worker-Lifecycle.md) | Actual ThreadStore/RuntimeThreadShutdown/FixAllocContext, normal/detached workers, repeated attach, TLS cleanup, failures and reuse |
| P1.8.g GC rendezvous/roots | [runtime rendezvous](P1-Runtime-Rendezvous.md) | Worker-driven compacting GC, main/worker stack and RAX/RBX roots, actual context redirection, active service refusal and collection during thread exit |
| P1.8.h integration gate | Final queue listed below; hash-linked runtime acceptance | Passed the complete final queue, all 218 overlay hashes, source/input/image/disk/serial hashes and the actual execution criteria |
| P1.9 actual handoff driver | [first managed boot](P1-First-Managed-Boot.md), boot_driver.witos.cpp, RuntimeGuestDriver | Validated handoff/image/environment, GS/TLS/initializer ordering, real wmain/Main, invalid-handoff rejection and orderly teardown at two bases |
| P1.10 complete strict link | RuntimeGuestDriver image/map/inputs, source-build report | Actual full executable has zero unresolved symbols and no OS imports, real roots and readonly initializer tables; no forced success |

## Final validation results ? all passed

- Release solution build: artifacts/p1-hijack-build.log.
- 20 kernel integration scenarios: artifacts/p1-final-test.log.
- Source/package audit: artifacts/p1-final-runtime-audit.log.
- Hosted managed probe: artifacts/p1-final-runtime-probe.log.
- Hosted target/bootstrap and strict boundary: artifacts/p1-final-runtime-target.log.
- Full reference/WitOS source builds, reference executions, object identity, GC policy and source corrections: artifacts/p1-final-runtime-source.log.
- Four existing native runtime profiles, preserving expected contained faults: artifacts/p1-final-runtime-config.log.
- Final four-profile, two-base full managed runtime matrix: artifacts/p1-final-runtime-boot-run.log and artifacts/x64/runtime-boot/acceptance.json.

The final guest matrix executed image SHA-256 0add9e777e7d1099d12624a7ed1b73bccec8c57c031f9b2c0310db22c6dc78dd, matching the final source build. All linked input/source, disk and serial-log hashes match; all 218 overlay files match the source report. artifacts/p1-completion-evidence.json records the checked gate-log hashes and counts: 20 boot scenarios, 4 native profiles of 284 groups / 66 expected faults, 8 managed executions and 104 worker lifecycles. PLAN.md marks every P1 requirement complete. P3 fault/failure/abrupt-exit requirements, full M3/P5 and CoreCLR/JIT/P6 remain open.

Original vision documents and the portable CoreCLR/JIT application contract are unchanged. No commit, push or publication is part of this goal.


## Subsequent quality review

The [code quality and test coverage audit](Code-Quality-and-Test-Coverage-Audit.md) reproduced four host-tooling defects and identified a NativeAOT CI path-filter gap. The captured P1 runs and their separately verified hashes/counts remain valid. The subsequent [Q0 hardening](Q0-Tooling-Hardening.md) fixes those findings and passes the complete validation pipeline. PLAN.md marks Q0 complete and retains the remaining P3/P5/P6 scope.

## P3 follow-up

The subsequent [P3 completion audit](P3-Completion-Audit.md) closes all P3.1-P3.8 criteria with the final fault, OOM/recovery and abrupt-exit matrix. The P1 checkpoint and hashes above remain historical evidence. PLAN.md now points to P5.1; full M3/P5 and CoreCLR/JIT/P6 remain open.
