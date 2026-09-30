# P1.2: Executable startup platform boundary

Status: decisions recorded; implementation and full guest acceptance remain P1.3-P1.10.
Baseline: WitOS 0.0.44, upstream runtime b82454cad0aaaae3db2cf18fbf2cccc36e201ccc (.NET 10.0.8).

## Scope and decision

Keep the existing x64 PE/static executable, one-online-CPU, FXSAVE-only architecture and ordinary upstream CoreLib. Select workstation, non-concurrent GC for the first executable. The real ILC command line contains `--runtimeknob:System.GC.Server=false` and `--runtimeknob:System.GC.Concurrent=false`; RuntimeReadiness now rejects their absence. The ordinary hosted executable executes a real collection. Existing native guest GCConfig tests also verify false ConcurrentGC through environment precedence. None of this claims that the collector already executes in WitOS.

Keep real finalization, runtime attachment, root enumeration, suspension and exception/unwind requirements. Non-concurrent GC does not remove native workers, the finalizer, safepoints, managed/native transitions or shutdown. Do not remove those paths to obtain a smaller link.

Windows reference libraries and application compatibility requirements remain unchanged. Every needed service must be implemented or have a proven, explicit unsupported/optional contract. No symbol is resolved by this decision record.

## Reproducible evidence

`RuntimePlatformBoundary` now generates `artifacts/runtime-readiness/platform-boundary.json` and `.md` during runtime-readiness/runtime-source/runtime-config. It classifies every unresolved startup symbol; an unclassified symbol fails the run. The report includes:

- Exact native object/member and linker-reported referencing function where available.
- Actual undefined-symbol relocations in the managed ILC COFF object, with section, offset, relocation kind and nearest preceding external symbol in that section.
- ILC object SHA-256 and real runtime knobs.
- Required work package and policy for each dependency.

COFF owners identify containing symbols, not a dynamic call graph. Some references are data, and linker inclusion does not prove execution in Main. Source call sites and their error branches must still be checked before excluding a path. `NativeObject` validates relocation indices/ranges and collects this extra evidence only when requested.

Current evidence covers all 64 minimal unresolved names. The broad reference retains 70; its existing strict checks remain intact. The managed object, true wmain bootstrap, transport/TLS metadata and all unresolved runtime dependencies remain linked exactly as before.

## Group decisions and observed call sites

