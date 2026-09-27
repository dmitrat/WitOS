# ADR 0007: Build the native runtime from the pinned upstream tree

**Status:** Implemented and verified locally on 2026-09-20.
**Scope:** Full upstream native libraries and an incomplete WitOS workstation archive. The 0.0.11 [discovery extension](NativeAot-Gc-Discovery.md) adds guest environment queries; managed execution in WitOS is still pending.

## Context and decision

The first guest adapter compiled five GC memory methods against upstream headers. The next requirement is to compile the actual runtime and collector, and show where the adapter meets their real dependencies.

Use the pinned upstream `src/coreclr/build-runtime.cmd -x64 -release -component nativeaot` build. Build two separate profiles, `reference` and `witos`, with separate CMake, object and install directories. Keep the upstream source tree unchanged. A small CMake project hook replaces `gc/windows/gcenv.windows.cpp`, with `src/Runtime.NativeAot/gcenv.witos.cpp` , `gc_events.witos.cpp` and `gc_time.witos.cpp` in `Runtime.WorkstationGC` after the upstream target is declared. It also replaces Crst.cpp with the checked Release adapter and mutex.c in aotminipal with the native WitOS mutex implementation. It adds the tested tls.witos.cpp dynamic TLS helpers and native_new.witos.cpp nothrow allocator to Runtime.WorkstationGC. It fails if the expected target/source/platform changes.

The WitOS profile additionally removes Windows PalCommon.cpp/PalMinWin.cpp and compiles the [partial WitOS PAL](NativeAot-Pal-Thread-Discovery.md). Missing services remain unresolved at the PAL interface.

Version 0.0.23 also compiles the shared System.Native/image.c descriptor/context implementation and the [PAL module adapter](NativeAot-Pal-Module-Discovery.md), verifying both archive objects byte-for-byte.

The source commit is `b82454cad0aaaae3db2cf18fbf2cccc36e201ccc` (.NET 10.0.8). The tool fetches that exact commit into an ignored sparse checkout containing `eng`, `src/coreclr` and `src/native`, verifies the repository/revision and refuses a dirty or mismatched checkout. It checks cleanliness again after building. Git commits pin the full native source/build tree; the current 58-file SHA-256 audit includes the GC/Crst inputs, PAL declaration closure and RhConfig/GCConfig sources; the runtime revision and VMR/package provenance remain unchanged.

The upstream [native build entry](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/build-runtime.cmd) and [full runtime target](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/nativeaot/Runtime/Full/CMakeLists.txt) define the build. Generated event headers, assembly offsets and support libraries remain part of that recipe.

Alternatives were a hand-maintained list of runtime source files or modification of a prebuilt archive. Both would obscure dependencies and make upstream updates harder to assess. The original slice retained Windows PAL implementations. Version 0.0.19 instead exposes the incomplete platform contract by replacing those files with the partial WitOS PAL. The selected hook preserves real missing-symbol failures while integrating tested GC, locking, allocation, TLS and thread-discovery adapters.

## Reproduction

```powershell
dotnet build WitOS.slnx --configuration Release
dotnet run --project tools/WitOS.Dev --configuration Release -- runtime-source
```

Requirements: Windows x64, the existing .NET/MSVC/SDK tools, Git, Python 3, and CMake/Ninja (Visual Studio C++ CMake tools are supported). The initial source fetch needs network access and additional disk space. The upstream batch build currently requires ASCII workspace paths without command-shell metacharacters. Up to four native jobs run concurrently.

The command refreshes `runtime-target` first, obtaining the real ILC object, native Windows test host, link response and locked-package evidence. It then builds the complete upstream `nativeaot` CMake component twice. All outputs stay in ignored `.tools/upstream/runtime-10.0.8/artifacts/`; reports are in `artifacts/runtime-source/`.

Compiler and managed CoreLib remain the locked published 10.0.8 packages. This is a source build of the native libraries, not a source build of the entire .NET product. Upstream developer version headers and local compiler versions also mean the outputs are not claimed to be byte-identical to Microsoft's published packages.

## Executable evidence

The local run produced:

