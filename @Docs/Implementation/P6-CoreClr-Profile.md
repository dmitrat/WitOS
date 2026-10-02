# P6.1 — CoreCLR/JIT profile and platform boundary

**Status:** P6.1 complete; profile and hosted reference verified. Guest CoreCLR remains pending. **Date:** 2026-09-30. **Decision owner:** WitOS implementation within the approved upstream .NET / standard developer-experience architecture.

## Context and decision

The P1–Q1 baseline was published to private `dmitrat/WitOS` as `5d22d7545c3b887c30bf03f744c982bb32210b75`. P6 work remains local until separately authorized for publication.

Pin the same upstream runtime as M3: .NET 10.0.8, runtime commit `b82454cad0aaaae3db2cf18fbf2cccc36e201ccc`, package VMR `94ea82652cdd4e0f8046b5bd5becbd11461482ca`. The machine-readable contract is `experiments/CoreClrProbe/profile.json`; `coreclr-source` refuses a mismatch with the existing source/package lock, verifies the upstream origin/revision and clean tree, and uses a separate `coreclr-reference` output subtree.

Build actual x64 Release CoreCLR, RyuJIT and corerun with the unchanged upstream Windows native build entry (`-component runtime -component jit`). This reference gives an executable oracle and a concrete import surface. It is not a WitOS binary and is never treated as a guest port. Keep NativeAOT as a system implementation tool; applications remain ordinary unchanged net10.0 IL with standard SDK/TFM/NuGet.

For the guest, retain AMD64 PE/COFF compiler calling conventions and the established WitOS kernel/system-layer separation. Windows-shaped private bindings are an implementation aid, not Windows identity or Windows binary compatibility. The guest must identify as WitOS; upstream CoreLib/platform sources may receive an explicit platform port, but no replacement CoreLib, successful runtime/CRT stubs or application recompilation contract is allowed. Windows DLL implementations must not enter the guest image.

## Alternatives considered

- Continue extending NativeAOT as the application runtime: rejected, because this cannot satisfy unchanged IL, JIT, dynamic loading and runtime code generation.
- Port the Unix PAL first: keeps a portable PAL boundary but introduces POSIX signal, pthread, libc and ELF assumptions absent from the current kernel. This remains a source of reusable algorithms; it does not remove platform work.
- Use the existing AMD64/Win64 code-generation and unwind foundation with an explicit WitOS platform adapter: selected bring-up direction. Reuse mechanisms only after validating CoreCLR call sites and semantics; the existing NativeAOT adapter is not assumed complete.

## Executed evidence

`coreclr-source` built 2247 native compile-command entries and produced these reference binaries:

| Image | File bytes | Direct imported symbols | Delayed imported symbols |
| --- | ---: | ---: | ---: |
| coreclr.dll | 5,408,768 | 359 | 5 |
| clrjit.dll | 2,335,744 | 118 | 0 |
| corerun.exe | 218,112 | 122 | 0 |

CoreCLR + JIT have 382 distinct imported names, including ordinal identities; this is not a count of missing services. `NativeImports` now parses both directories structurally and preserves ordinals. The earlier exploratory dumpbin name count omitted ordinals and was not used for acceptance. The complete machine-readable list is `artifacts/coreclr-source/platform-boundary.json`, with a browsable `.md` companion. Group labels are navigation, not proof of support or dynamic reachability.

The ordinary SDK-built `CoreClrProbe.dll` passes through the source-built corerun/CoreCLR/JIT with exit 42:

- Dynamic code supported and compiled; ReadyToRun disabled during execution.
- A real emitted DynamicMethod executes.
- A closed generic is created and invoked through reflection.
- Actual GC/finalization and roots across managed exception handling.
- ThreadPool/Task and GC interaction.
- A collectible AssemblyLoadContext loads ordinary IL from a stream.
- The process module paths identify the actual source-built CoreCLR and JIT, not the installed runtime.

