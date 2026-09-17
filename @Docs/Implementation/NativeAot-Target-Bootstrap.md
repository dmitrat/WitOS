# ADR 0003: NativeAOT target artifacts and native bootstrap

**Status:** Reproducible experiment implemented; candidate format/ABI selected for the next loader experiment.
**Date:** 2026-09-17.
**Guest status:** WitOS remains 0.0.7. No NativeAOT runtime has been started inside the guest.

## Decision and scope

Use AMD64 COFF / PE32+ and the Microsoft x64 calling convention as the first measured integration candidate. They match the existing MSVC/MASM environment and native WitOS call convention. The published win-x64 NativeAOT compiler can produce inspectable objects without a custom compiler build.

This selects an experimental object/call boundary, not the runtime's final OS backend. The Windows PAL, CoreLib platform paths, TLS and exception integration are still unported. A Windows compatibility personality has not been selected or implemented.

| Option | Evidence and trade-off |
| --- | --- |
| PE/COFF + Microsoft x64 | Measured here with pinned upstream packages; matches current build/CPU boundary; Windows runtime dependencies require explicit adaptation |
| ELF + System V x64 | Supported upstream Unix runtime surface, but needs a separate Linux build/link environment and changes to the current native boundary; not measured here |
| Custom ILC OS target and ABI | Could reduce legacy platform assumptions, but increases compiler/CoreLib maintenance before a first bootstrap; deferred |

The official [cross-compilation guidance](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/cross-compile) does not support cross-OS NativeAOT publishing. This experiment uses a supported Windows host/target pair and does not present it as a WitOS target RID.

## Reproduction

From the repository root:

```powershell
dotnet build WitOS.slnx --configuration Release
dotnet run --project tools/WitOS.Dev --configuration Release -- runtime-target
```

The command verifies the existing source lock, uses locked .NET 10.0.8 packages, publishes Static and Shared variants, inspects their artifacts, builds a C host and executes it in a separate process. It also performs two intentionally unsuccessful native links and validates their exact failure category.

Outputs live under `artifacts/runtime-target/`:

- `static/NativeAotTarget.lib`: archive containing the exact ILC-generated COFF object, verified byte-for-byte;
- `static/native/`: object, compiler arguments and ILC metadata map;
- `shared/NativeAotTarget.dll`: real hosted NativeAOT library;
- `shared/native_host.exe`: C executable with no CRT or CoreCLR imports; only Kernel32 is imported;
- `without-runtime.log` and `without-platform.log`: strict-link dependency evidence;
- `target-report.json` and `target-report.md`: object, image, provenance, hashes and readiness limits.

Static and Shared use separate native intermediate/output paths. Otherwise the shared link's import library can overwrite the static archive under the same filename and fool an incremental publish. The experiment verifies archive content on every run, including repeated runs.

## What actually executes

`experiments/NativeAotTarget/Exports.cs` uses ordinary upstream System.Private.CoreLib. Two UnmanagedCallersOnly exports expose version and workload checks.

The dedicated C process loads the DLL from its application directory (dependencies restricted to that directory and System32), then:

1. Calls the first export and checks the actual runtime version.
2. Executes managed object/large-array allocations, forced GC, roots, catch and finally.
3. Calls the module from two native Windows threads; each must begin with its own ThreadStatic sequence.
4. Calls it again on the original thread and verifies that its TLS sequence survived.

The host has no managed entry point and does not start CoreCLR. NativeAOT's normal DLL bootstrap and reverse-P/Invoke machinery perform initialization and native-thread attachment. No allocation, GC, exception or thread helper is replaced by a successful stub.

