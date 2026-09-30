# P1.7 GC OS policy

The selected workstation profile uses upstream software heap write-watch and manually maintained card bundles. OS write-watch and large-page allocation are unavailable. Their adapters report real failure; unexpected void reset fails closed. DebugBreak issues a real user-mode INT3. The full upstream collector remains in the native archive, and guest collector execution is still a later milestone.

## Write-watch exclusion evidence

RuntimeGcPolicy is now an obligatory runtime-source gate. It verifies a clean pinned upstream tree, the actual Runtime.WorkstationGC gcwks.cpp compile command and both FEATURE_USE_SOFTWARE_WRITE_WATCH_FOR_GC_HEAP and FEATURE_MANUALLY_MANAGED_CARD_BUNDLES definitions. It rejects explicit undefines and requires function COMDAT generation.

The pinned source has one definition and one call of update_card_table_bundle. The call is guarded by `#ifndef FEATURE_MANUALLY_MANAGED_CARD_BUNDLES`. Heap get/reset paths select SoftwareWriteWatch::GetDirty/ClearDirty under the software feature. Card bundles themselves remain enabled: can_use_write_watch_for_card_table returns true under the manual feature. SupportsWriteWatch()==false alone was therefore insufficient evidence.

The audit inspects every exact object in the real runtime archive, including relocations to defined external-linkage symbols. Code and data references are retained; only unwind/debug metadata is excluded. Exactly two OS watch references remain, both inside WKS::gc_heap::update_card_table_bundle, and no code/data reference enters that method. Unexpected callers or changed feature selection fail the build. Source and object hashes, compiler flags and actual relocation owners are recorded in artifacts/runtime-gc-policy/policy.json. The gc.cpp hash describes build worktree bytes; pinned canonical source provenance remains the existing audit/clean Git revision, not a newly invented source pin.

GetWriteWatch consequently returns false without reading or changing caller buffers or memory. SupportsWriteWatch remains false. ResetWriteWatch has no failure result, so any reached call terminates the component with private reason WIT_NATIVE_GC_WRITE_WATCH_EXIT (0xFFFF0005). It never reports a successful empty dirty-page list and never pretends that tracking was reset. No collector function, dependency or source body is removed to get a smaller link.

## Large pages

The user allocator supports ordinary 4 KiB mappings. VirtualReserveAndCommitLargePages returns nullptr without reserving or committing anything, including on oversize/invalid requests. It never substitutes ordinary pages: upstream skips ordinary commit/decommit for allocations it believes are large pages.

The minimal executable explicitly selects the supported upstream `System.GC.LargePages=false` runtime knob, and RuntimeReadiness requires that exact ILC option alongside workstation/non-concurrent GC. The broad inventory remains separate. In pinned gc.cpp, virtual_alloc selects the large-page method only when requested; null propagates through allocation attempts, and a failed regions-range reservation / reserve_initial_memory returns E_OUTOFMEMORY. There is no claimed automatic ordinary-page fallback or successful large-page initialization. A caller overriding the profile to require large pages receives failure.

## Architectural debug break

The architecture helper executes byte CC (INT3) with a named continuation address. IDT vector 3 now permits ring-3 entry, alongside the existing INT 0x80 syscall gate; other interrupt gates retain their privilege policy. The exception path captures the actual user trap and contains it within the component. There is no guest debugger attachment yet, and no managed exception translation is claimed here. Kernel-mode breakpoint behavior remains covered by the ordinary boot suite.

## Acceptance

The exact source-archive policy and breakpoint objects run in the COM/GC fixture. Modes 82/83 exercise refusal paths without compiler TLS and in native TLS workers, preserve last-error/errno, check count/address outputs, protect a committed page as no-access, and compare actual reservation/commit/owned-memory accounting. Ordinary reservation/commit/release still works. Mode 84 deliberately calls unsupported reset and verifies the private failure reason plus unchanged committed payload before teardown. Mode 85 checks contained vector 3, zero error code, user CS, continuation RIP and the actual CC instruction byte. Supervisor teardown must restore all pages and handles.

Validation passed: Release build, runtime-audit/probe/target/source/readiness and four runtime-config profiles with 245 user groups and 55 expected contained faults each. Full archive: 122 members; configuration archive: 37 exact objects. Minimal/broad strict link: 18/24 unresolved symbols, with no remaining GCToOSInterface requirement. The COM/GC image is 29184 bytes with 96 plain unwind records under the unchanged loader cap of 128. All 20 ordinary boot scenarios also passed. P1.7 is complete; P1.8 remains open.

The older standalone runtime-port memory-adapter fixture intentionally excludes the new full-runtime policy object and retains its negative missing-reset link check; it is not the complete runtime boundary. Full source/readiness now verifies the implemented four methods and the remaining real PAL/context/exception requirements. None of these checks means that .NET or the collector has executed inside WitOS.

Logs: artifacts/p1-gc-policy-config.log, artifacts/p1-gc-policy-test.log, artifacts/p1-gc-policy-audit.log and artifacts/p1-gc-policy-probe.log. `runtime-gc-policy` can also inspect the existing pinned build independently.
