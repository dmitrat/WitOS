# P5 completion audit

Subsequent [pre-P6 quality audit](P5-Code-Quality-and-Coverage-Audit.md) reproduced three tooling failure cases and identified a fallback-hijack coverage gap. Historical P5 acceptance remains recorded below. [Q1.1–Q1.6 are now complete](Q1-Quality-Hardening.md), including real return-address hijack evidence and fresh acceptance; P6.1 is next.

Status: **P5.1-P5.6 complete in the tested x64/UP NativeAOT M3 profile (2026-09-30).** All final gates and requirement-by-requirement evidence checks passed. Scope is P5.1-P5.6 in root PLAN.md. Earlier P1/P3 evidence is retained as historical checkpoints; this audit identifies the current executed image and complete regression set.

| Completed requirement | Verified implementation/evidence |
| --- | --- |
| P5.1 managed EH | ExceptionProbe checks filter search order, a throwing filter, identity-preserving rethrow, nested finally/handlers, live payloads through compacting GC and actual native page release. FaultProbe supplies real CPU null read/write/divide faults through standard CoreLib. |
| P5.2 finalization | Three waves per cycle, real finalizer thread distinct from Main, native resources released, suppression, resurrection/re-registration, rooted survivor behavior and empty-queue waits. Queue drains before orderly shutdown. |
| P5.3 managed threads | Standard Thread.Start/Join, Monitor recursion/wait/pulse/timeout, ThreadStatic freshness, running/parked roots, counted kernel suspensions, old observers during slot reuse and actual native lifecycle/ThreadStore cleanup. |
| P5.4 failures | Real heap-limit/backing-pressure OOM recovery, quota-induced GC initialization failure, native fatal paths, joined/detached raw/fault workers, managed stack guard faults and Thread.Start capacity failure with retry of the same object. Kernel accounting/output rollback and malformed request rejection remain checked. |
| P5.5 combined/repeated | Four full cycles per component, locked ThreadStore audit after every cycle, base A/B/A/B in each of four QEMU profiles, exact counters and complete component resource reclamation. |
| P5.6 supported/reproducible | Explicit M3 profile and limitations, pinned SDK/compiler/CoreLib/native sources, exact hosted semantic contract, local equivalent of CI gates, strict per-execution/hash-linked evidence and completed docs/plan. Remote CI is not claimed without a published run. |

## Fixes found during integration

- Atomic reference-bearing native thread creation and actual normal-priority/single-wait/reset bindings support upstream Thread; observers retain terminal identity independently of reaped thread backing.
- New platform bindings are separate objects so the legacy CPU image keeps its 128-entry plain unwind admission. Full-runtime image cap increased only to the next measured 64 KiB boundary; exact-cap/cap-plus-page parser tests were added.
- Full-image unwind validation had been repeated on every frame, causing an intermittent component budget exhaustion. The guest now caches complete validation per thread for the immutable published image, preserving selected-record validation, canonical entries, stack leases, bounded reads and transactional outputs. Hosted mutable input still uses full validation. Forty cached comparisons and forty forged-entry rejections accompany the original Windows differential tests.
- Runner fault accounting now preserves the legacy fault count independently from the two new managed stack faults; malformed lines or compensation across the boundary are rejected.

- Final validation additionally exposed an intermittent host QEMU timeout cleanup failure. Pending capture I/O is now canceled/disposed before termination confirmation on timeout; native process signaling and empty owned-job accounting are polled asynchronously under the unchanged five-second grace. Live jobs are never accepted, and EOF is not used as liveness evidence. A burst-output timeout regression and the existing 300-process/three-QEMU-timeout harness check the path. The final rerun passed with the QMP handshake and forced fallback described below.

## Final verification

Final run: `20260930T044307187-ae0aae48c9ce4d01b0e0e504fce9cef0`. Executed PE SHA-256: `c10b7a66c024c7410438e835b63968c297f00525aa1856565849e92460abe4f7`. Shared Windows/guest managed object SHA-256: `12213d9a391df7689e49fd7ca160a30f2a3e619252bb85a26d1d9ba48f032964`. Current-run status is succeeded and agrees with acceptance and immutable per-run snapshots. Hosted report/log/Windows PE hashes, captured guest PE, linked inputs, source fixtures, disk and serial hashes agree. All 220 overlay files match; the pinned upstream tree is clean at b82454cad0aaaae3db2cf18fbf2cccc36e201ccc. SDK 10.0.300 and all 75 source pins were verified.