Standard CoreLib is taken from the installed 10.0.8 framework only after checking its product-version VMR against the package pin; its exact hash and the ordinary managed assembly hash are captured. These are Windows-hosted results. They do not close P6.5, P6.6 or P6.7 guest criteria. The schema-3 `artifacts/coreclr-source/current-run.json` commit record points to an immutable acceptance snapshot. Its run directory preserves native binaries, standard CoreLib, ordinary IL, profile, logs and boundary reports. `hosted.log` contains the exact expected reports. Legacy top-level reference files are archived; failed attempts cannot retain current success.

## Required WitOS work by boundary

| Boundary | Reuse candidate | Work that remains real and required |
| --- | --- | --- |
| Executable allocator | owned reservations, commit/protect/decommit, kernel page tables | P6.2: dynamic RX memory, no W+X virtual mapping, publication protocol, near-address allocations, mapping lifetime and synchronization. Upstream double mapping is an actual allocator policy; turning W^X off to request RWX is not an implementation. |
| Code registry/unwind | checked static-image AMD64 unwind, contexts and stack leases | P6.2: dynamic/multiple modules and JIT records, lookup/add/delete/callback lifetime, GC/exception readers versus code retirement. The static immutable-image cache cannot simply cover mutable JIT metadata. |
| Images/files/binding | validated single PE image, immutable startup/environment | P6.3/P6.4: framework/application assembly storage, read/seek/map semantics, metadata, multiple native modules, exports/imports and loader lifecycle; normal runtimeconfig/deps/framework binding. |
| Threading/TLS | kernel identities, references, waits, contexts, NativeAOT attachment experience | P6.4/P6.5/P6.7: actual CoreCLR ThreadStore/GC suspension, TLS/FLS contracts, critical sections/SRW/condition variables/semaphores, thread pool/timers and appropriate measured quotas. Four threads are a prototype bound, not the CoreCLR capacity target. |
| CoreLib/BCL platform | standard upstream sources and private native transport | P6.4–P6.7: correct WitOS identity and actual required platform calls. Imports include files, environment, clock/UTC, globalization, CRT/math and diagnostics beyond M3. |
| Windows-specific services | no blanket reuse | COM/WinRT/registry/token/impersonation and Windows resources need explicit platform exclusion or real services where applicable. No fake success. Ordinal OLEAUT imports are retained in the inventory, not hidden. |
| Developer workflow/compatibility | ordinary SDK/net10.0 assembly build | P6.8/P6.9: delivery and `dotnet Application.dll`, diagnostics, independent-OS build, unchanged hashes and representative portable NuGet tests. |

The selected source contains direct assumptions beyond imports: `vm/threads.cpp` reads `NtCurrentTeb`; `vm/threadstatics.cpp` accesses `ThreadLocalStoragePointer`; x64 assembly uses the TEB TLS offset. Existing compiler GS TLS is not a Windows TEB. These require explicit source adaptation and kernel-confirmed state, not a fake TEB/process heap.

Native DLLs alone exceed the current 1088 KiB PE cap, before CoreLib, JIT heaps or BCL data. Future limits must follow measured requirements and preserve ordinary/M3 admission policies. No quota is enlarged merely to silence a failed link or boot.

## Validation and next step

Commands: Release solution build; host regressions including direct/delayed/ordinal and malformed PE descriptors; `coreclr-source`; existing runtime audit/probe/target and kernel regressions where the shared tooling is affected. The actual source-built reference executes rather than merely links.

Final gates passed: Release without warnings/errors; 31 host groups and 555 PE inputs; the source-built CoreCLR/JIT hosted probe; existing runtime-audit/probe/target; full runtime-config matrix and all 20 kernel scenarios. Evidence: artifacts/p6-stage1-evidence.json; CoreCLR reference run 20260930T071126205-2b0fecdd69554e918e95c0097e39e94e. A remote baseline CI rollback-fixture budget failure was corrected locally without increasing the budget: all 18 failure boundaries across three APIs remain tested, using 0–1/10 ticks. Publishing that new correction awaits separate user permission. P6.2 is now the active stage: executable-memory ownership/protection/publication first, then dynamic code registration and unwind lifetime. Guest CoreCLR execution remains open.
