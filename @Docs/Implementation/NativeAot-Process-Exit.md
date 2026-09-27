# Native process exit callbacks

**Status:** Implemented in WitOS 0.0.27; ABI v13 unchanged.
**Update (0.0.35):** Process shutdown now invokes the private current-thread exit notification after the atexit queue, before publishing completion. Individual native thread exit runs TLS cleanup followed by its notification. See [the current contract and tests](NativeAot-Thread-Exit-Notification.md); historical build counts below describe 0.0.27.

**Decision:** A bounded user-space CRT exit registry, used by explicit native component shutdown. Upstream CoreLib and managed semantics remain unchanged.

## Context and compatibility direction

The Developer Experience Manifesto defines the final application contract: unchanged portable managed assemblies under upstream CoreCLR/JIT, standard SDK/TFMs and optional WitOS APIs. NativeAOT is the current system bring-up technology. Neither this adapter nor the earlier native probes constitute that application compatibility milestone.

The pinned NativeAOT RhInitialize registers OnProcessExit using atexit before InitDLL. This callback eventually interacts with ThreadStore and tracing. The platform must provide real registration and cleanup rather than satisfy the linker with a no-op.

## Decision and alternatives

Use a component-private registry with 32 pending callbacks, a serialized lifecycle, kernel-provided generation-bearing owner identity and an explicit shutdown entry point. A gate protects registration and removal; it is released before TLS destruction, callback invocation or waiting. Contenders yield outside the gate.

Linking the Windows CRT would introduce foreign OS dependencies. A successful atexit stub would lose runtime cleanup. An unbounded heap registry is a future option; the initial bounded implementation makes exhaustion testable without introducing allocation during shutdown. The limit is a prototype constraint, not the eventual .NET compatibility contract.

## Implemented contract

- The real C atexit signature returns zero on registration and nonzero on failure. It requires a published immutable image and a callback in initialized executable, nonwritable image bytes. Null, data and unavailable-image callbacks are rejected before mutation.
- Registration works before compiler TLS initialization. Multiple native workers may register before shutdown; their individual exits do not drain the process queue.
- wit_native_process_shutdown claims a kernel-confirmed thread identity and requires compiler TLS. It destroys that thread's C++ TLS objects, then drains process callbacks in reverse registration order. This matches the TLS/atexit ordering documented for [Microsoft CRT exit](https://learn.microsoft.com/en-us/cpp/c-runtime-library/reference/exit-exit-exit?view=msvc-170); [atexit](https://learn.microsoft.com/en-us/cpp/c-runtime-library/reference/atexit?view=msvc-170) defines the return and LIFO registration contract.
- The draining owner may register more work during TLS cleanup or a callback. A callback is removed before invocation, so newly registered work executes before older pending work. At most 64 callbacks may run during one drain; endless re-registration fails fast.
- Other threads cannot register once shutdown starts. Concurrent or recursive shutdown fails fast. Completed shutdown is idempotent only for the same owner; registrations after completion fail.
- Native last-error is preserved by registry operations. No writable FS/GS value is used as thread authority.
- wit_native_process_exit performs shutdown and then the real process-exit syscall with the caller's code. Raw process exit and faults bypass cleanup; callbacks that fault terminate only their component.

Callers must quiesce workers before ordinary process cleanup. The registry does not suspend/kill workers or implement a general process shutdown coordinator. Adversarial tests deliberately leave one worker active to verify rejection and gate release. Existing native_start still provides the raw return-to-exit transport: components opt into the new lifecycle explicitly. Returning from arbitrary legacy fixtures does not acquire new cleanup behavior.

## Upstream integration

The Windows reference retains the unchanged upstream startup.cpp. The WitOS overlay compiles a generated copy of the complete hash-verified file, with one recorded correction: RhInitialize returns false when atexit registration fails. All InitDLL, GC, ThreadStore and diagnostic dependencies remain real and unresolved where not implemented. The upstream checkout stays clean; input/generated-source hashes and exact archive object bytes are checked.

The exit adapter is included in Runtime.WorkstationGC.lib. The strict link now resolves atexit and still fails on missing platform services. The local archive has 83 members; the boundary contains 98 unresolved symbols: seven GC environment, 17 PAL, five deliberately omitted native transport symbols and 69 other runtime/platform requirements. These counts are evidence for this workload, not a completion percentage.

The generated startup source is prepared alongside the configuration sources, but is not included in the separate five-object guest configuration probe. The complete RhInitialize and its OnProcessExit callback have not executed in WitOS. ThreadStore detach, GC allocation-context cleanup, tracing shutdown and managed finalization still require their real runtime implementation and lifecycle integration.

This is not a full CRT: standard exit/_onexit, stdio flushing, DLL unloading and C++ static initialization/destruction are not claimed. It introduces no custom managed CoreLib.

## Validation

Six required guest groups cover:

1. Early registration, TLS-before-callback ordering, LIFO, registration during cleanup, relocated images, explicit process exit and completed-call idempotence.
2. Capacity rejection without losing or duplicating the 32 accepted callbacks.
3. Concurrent registration from three joined workers; no process callbacks on thread exit; foreign registration rejection while the owner callback yields.
4. Recursive/concurrent shutdown and bounded callback re-registration, followed by a clean component restart.
5. Contained callback page fault and successful fresh restart.
6. Raw exit bypassing both TLS and process callbacks.

The fixture imports no OS/CRT implementation and contains no managed header. It is 8,192 bytes with 27 plain unwind entries on the local compiler. Every case checks physical-page reclamation; successful ordinary paths also check owned pages, joins/reaps and empty handle/event tables.

The suite now requires 162 ordinary user groups and 51 contained user hardware faults; runtime-config adds nine groups, giving 171 per dedicated boot. Release build, source audit, hosted probe, runtime-target/source and the two runtime-config boots passed locally. All eighteen kernel scenarios passed; the final 256 MiB boot also passed after strengthening early-registration and raw-exit assertions.

## Next boundary

Continue actual RhInitialize/InitDLL: explicitly choose the initial upstream diagnostic profile, handle optional Windows module/export discovery, then integrate RuntimeInstance, collector startup, ThreadStore and exception/GC coordination. Preserve the standard binary-portability goal; do not replace these dependencies with a custom managed runtime.
