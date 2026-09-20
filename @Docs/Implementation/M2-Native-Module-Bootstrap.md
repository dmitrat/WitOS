# ADR 0005: Native image handoff and user-space bootstrap

**Status:** Implemented in WitOS 0.0.9.
**Date:** 2026-09-17.
**Scope:** Native C startup inside the guest. NativeAOT itself is still not running there.

## Source-backed boundary

The pinned NativeAOT bootstrap calls RhInitialize, registers the native image/code ranges and class-library callbacks, then calls InitializeModules. That last operation already uses managed arrays, GC handles, frozen-object registration, static-base initialization and eager constructors.

Those managed operations belong to the runtime in user space. The kernel should describe and protect an image, not interpret ReadyToRun rows or implement a substitute TypeManager/GC.

At this milestone the source audit pinned 26 files, adding:

- [StartupCodeHelpers.cs](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/nativeaot/Common/src/Internal/Runtime/CompilerHelpers/StartupCodeHelpers.cs)
- [ModuleHeaders.h](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/nativeaot/Runtime/inc/ModuleHeaders.h)
- [TypeManager.cpp](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/nativeaot/Runtime/TypeManager.cpp)
- [RuntimeInstance.cpp](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/nativeaot/Runtime/RuntimeInstance.cpp)

Repository/package revisions remain unchanged. The source lock records checksums of the exact downloaded bytes.

## Decision and alternatives

| Option | Assessment |
| --- | --- |
| Register/initialize managed modules inside the kernel | Rejected: couples the kernel to runtime metadata and GC internals |
| Expose a readonly native image description; initialize in user space | Chosen: enough information to identify this native module and validate native startup ranges |
| Parse arbitrary DLL graphs and automatically invoke Windows CRT/TLS callbacks | Deferred: requires explicit linking, TLS, exception and lifetime contracts |

The new startup helper is ordinary freestanding C in `src/System.Native/`. Its syscall/entry/atomic-claim shim is x64 assembly under `Kernel.Arch.X64`, linked into the user PE only. It is not a replacement CoreLib, Windows CRT or implementation of NativeAOT's InitializeModules.

## ABI v5 image handoff

This section records the introduced v5 layout. [ABI v6](NativeAot-Gc-Discovery.md) keeps the same 24-byte prefix and adds MemoryQuery; current startup Version is 8 after the [thread-identity/mutex extension](NativeAot-Mutexes.md).

The startup prefix grows from 16 to 24 bytes:

| Offset | Field |
| --- | --- |
| 0 | Version = 5 (uint32) |
| 4 | Size = 24 (uint32) |
| 8 | Console handle (uint64) |
| 16 | ImageInfo user address (uint64) |

Raw one-page fixtures receive a zero ImageInfo. PE components receive a pointer into the existing readonly/NX startup page. Syscall numbers and transport remain unchanged; this replaces the earlier experimental prefix.

ImageInfo version 1 occupies 304 bytes and describes the actual relocated image:

- actual base, entry and image size;
- initialized header size;
- up to sixteen section ranges: RVA, virtual size, initialized size and R/W/X flags;
- RVA/size of the accepted x64 runtime-function table;
- zero reserved fields.

Initialized size excludes BSS and file padding beyond the declared virtual section. The descriptor exposes no kernel or physical addresses. It remains valid for the component lifetime; there is still one loaded native image per component.

The user-space address lookup returns this image's base for header/section addresses and zero outside those ranges. It supplies a building block for a future module-from-pointer adapter, not a cross-process authority or a complete dynamic-module registry.

## Native startup lifecycle

`wit_native_bootstrap` accepts a zero-initialized module context, the kernel handoff, a readonly table of up to sixteen initializer/cleanup pairs, a main function and an output exit code.

The helper:

1. Atomically claims New → Initializing. Reentrant, repeated or competing calls return AlreadyStarted.
2. Validates the descriptor, entire callback table and main/callback address ranges before calling any initializer.
3. Runs initializers in table order.
4. Enters main only after all initializers succeed.
5. Cleans completed initializers in reverse order, then records Stopped or Failed.

Callback records must be initialized readonly/non-executable image data. Function addresses must lie in initialized RX image bytes. This checks ranges and permissions; it does not prove function signatures or machine-code behavior. The module context is component-owned mutable state, not a kernel-enforced security token.