All four groups passed locally. The existing six-group hosted reference probe also passed unchanged. This is Windows bootstrap evidence, not guest runtime evidence. NativeAOT DLL unloading is unsupported, so the dedicated host process exits instead of attempting FreeLibrary; see the [upstream native-library sample](https://github.com/dotnet/samples/blob/main/core/nativeaot/NativeLibrary/README.md).

## Measured local reference

Profile: .NET/ILC 10.0.8; x86-64 instruction-set selection; size optimization; invariant globalization; workstation/non-concurrent GC; portable ThreadPool. Source and package revisions remain the [RFC 0015 pins](../RFC-0015-DotNet-Runtime-Port-and-Compatibility-Contract.md). Native host/linker: MSVC 14.51.

| Observation | Local result |
| --- | --- |
| ILC COFF sections | 1,547 |
| Object undefined externals | 118 |
| Strict link without native runtime | 118 unresolved symbols |
| Strict link with native runtime, no OS/CRT | 159 unresolved symbols |
| Hosted PE direct imports | 124 symbols from 10 libraries |
| Mapped image extent | 950,272 bytes: 232 pages before page tables, stacks or GC heap |
| Windows TLS template | 289 bytes and one callback |
| x64 unwind entries | 2,599 |
| Preferred PE image base | 0x180000000 |
| COFF relocation kinds | REL32, SECREL, ADDR32NB, SECTION, ADDR64 |

These are measured reference values, not portable golden counts. MSVC/SDK changes can change the linked DLL while the pinned managed compiler/runtime inputs remain constant. The JSON report records actual native input hashes and structural requirements per run.

The linked image has RX code, read-only metadata, RW data with a substantial zero-filled tail, a runtime-function table and DIR64 base relocations. Its preferred base is outside the current controlled user arena, so future mapping needs relocation or a deliberate layout change. Its extent already exceeds the current 128-frame component quota.

The emitted required-CPU-feature word was zero for the selected baseline. This is not proof that every path in the prebuilt native runtime libraries is compatible with WitOS's x87/SSE-only context management.

## The two link boundaries

NativeLib=Static archives the ILC object; it does not bundle a complete portable native runtime. The experiment checks the archive member against the exact generated object.

The first diagnostic link roots the two exported methods and supplies the static archive only. Unresolved symbols include `RhpReversePInvoke`, allocation/GC helpers and `_tls_index`, plus platform functions emitted by CoreLib.

The second also supplies the captured, pinned native libraries/objects and roots `RhInitialize`, `RhRegisterOSModule` and `InitializeModules`. Reverse-P/Invoke then resolves, but Windows/CRT/TLS dependencies remain, including VirtualAlloc, native waits, critical sections and unwind/context services.

Both use /NODEFAULTLIB and /NOENTRY. /NOENTRY is a diagnostic choice, not a proposed guest bootstrap. No SDK/CRT import libraries, /FORCE option or dummy symbol definitions are supplied. Only LNK2001/LNK2019 followed by LNK1120 count as the expected negative result; timeout, missing files or a successfully produced DLL fail the experiment.

Undefined object symbols, selected-link unresolved symbols, PE IAT imports and actually executed calls are different sets. None is a complete WitOS syscall list.

## Loader and runtime requirements exposed

The pinned [bootstrap source](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/nativeaot/Bootstrap/main.cpp) establishes an initialization callback, initializes the runtime, registers code/unboxing ranges and class-library callbacks, initializes modules, then runs the library startup.

The [runtime startup source](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/nativeaot/Runtime/startup.cpp) includes GC and exception-handler initialization. The CLR header being absent does not eliminate this work.

The artifacts make these remaining tasks concrete:

- Load multiple protected PE sections, zero-fill data and handle base relocations.
- Define module lifetime, registration boundaries and bootstrap order.
- Adapt compiler/runtime TLS and initialization callbacks; a raw FS page is not a Windows TLS implementation.
- Supply native runtime/PAL/CoreLib platform behavior or replace the selected backend deliberately from source.
- Integrate unwind tables, managed/hardware fault delivery and GC rendezvous.
- Expand image/stack/commit limits and replace provisional clocks where the selected runtime profile requires it.

The current inspector is an analysis tool, not a complete validating guest loader. Its COFF/PE structures follow the [Microsoft format reference](https://learn.microsoft.com/en-us/windows/win32/debug/pe-format); truncated object/image and mismatched archive inputs are rejected in the experiment.

## Next bounded step

Implement and test a guest PE image/metadata loading contract using controlled native images: separated RX/RO/RW sections, zero-fill, relocations and explicit rejection of unsupported import/TLS features. Use this module's measured requirements to drive that work. In parallel with that progression, choose the actual source-level runtime backend and record its patch set.

Do not load the unadapted Windows DLL in ring 0, bypass reverse-P/Invoke/GC initialization, substitute a fake CoreLib or call a leaf C# method and label it a completed runtime port. M3 still requires real allocations/GC, finalization, exceptions and thread activity inside WitOS.
