# ADR 0020: Native PAL module lookup from the immutable image handoff

**Status:** Implemented in WitOS 0.0.23; ABI v13 unchanged.
**Scope:** Single native PE image identity and bounds. No managed module registration or guest .NET execution.

## Decision and contract

Implement the pinned NativeAOT `PalGetModuleHandleFromPointer` and `PalGetModuleBounds` signatures using the kernel's readonly `WitUserImageInfo`. Extract the existing descriptor/range validation from `Runtime.Native/bootstrap.c` into `image.c`, shared with dynamic TLS. User-space startup publishes the single-image context once, after validating the entire descriptor and before invoking C++ TLS constructors or creating workers. The context itself requires no compiler TLS, allocation, Windows loader structures or PE-header reparsing. There is no unloading/replacement during the component lifetime.

Lookup returns the actual relocated image base for an address in the declared headers or a declared section, including its zero-fill portion. It does not dereference the query address. It rejects null, overflow-like addresses, header/section padding, image-end addresses, startup metadata, stacks, raw/compiler TLS and reserved/committed/released dynamic memory. The module value is an image identity, not a kernel handle to pass to CloseHandle. Successful lookup preserves native last-error; failure before initialization reports ERROR_NOT_READY, and an address outside the accepted image extents reports ERROR_INVALID_ADDRESS.

Bounds require that exact image identity and two distinct non-null output pointers. They return `[Base, Base + ImageSize - 1]`, with an **inclusive upper endpoint**, matching the pinned [PalCommon implementation](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/nativeaot/Runtime/windows/PalCommon.cpp). This span can contain unmapped holes: it is not a promise that every byte is readable. The upstream method returns void; invalid outputs, unavailable context or a foreign/interior module value record the corresponding native error and fail the component before writing either output. Valid writable output storage remains the native caller's responsibility. Success preserves last-error.

The original `WitNativeModule` C initializer helper retains its lifecycle and delegates address checks to the same image helper. It does not automatically become a managed registry. TLS uses the same complete descriptor validator and initialized RX/readonly range checks for its callback table.

## Guest evidence

The separately linked PalModuleFixture uses actual pinned Pal.h declarations and has no OS/CRT imports. Two new required groups cover:

- PalModuleDiscovery: headers, code, writable data, section endpoints and a real zero-fill extent; inclusive bounds; rejected padding/outside addresses and dynamic memory in all three states; pre-initialization failure and last-error preservation. It runs at two relocated bases, from actual C++ TLS constructors/destructors and in three sequential worker slots, then after main-thread TLS cleanup.
- PalModuleInvalidBounds: null and interior module values, null lower/upper outputs, aliased outputs and a pre-initialization call. The supervisor checks fail-fast exit plus unchanged output sentinels, then runs a healthy component again.

The supervisor also requires three joins/reaps, no outstanding handles/events/reservations, unchanged owned memory and full physical-page reclamation. Existing bootstrap validation and dynamic TLS tests exercise the shared validator after extraction. A test-development failure exposed an incorrect expectation of Close succeeding after ThreadJoin; the test now checks BAD_HANDLE because join already consumes/reaps the handle. The test's finalization helper also preserves an earlier failure code for diagnosis.

Release build, runtime-port, the 41-file source audit and hosted NativeAOT probe passed. The full upstream native source build and its runtime-target prerequisite passed: 78 Workstation archive members (15 local adapter/helper sources), eleven minipal members and four Windows reference execution groups. Both new adapter objects are verified byte-for-byte in the archive. The strict WitOS link has 108 unresolved symbols: seven GC environment, 20 PAL, five deliberately excluded native transport/startup and 76 other platform/runtime requirements. The complete sequential regression passed all 18 VM scenarios. Successful boots require 152 user groups and 50 contained user hardware faults. No timeouts, resource limits or assertions were relaxed.

## Startup dependency and remaining work

The pinned [RhInitialize](https://github.com/dotnet/runtime/blob/b82454cad0aaaae3db2cf18fbf2cccc36e201ccc/src/coreclr/nativeaot/Runtime/startup.cpp) obtains its module through this PAL interface, but first calls PalInit. The pinned Windows PalInit initializes GCConfig and the GC OS environment. GCConfig reads real runtime configuration through GCToEE/RhConfig, including environment and compiler-embedded settings. Those dependencies need a concrete supported contract before PalInit can be implemented honestly.

PalInit, PalAttachThread and PalGetModuleFileName stay unresolved. The current handoff carries no filesystem path, so the adapter does not manufacture one. Actual RuntimeInstance/GC initialization, OS module registration, managed TypeManager/GC-table initialization, thread attachment, context/unwind integration and GC rendezvous remain pending. Native image discovery closes two PAL dependencies; it does not execute .NET or its collector in the guest.
