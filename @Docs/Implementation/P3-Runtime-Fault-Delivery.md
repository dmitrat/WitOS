# P3.5: actual runtime hardware-fault delivery

Status: **P3.5 complete.** The full final validation queue passed. artifacts/p3-fault-validation.json binds the final run/image hash to 144 managed hardware faults, eight contained native fatal components, 20 kernel scenarios and 18 host groups.

## Managed translation

NativeAotBoot/FaultProbe uses no-inline unsafe reads and writes through address zero and a no-inline integer division with a zero denominator. These are not explicit managed throw substitutes. Each run repeats all three operations, catches the corresponding standard-CoreLib NullReferenceException or DivideByZeroException, executes finally, and performs real GC inside catch while retaining both an ordinary heap root and the exception object.

The workload runs on Main, one joinable native-created/runtime-attached worker and one detached worker. Kernel-owned counters independently require six null reads, six null writes, six divide faults and eighteen successful exception continuations per image. The eight-image matrix therefore delivers 144 actual CPU faults. A missing/zero counter line is rejected by the host protocol tests.

The path remains the actual kernel exception upcall -> native VEH dispatcher -> pinned RhpVectoredExceptionHandler -> RhpThrowHwEx -> standard CoreLib exception dispatch. No CoreLib, exception factory, GC implementation or runtime handler was substituted. The existing adapter handled these cases without a new runtime semantic patch.

## Native failure containment

The same full PE is loaded under a private immutable boot-resource label ending in native-fault.pe. The driver selects a test callback before invoking ordinary upstream wmain. Managed Main enters that callback through the normal unmanaged call transition; an x64-only leaf in Kernel.Arch.X64 executes UD2.

The real upstream handler recognizes the native fault inside its module and calls RaiseFailFastException. Kernel acceptance requires exited state, code 0xC000001D, matching captured RIP/address within the loaded image, one real illegal-instruction delivery, no successful continuation and no remaining handles/events. Destruction must restore the exact pre-load physical-page count. Both bases run this case before the successful managed workloads, proving continued kernel supervision and slot reuse after fatal runtime teardown.

The boot-resource label is a private test selector over the already validated readonly image descriptor; no public resource API or user ABI was added.

## Context profile

The existing kernel predicate rejects CR4.CET, OSXSAVE, FSGSBASE and other unsupported context modes, plus active hardware debug registers. PalAreShadowStacksEnabled queries this actual profile; it does not claim an enabled shadow stack. Legacy x87/SSE and validated code/stack selectors remain the supported contract. Existing context storage/restore/native exception negative suites remain mandatory.

The final gates close the selected fault-delivery boundary. Full managed exception/finalizer/Thread API coverage remains P5. GC failure/recovery is P3.6, and the attached-runtime abrupt raw-exit policy is P3.8.

## Evidence

- artifacts/p3-fault-build.log and p3-fault-host-tests.log: Release build and protocol regressions.
- artifacts/p3-fault-refresh.log: standard-CoreLib hosted reference and complete strict guest link.
- artifacts/p3-fault-boot.log: four CPU/RAM profiles, both bases, eighteen managed faults and one contained native failure at each base.
- artifacts/p3-fault-kernel-final.log and p3-fault-final-*.log: mandatory final kernel/audit/probe/target/source/config/boot gates.
- artifacts/x64/runtime-boot/current-run.json and the matching per-run acceptance/input/log snapshots: final executable identity and outcome.


The earlier kernel-test/retest logs retain two host timeout-cleanup failures. These are not accepted as successful runs. The issue was isolated with an external-writer pipe fixture and corrected as described in Q0-Tooling-Hardening.md; final regression uses the unchanged deadlines and expected outcomes. artifacts/p3-timeout-negative-baseline.log records the failing regression, p3-timeout-fixed-host-tests.log records 18 passing host groups, and p3-timeout-fixed-stress.log records the post-fix command/QEMU stress.
