# M3 NativeAOT system-component profile

Status: **M3/P5 complete in this tested profile (2026-09-30).** See the [completion audit](P5-Completion-Audit.md) for the final run, hashes and gate results.

## Meaning of the milestone

The selected upstream .NET 10.0.8 NativeAOT runtime and standard CoreLib run inside an isolated WitOS component. The acceptance executable uses actual managed allocations/GC, exceptions, finalizers and Thread/Monitor APIs, with checked console output. The managed object linked into the guest is the exact object produced for the Windows NativeAOT reference. This is the NativeAOT system implementation milestone; unchanged portable DLLs under CoreCLR/JIT remain P6.

## Supported bring-up profile

- x64, one online CPU, baseline x87/SSE context preservation; no AVX/XSAVE, CET or SMP claim.
- Static single-image NativeAOT executable, standard CoreLib, workstation/non-concurrent GC, invariant globalization. The guest acceptance driver sets a 4 MiB managed heap hard limit; its private native allocation arena is 256 KiB with 128 allocation records. These are prototype quotas, not a general application compatibility promise.
- User ABI v37 / boot ABI v3; fixed 64 KiB user stacks, contained fatal stack exhaustion.
- Private full-runtime image cap 1088 KiB, 4096 unwind entries, 8 MiB owned backing, 32 reservations, 16 events and 32 handles. Four thread slots include Main and finalizer, leaving two concurrent application workers. Normal scheduling priority only.
- Kernel-published immutable image/unwind metadata; per-thread validation cache preserves checked stack/entry/record/output semantics. No dynamic module/unload/JIT registration contract.
- Real thread attachment/detachment, managed ThreadStatic isolation, observer lifetime after slot reuse, monitor waits, actual GC root walks/redirection/return-address hijack and finalizer queue. Raw/fault thread termination in coordinated components is component-fatal; orderly completion runs user-space cleanup.
- Explicit GetStdHandle/WriteFile system-component interop demonstrates terminal output. Full System.Console, filesystem/network/UTC services, ThreadPool/Task/async and broad BCL compatibility are not established here.

Finalization tests explicitly drain the queue before shutdown. No automatic finalization of all still-live objects on process exit is promised. Hardware null read/write and divide faults use actual runtime translation; stack exhaustion terminates the component and is not a catchable StackOverflowException or growing-stack implementation.

## Reproduce

Use the repository-pinned SDK and local toolchain. From the repository root:

```powershell
dotnet build WitOS.slnx --configuration Release
dotnet run --project tools/WitOS.Dev --configuration Release -- setup
dotnet run --project tools/WitOS.Dev --configuration Release -- runtime-audit
dotnet run --project tools/WitOS.Dev --configuration Release -- runtime-probe
dotnet run --project tools/WitOS.Dev --configuration Release -- runtime-target
dotnet run --project tools/WitOS.Dev --configuration Release -- runtime-source
dotnet test tests/WitOS.Dev.Tests --configuration Release --no-build
dotnet test tests/WitOS.Dev.Tests --configuration Release --no-build --filter TestCategory=Pe
dotnet run --project tools/WitOS.Dev --configuration Release -- test
dotnet run --project tools/WitOS.Dev --configuration Release -- runtime-config
dotnet run --project tools/WitOS.Dev --configuration Release -- runtime-boot-run
```

`setup` verifies/extracts the pinned QEMU package. `runtime-source` builds separate Windows-reference and WitOS native archives and the full guest driver; `runtime-boot-run` rebuilds the kernel and boots that hash-verified driver. `runtime-boot` combines source-build and guest execution. Hosted commands alone never establish guest execution. Artifacts and downloads remain ignored.

## Acceptance and provenance

Each QEMU RAM/CPU profile runs two image addresses twice, with four combined cycles per positive component. Each cycle checks GC/hardware and managed EH/finalization/threads/quota recovery, then inspects the real ThreadStore for exactly Main plus finalizer. Previous Thread observers are checked during slot reuse. Every component must return 42 and reclaim all component-owned backing/tables/handles/events. Failure components independently cover GC initialization, native fatal errors, raw/fault workers and actual managed stack exhaustion. Success requires semantic checks, strict per-execution protocol, correct QEMU exit and hashes; a marker or exit code alone is insufficient.

Upstream source remains at b82454cad0aaaae3db2cf18fbf2cccc36e201ccc; compiler/CoreLib packages are locked to 10.0.8 and VMR 94ea82652cdd4e0f8046b5bd5becbd11461482ca. Canonical-byte source audit and overlay hashes identify all local native changes. Existing corrections include GC bookkeeping alignment, checked startup registration/AllocHeap cleanup, thread/TLS/stack discovery and scoped foreign GC root walks; optional profile switches and the Windows-shaped native adapter are explicit. No replacement CoreLib or successful dummy runtime/OS bodies are used.

The hosted comparison shares the same managed source/object and requires the exact successful semantic reports. Windows uses its ordinary unconstrained thread creation; the guest additionally exercises its bounded thread quota and kernel teardown. Those platform-specific failure conditions are reported separately.

## Architecture gate

The current evidence supports a minimal native kernel hosting a managed system component in this bounded profile. It does not prove general application compatibility or zero porting effort. Native platform bindings, context/unwind integration and explicit upstream native corrections are substantial maintenance obligations. Keep P6 CoreCLR/JIT and platform services as separate work; preserve standard managed semantics and ordinary SDK/TFM/NuGet workflows.

Runner cleanup is intended to keep the original command deadline plus a five-second cleanup budget. The subsequent [quality audit](P5-Code-Quality-and-Coverage-Audit.md) found that a control callback ignoring cancellation can exceed this budget (Q1.3); [Q1](Q1-Quality-Hardening.md) now bounds that wait independently and tests late callback faults. On QEMU timeout it attempts a local stdio QMP capability/quit handshake, then retains forced owned-process/job termination as fallback. Timeout remains a failure for ordinary boot acceptance; the dedicated timeout test explicitly expects it. Serial and monitor logs are separate. Successful cleanup requires native process signaling and an empty job, independently of markers or QMP replies.

Q1 follow-up acceptance is complete: 16 guest executions require both redirect and return-address hijack separately, with 43 orderly workers per execution (two additional native-hosted managed callbacks). Standard managed Thread count remains 28 per execution. Evidence publication uses one schema-3 commit record with recoverable projections, and capture limits reject truncated success. LLVM native-parser coverage/ASan and a bounded libFuzzer lane complement the existing host and guest suites; their percentages do not describe whole-kernel or BCL coverage. See the [Q1 implementation and evidence](Q1-Quality-Hardening.md).