| Group | Observed actual reference | Decision / implementation gate |
| --- | --- | --- |
| Runtime attachment | `ThreadStore::AttachCurrentThread` -> PalAttachThread; `FinalizerStart` -> PalInitComAndFlsSlot | Required. Bind to real native exit notification and upstream RuntimeThreadShutdown/GC cleanup. P1.8; no successful dummy FLS initialization. |
| Context/hijack/CET | `Thread::HijackCallback`, `EnsureRedirectionContext`, `RhpSuspendRedirected`, `RhpVectoredExceptionHandler` | Required contexts and suspension. Any no-CET policy must be checked against kernel hardware state. Do not equate thread ID with context capability. P1.8/P3. |
| Names/image identity | `FinalizerStart`, `RhSetCurrentThreadName`, `RhGetModuleFileName` | Implement real component image identity and per-thread naming/ownership. P1.6. |
| GC OS | `update_card_table_bundle`, `reserve_initial_memory`, `GCHeap::Alloc` | Keep real card-table and memory contracts. Software heap write-watch does not automatically remove card-bundle OS write-watch calls. Large-page failure must follow verified upstream fallback; no invented success or ignored void reset. P1.7. |
| Handles/identity | CoreLib `Thread::GetOSHandleForCurrentThread`, priority helpers and native `Thread::Destroy` | Real handle/reference lifetime and rights. Pseudo current-thread/process handles are not duplicate/context capabilities. P1.5. |
| Memory/waits | CoreLib `FrozenObjectSegment` constructor/TryAllocateObject; `Lock::TryEnterSlow`, `Thread::Sleep`, `WaitHandle::WaitForMultipleObjectsIgnoringSyncContext` | Required even for minimal executable. Adapt real memory/events/time; do not infer that no application-created threads means no wait/handle dependency. P1.5. |
| COM apartment lifecycle | CoreLib `Thread::InitializeCom`, `GetCurrentApartmentState`, `UninitializeCom`; native `FinalizerStart` | Required reached initialization/refcount/query lifecycle; general COM activation is not added. E_NOTIMPL throws PlatformNotSupportedException in current CoreLib, so a blanket unavailable stub is not acceptable as MTA initialization. Implement actual scoped apartment state or demonstrate an upstream-supported alternative before closing P1.6/P1.8. |
| Console/module access | CoreLib `Internal.Console::WriteCore`, `Console.Error::Write`, `Environment::GetProcessPath`; native RhInitialize | Real console/image services. Replace Windows startup kernel32 discovery by an explicit source adaptation to WitOS exception facilities; no fabricated module/procedure addresses. P1.5/P1.6/P1.8. |
| UTF/formatting/math | CoreLib signed-byte String constructor/console conversion; native `log_init_error_to_host`, `ee_alloc_context::ComputeGeometricRandom` | Required conversion ownership, formatting and genuine numerical behavior. `_fltused` is the compiler's floating-point-use marker, not an emulator. P1.3/P1.6. |
| Event log/debugger | CoreLib `EventReporter::ClrReportEvent` and runtime debugger helpers | Event log is optional only through actual failure behavior: ClrReportEvent returns when registration returns a null handle. Preserve console/fail-fast diagnostics; no fake event-log handles. Debugger discovery must report actual support. P1.6. |
| Exceptions/unwind | CoreLib `RuntimeExceptionHelpers::FailFast`; native RhInitialize, EHHelpers and CoffNativeCodeManager | Required real fail-fast/exception dispatch and unwind. No disabling managed exceptions, fake unwind frames or C-specific handler success. P1.8/P3. |
| GS/CFG | ILC CoreLib buffers and native GC/PAL functions; native indirect calls | Real GS cookie, initialization/check and unwind validation. For the initial WitOS compiler profile choose no Windows CFG/EH-continuation instrumentation, consistently, through supported compiler settings; do not create dummy dispatch pointers. Reference profile unchanged. P1.3. |
| Randomness | CoreLib `Interop::GetRandomBytes` | Required entropy-backed generation; native TLS PRNG is insufficient. P1.4. |

## Compiler profile rationale

The pinned upstream `eng/native/configurecompiler.cmake` defines per-target inherited `CLR_CONTROL_FLOW_GUARD` and `CLR_EH_CONTINUATION` properties. The NativeAOT package exposes ControlFlowGuard settings; the current minimal ILC response has no `--guard:cf`. A coherent initial WitOS profile can select these options off rather than emulating a Windows CFG loader with a jump-through-RAX stub. P1.3 must implement and verify this selection in actual compile commands/objects and preserve the Windows-reference build. This does not disable /GS, PE section protection, capability checks, exception semantics or any existing test; it does not claim CFI/CET support.

Alternatives rejected: linking the Windows CRT/PAL; /FORCE; successful fake OS helpers; replacing CoreLib; disabling GC/finalizers/exception machinery; dropping all native security checks. Full CFG is a possible later platform feature requiring loader tables and a real validated dispatch path, not an unresolved-symbol workaround.

## Dependencies exposed by this audit

P1.3 cookie initialization requires the entropy contract in P1.4 before its final positive guest acceptance. Implementing code/check behavior first is possible, but startup must remain blocked until entropy and initialization ordering are real. P1.8 likewise depends on the P3 mechanisms already identified in PLAN.md. These dependencies do not permit marking the earlier item complete on link-only evidence.

The next implementation work is P1.3: genuine math/formatting/compiler helpers and an explicit coherent compiler profile. Keep the overall P1 goal open until the real driver and complete runtime link meet P1.10.

## P1.2 validation

Release build, all 19 boot scenarios, runtime-audit (66 pinned files), hosted runtime-probe, runtime-target/source/readiness and all four runtime-config profiles passed (212 user groups / 54 contained faults each). The final report covers 64 unique unresolved names with linker evidence; every unresolved name attributed to NativeAotBoot.obj has actual COFF relocation evidence. Work remains local and P1 as a whole remains incomplete.