A failed initializer supplies a failure code, skips main and triggers cleanup only for earlier successful initializers. A failing initializer owns cleanup of its own partial work. Main's return code survives normal cleanup. Failed/stopped contexts cannot be reused; a new lifetime needs a new context/component.

Cleanup callbacks have no failure return channel. A CPU fault in startup/cleanup follows the ordinary whole-component fault path; the kernel closes handles and later reclaims pages instead of trying to continue a damaged C call chain.

The assembly entry gives C the correct stack/shadow space and converts the returned status into component Exit. The C syscall bridge preserves the established INT 0x80 contract. No CRT imports or kernel-mode calls to user callbacks are involved.

## Bounded unwind metadata

Ordinary compiled C functions emit x64 runtime-function/unwind data even when they use no language exceptions. The PE profile now accepts up to 128 sorted, nonoverlapping function records with initialized RX code ranges and readonly unwind information.

The parser checks version-1 headers, aligned spans, prolog/slot bounds, ordinary nonvolatile save and stack/frame encodings. Handler flags, chained records, machine frames and unsupported versions/opcodes are rejected. Relocations cannot modify the function table or accepted unwind records after validation.

The structures follow the [Microsoft x64 unwind format](https://learn.microsoft.com/en-us/cpp/build/exception-handling-x64?view=msvc-170). Validation is structural: it does not verify that metadata describes the actual instructions. No stack walking, exception dispatch, Windows handler execution or managed unwinding is implemented. The readonly descriptor merely exposes the validated table range to future user-space support.

## Guest evidence

`BootstrapFixture.pe` is compiled from the startup helper plus `tests/User.X64/bootstrap.c`, with a native assembly entry and no CRT/default libraries. The host requires no imports or TLS directory and checks that real relocation and unwind directories exist. A local reference contains thirteen unwind entries.

The fixture creates actual WitOS memory reservations/committed pages and an event during initialization. It uses them in main, then closes/releases them through native cleanup. An atomic run-once test calls the same startup context from two guest C threads. Invalid callback tables/functions and descriptor versions fail before initialization.

At version 0.0.9, seventeen VM scenarios required 83 M2 groups and 29 contained user faults. Eleven new groups cover:

| Group | Evidence |
| --- | --- |
| BootstrapUnwindMetadata | A real compiler-produced function/unwind table is accepted |
| BootstrapUnwindRejection | Invalid ordering/ranges/slots, handler/chained flags, unsupported codes and relocation into metadata are rejected without allocation |
| BootstrapNativeEntry | C entry, syscall bridge and normal return work at two load bases |
| BootstrapImageDescriptor | Actual image base/ranges and metadata match the loaded PE; descriptor is readonly/NX |
| BootstrapOrderAndRunOnce | Ordered initialization/reverse cleanup, reentrancy/repeat rejection and one successful startup across two threads |
| BootstrapRollback | Second initializer fails; first initializer's resources are released; main never runs |
| BootstrapMainFailure | A nonzero main result is preserved through cleanup |
| BootstrapValidation | Bad later callback, writable table, bad main, excessive count and invalid image description are rejected before any initializer |
| BootstrapEmptyList | Main/exit works without initializers |
| BootstrapDescriptorProtection | Writing image information faults in ring 3; a fresh component runs afterward |
| BootstrapInitializerFault | A fault while constructor-owned memory exists terminates only the component; teardown restores page counts |

The original image, memory, thread, event and isolation tests remain. The source audit and both hosted NativeAOT probes also pass; those hosted results remain separate from this guest C evidence.

## Remaining runtime work

The subsequent [0.0.10 backend decision and memory slice](NativeAot-Gc-Memory-Port.md) select Windows x64 code generation with a WitOS source adapter; the full runtime is still unported. The kernel has not registered ReadyToRun modules, initialized GC tables or run managed constructors. Compiler TLS, imports, runtime exception delivery and GC rendezvous remain missing.

Continue the selected source port with the full runtime build and explicit adapters to the tested image/thread/wait contracts. Preserve the upstream order: real runtime/GC initialization must precede managed-module initialization. Do not implement successful Rh* placeholders or call the native initializer test a managed runtime bootstrap.