- 67 compile units/archive members in the Windows reference and 83 in the WitOS `Runtime.WorkstationGC.lib`, including real collector, handle-table, startup, thread, TypeManager and assembly code.
- The Windows GC environment and Crst in the reference target; twenty-four WitOS adapter/helper or explicitly derived sources in the selected workstation target. Both aotminipal archives contain eleven members, with mutex.c replaced by mutex.witos.cpp only in the WitOS profile. Source/member inventories reject the old objects there; each adapter object occurs exactly once, verified byte-for-byte.
- A reference DLL relinked with all 13 captured native library/object inputs replaced by their source-built counterparts. Standard Windows/CRT libraries remain available for this positive reference.
- Four passing native-host groups with the source-built Windows runtime: initial exported entry, allocations/GC/exceptions, TLS across two native threads and repeated entry.
- A strict link of the real static ILC workload plus source-built WitOS-profile inputs, without OS/CRT libraries or forced linking: 98 unresolved symbols after the 0.0.27 process-exit extension; 99 after the 0.0.26 PalInit extension; 100 after native C/configuration support; 105 after environment/string support; 108 after module discovery; 110 after last-error; 114 after detached workers; 116 after PAL memory/waits; 125 after the 0.0.19 PAL replacement (134 after dynamic TLS; 136 after native allocation; 162 at the initial source-build milestone). The new count reclassifies missing Windows internals as missing PAL services and is not a direct completion measure. Implemented GC memory/discovery/event/time and mutex methods resolve; the four Windows critical-section imports are absent; core runtime helpers such as `RhpReversePInvoke` resolve.

Of the unresolved symbols, seven belong to GC environment methods, five are `wit_native_*` glue symbols, 17 belong to the explicit PAL boundary, and 69 are other platform/runtime requirements. Counts are diagnostic, not a compatibility percentage or a frozen cross-toolchain contract. The five glue functions already exist in the guest x64 assembly; this broad link deliberately excludes the fixture entry/transport object. It is not a missing kernel mechanism.

The tool requires the expected incomplete-port failure with only unresolved-symbol diagnostics, checks representative missing and resolved boundaries, and records the full inventory. It does not supply successful placeholder methods. The WitOS archive is never loaded into Windows or into the guest as a full runtime.

## Reports and CI

`source-build-report.json` records the source commit/tree, overlay/input/archive/image hashes, archive members, compile-unit counts, reference execution and unresolved-symbol groups. Separate compile-command, member-list, build, reference-host and strict-link logs preserve the evidence. `missing-platform.md` groups the remaining symbols. These reports and the source-built reference image are uploaded by the NativeAOT workflow.

The workflow triggers on the source overlay, native adapter headers and tooling as well as runtime experiments. It fetches native sources in a clean runner, instead of relying on the developer's checkout or installed prebuilt runtime archive. Kernel CI retains all 18 VM scenarios, 162 user groups and 51 contained user faults. Local source build, source audit, hosted probes and kernel suite passed.

The 0.0.24 [environment/string adapter](NativeAot-Pal-Environment.md) and direct/import assembly bindings are also compiled and verified byte-for-byte. PalInit is now implemented; PalAttachThread and PalInitComAndFlsSlot remain explicit missing requirements.

The 0.0.25 [configuration probe](NativeAot-Runtime-Configuration.md) introduced a separate four-object archive; the PalInit extension grows it to five verified objects. Workstation compiles native C string/integer functions and an explicitly derived RhConfig source with allocation-failure checks. Reports retain clean-checkout evidence and record that correction separately. The runtime-config command boots the actual selected methods in QEMU.

The 0.0.27 [process-exit adapter](NativeAot-Process-Exit.md) supplies real atexit registration. A complete generated startup.cpp retains upstream dependencies while checking registration failure; its exact object is verified in the archive. The reference source remains unchanged.

The 0.0.28 [interface-dispatch startup probe](NativeAot-Interface-Dispatch-Startup.md) grows the separate archive to nine objects. The full WitOS runtime uses the complete derived AllocHeap with explicit Crst destruction; full dispatch sources retain their original dependencies. Guest startup slices do not replace them in the runtime archive.

The 0.0.29 [RuntimeInstance probe](NativeAot-Runtime-Instance.md) grows the dedicated archive to eleven objects. The complete WitOS ThreadStore source uses kernel-confirmed compiler TLS for its startup metadata; both allocation-failure paths and real object creation run in the guest. Attachment/collector dependencies remain outside the probe and unresolved in the full port.

## Next boundary

The full native compilation path is established. GC OS environment initialization and discovery are implemented; the dedicated probe now executes the real GCConfig initialization path. The probe now also executes [PalInit](NativeAot-Pal-Initialization.md). GC events now support finite waits using monotonic time. Recursive minipal/Release Crst locks are now implemented. Continue through RhInitialize/InitDLL, ThreadStore attachment, module registration and exception/GC coordination. The generated symbol inventory is an input to that work, not a reason to implement unrelated Windows APIs.

The overlay replaces the main Windows PAL files, while other upstream Windows feature/CRT dependencies remain. Static/dynamic compiler TLS and native module bounds work, while full guest PAL, managed TLS/ThreadStore attachment, GC rendezvous and runtime fault unwinding remain pending. Baseline guest instruction safety and larger image/commit/stack limits must also be addressed before loading the full runtime. M3 still requires actual managed execution inside WitOS.
