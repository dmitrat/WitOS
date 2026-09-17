# ADR 0007: Build the native runtime from the pinned upstream tree

**Status:** Implemented and verified locally on 2026-09-17.
**Scope:** Full upstream native libraries and an incomplete WitOS workstation archive. The 0.0.11 [discovery extension](NativeAot-Gc-Discovery.md) adds guest environment queries; managed execution in WitOS is still pending.

## Context and decision

The first guest adapter compiled five GC memory methods against upstream headers. The next requirement is to compile the actual runtime and collector, and show where the adapter meets their real dependencies.

Use the pinned upstream `src/coreclr/build-runtime.cmd -x64 -release -component nativeaot` build. Build two separate profiles, `reference` and `witos`, with separate CMake, object and install directories. Keep the upstream source tree unchanged. A small CMake project hook replaces exactly one source, `gc/windows/gcenv.windows.cpp`, with `src/Runtime.NativeAot/gcenv.witos.cpp` and `gc_events.witos.cpp` in `Runtime.WorkstationGC` after the upstream target is declared. It fails if the expected target/source/platform changes.

The source commit is `b82454cad0aaaae3db2cf18fbf2cccc36e201ccc` (.NET 10.0.8). The tool fetches that exact commit into an ignored sparse checkout containing `eng`, `src/coreclr` and `src/native`, verifies the repository/revision and refuses a dirty or mismatched checkout. It checks cleanliness again after building. Git commits pin the full native source/build tree; the current 32-file SHA-256 audit and VMR/package provenance remain unchanged.

The upstream [native build entry](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/build-runtime.cmd) and [full runtime target](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/nativeaot/Runtime/Full/CMakeLists.txt) define the build. Generated event headers, assembly offsets and support libraries remain part of that recipe.

Alternatives were a hand-maintained list of runtime source files or modification of a prebuilt archive. Both would obscure dependencies and make upstream updates harder to assess. Replacing the complete Windows PAL now would require implementations we do not yet have. The selected hook changes only the already-tested memory environment and preserves real missing-symbol failures.

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

- 67 compile units/archive members in the Windows reference and 68 in the WitOS `Runtime.WorkstationGC.lib`, including real collector, handle-table, startup, thread, TypeManager and assembly code.
- The Windows GC environment in the reference target; the two WitOS adapter sources in the selected workstation target. Each adapter object's bytes occur exactly once in the resulting archive.
- A reference DLL relinked with all 13 captured native library/object inputs replaced by their source-built counterparts. Standard Windows/CRT libraries remain available for this positive reference.
- Four passing native-host groups with the source-built Windows runtime: initial exported entry, allocations/GC/exceptions, TLS across two native threads and repeated entry.
- A strict link of the real static ILC workload plus source-built WitOS-profile inputs, without OS/CRT libraries or forced linking: 150 unresolved symbols after the 0.0.12 event extension (162 at the initial source-build milestone). Implemented GC memory/discovery/event methods resolve; core runtime helpers such as `RhpReversePInvoke` resolve.

Of the unresolved symbols, 12 belong to GC environment methods, four are `wit_native_*` glue symbols, and 134 are other platform/runtime requirements. Counts are diagnostic, not a compatibility percentage or a frozen cross-toolchain contract. The four glue functions already exist in the guest x64 assembly; this broad link deliberately excludes the fixture entry/transport object. It is not a missing kernel mechanism.

The tool requires the expected incomplete-port failure with only unresolved-symbol diagnostics, checks representative missing and resolved boundaries, and records the full inventory. It does not supply successful placeholder methods. The WitOS archive is never loaded into Windows or into the guest as a full runtime.

## Reports and CI

`source-build-report.json` records the source commit/tree, overlay/input/archive/image hashes, archive members, compile-unit counts, reference execution and unresolved-symbol groups. Separate compile-command, member-list, build, reference-host and strict-link logs preserve the evidence. `missing-platform.md` groups the remaining symbols. These reports and the source-built reference image are uploaded by the NativeAOT workflow.

The workflow triggers on the source overlay, native adapter headers and tooling as well as runtime experiments. It fetches native sources in a clean runner, instead of relying on the developer's checkout or installed prebuilt runtime archive. Kernel CI retains all 17 VM scenarios, 100 user groups and 33 contained user faults. Local source build, source audit, hosted probes and kernel suite passed.

## Next boundary

The full native compilation path is established. Environment initialization and discovery are now implemented. GC events now support polling/infinite waits. Next implement compatible timing, finite waits and remaining native locks, then continue with PAL/thread attachment/TLS, native allocation support, module registration and exception/GC coordination. The generated symbol inventory is an input to that work, not a reason to implement unrelated Windows APIs.

The overlay still compiles most Windows platform paths. No complete guest PAL, compiler/managed TLS, GC rendezvous or runtime fault unwinder exists yet. Baseline guest instruction safety and larger image/commit/stack limits must also be addressed before loading the full runtime. M3 still requires actual managed execution inside WitOS.