- Release build: zero warnings/errors. Host suite: 24 groups and 529 guarded PE inputs.
- All 20 kernel integration scenarios pass, including genuine timeout handling with native process/job completion.
- runtime-audit, runtime-probe, runtime-target, runtime-source, runtime-config and runtime-boot-run pass. The four native profiles each retain 285 groups / 66 expected contained faults.
- 16 positive guest executions, 64 combined cycles and locked ThreadStore audits, 448 standard managed Threads and 656 orderly worker completions.
- 896 tracked finalizer callbacks, 768 native releases in finalizers, 1408 EH rounds with 8448 compacting collections inside EH, 576 translated CPU faults and 64 real managed OOM recoveries.
- 64 actual Thread.Start quota failures/recoveries, 8 GC initialization failures, 8 native main fatal components, 32 abrupt worker components and 8 managed stack-overflow components. Later components execute after exact resource reclamation.
- Image: 1,073,152 mapped bytes, 3073 unwind entries, 656 TLS bytes; no OS/delay imports or PE TLS callbacks. User ABI v37 / boot ABI v3. Full image cap 1088 KiB; ordinary limits unchanged.
- Actual positive execution usage: 272-333 of the unchanged 3000 delivered ticks. Host watchdog remains 120 seconds. Cached unwind has 40 Windows differential comparisons and 40 forged-entry transactional refusals, alongside the original 20 comparisons/13 failure cases.

`artifacts/p5-completion-evidence.json` records the checked gate-log hashes and counts; `artifacts/p5-final-source-state.json` records the SDK/upstream/base Git state. Counts are scenario executions, not line/branch coverage percentages. Failed/interrupted runs remain diagnostic history. CI configuration includes the equivalent mandatory gates and artifact preservation; no remote CI run or publication is claimed. PLAN.md marks every P5 criterion complete and leaves P6 open.

## Limits and next stage

This completes the selected x64/UP/static NativeAOT M3 profile. It is not general Windows executable compatibility, dynamic-code support or unchanged portable IL execution. The [M3 profile](M3-NativeAOT-Profile.md) states resource/ISA/GC/lifecycle limits. P6 remains upstream CoreCLR/JIT with ordinary SDK/TFM/NuGet and unchanged portable assemblies. No custom CoreLib or fake collector/bootstrap substitutes are used.

The added native-state diagnostics showed that cancellation/polling alone did not resolve QEMU's forced-exit delay: original and freshly opened process handles remained unsignaled even with exit -1 and zero active job processes. Closing the already empty job did not resolve that observation. No success was accepted from those states.

QEMU timeout shutdown now uses its [QMP protocol](https://www.qemu.org/docs/master/interop/qmp-spec.html): after the original deadline expires, negotiate capabilities, wait for the matching acknowledgement, then request quit. Guest serial evidence is kept in a separate file and monitor traffic in a separate log. The cooperative phase shares the existing five-second cleanup budget with forced root/job termination fallback. Native process signaling and empty owned-job accounting remain mandatory; TimedOut remains true even when QEMU exits normally in response to quit. This follows the distinction between an exit request and [completed Windows process termination](https://learn.microsoft.com/en-us/windows/win32/procthread/terminating-a-process).

The first pipelined capability/quit attempt produced QMP parse errors; explicit acknowledgement sequencing fixed it. Host tests now cover cooperative timeout status, acknowledged control exchange, uncooperative forced fallback, output bursts, detached descendants and external pipe writers. The cleanup checkpoint passed 23 host groups/529 PE inputs; the later nested-snapshot schema regression expands the final host suite to 24 groups. The 300-process plus three-QEMU-timeout stress passes with exact native completion; artifacts/p5-cleanup-qmp-handshake.log and the corresponding monitor logs record acknowledgements and host-qmp-quit events. Failed intermediate experiments remain diagnostic evidence, not acceptance.

The first hosted/guest linkage check used the wrong JSON level for link inputs. It now reads the captured buildEvidence.inputs array, requires one correctly formed NativeAotBoot.obj hash and rejects missing, malformed or duplicated identities. The failure was detected before guest launch; its snapshots and log remain retained. A dedicated schema regression accompanies the corrected linkage.
