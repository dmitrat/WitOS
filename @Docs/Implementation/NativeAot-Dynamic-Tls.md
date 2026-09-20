# ADR 0015: User-space C++ dynamic TLS lifecycle

**Status:** Implemented in WitOS 0.0.18; user ABI remains v9.
**Date:** 2026-09-20.
**Scope:** Real MSVC C++ thread_local initialization and destructor registration in the guest, with normal/explicit native thread exit. This is not NativeAOT ThreadStore attachment, managed ThreadStatic, finalization or guest .NET execution.

## Contract and decision

The pinned NativeAOT `Runtime/thread.cpp` contains a dynamically initialized thread_local PerThreadRandom. Static template copying cannot construct it. The selected MSVC toolchain emits .CRT$XD* initializer records and uses __dyn_tls_init, __dyn_tls_on_demand_init, __tls_guard and __tlregdtor. The local compiler's tlsdyn.cpp/tlsdtor.cpp were inspected for these signatures and ordering; their implementations are not copied or linked into the guest.

Implement the required behavior in a separate WitOS user-space adapter, `tls.witos.cpp`. A C startup wrapper initializes the lifecycle before calling a C++ entry point. User-space thread wrappers initialize new threads and drain their TLS destructor registry before issuing the kernel thread-exit syscall. Kernel thread creation continues to copy only static template bytes; it never invokes C++ or managed callbacks.

The PE callback list remains empty. The loader still rejects arbitrary nonempty callback arrays. An explicit user entry performs the required initialization instead of pretending that general Windows DLL/FLS lifecycle is available.

## Initialization

`wit_native_tls_initialize` runs once before publishing workers. It accepts the kernel-provided readonly startup/image descriptor, validates the linked initializer-table extent, readonly/non-executable storage and every nonnull executable callback, and only then initializes the main thread.

The bounded table permits 32 initializer entries. Each thread has zero-initialized lifecycle state in its compiler TLS. Enter sets the compiler guard before invoking constructors, allowing constructor-time guarded cross-translation-unit access without repeated initialization. Repeated enter is harmless while initializing/ready; enter during/after teardown fails fast. Generated on-demand access invokes the same implementation, so a raw native thread can initialize on its first compiler-guarded access after process setup.

Both eager and genuine first-access initialization are tested. Constructors are actual compiler-generated calls for two C++ thread_local objects; they allocate from the native heap. No manually counted substitute objects are used.

## Destruction and thread wrapper

Each thread holds up to 32 destructor callbacks. __tlregdtor validates executable callbacks. MSVC callers ignore its failure result, so invalid registration or exhaustion fails the component instead of silently losing cleanup.

Normal thread-return and `wit_native_thread_exit` drain callbacks in reverse registration order, popping each before invocation. A callback may register more cleanup, but at most 64 callbacks may be executed during one drain; repeated self-registration therefore fails instead of looping indefinitely. Successful leave is idempotent. Recursive leave fails fast. TLS storage remains alive until the user-space drain completes and the kernel subsequently joins/reaps the thread.

The native thread wrapper uses a four-slot process-private start table. A yielding gate protects publication/copy-out and kernel-create rollback; it is never held while parking. The kernel create call cannot park, so the slot remains protected until success/failure is published. Child code copies the entry/argument before freeing its start slot. Entry pointers must lie in initialized executable image data. Repeated thread-capacity failures must not leak these slots.

Callers synchronize object lifetimes and join workers before orderly component completion. Explicit thread exit runs TLS cleanup but does not unwind arbitrary automatic C++ stack objects. Raw kernel exit, component abort, user faults and forced termination bypass orderly cleanup; kernel ownership teardown still reclaims mappings/handles. C++ exception unwinding is not implemented. Accessing destroyed C++ TLS objects after leave remains invalid, even where a compiler guard no longer calls the helper.

## Loader adjustment

The generated _tls_index is zero-initialized and may live in BSS. The static TLS validator now requires an aligned writable mapped four-byte range, rather than requiring initialized file bytes. Template and directory validation remain strict. This is the actual cause found when the first dynamic fixture was rejected; unwind metadata was ordinary and already supported.

`tls_metadata.c` supplies one-module static metadata for the controlled user executable. Its PE callback pointer is null. The metadata object is not injected into the broad runtime diagnostic link; runtime startup/module registration is still incomplete.

## Guest evidence

Five new required groups cover:

- `DynamicTlsLifecycle`: main-thread initialization, multiple worker construction/destruction, guarded cross-TU access, repeated enter, native allocations, thread-slot exhaustion/recovery, slot reuse, lazy first access and idempotent main-thread cleanup. Runs at both guest image bases.
- `DynamicTlsExplicitExit`: explicit native thread exit drains real C++ TLS destructors before the kernel exit syscall.
- `DynamicTlsDestructorOrder`: reverse order, including additional cleanup registered by a destructor.
- `DynamicTlsFailFast`: destructor-registry exhaustion, invalid destructor pointer, constructor fail-fast, attempted re-entry after teardown, recursive teardown, endless cleanup registration, and a corrupted initializer pointer rejected before any constructor executes. A fresh component recovers.
- `DynamicTlsFaultIsolation`: real page fault in a destructor, checked vector/error/address/selectors, complete component reclamation and subsequent recovery.

Normal components must show six thread creations including the main thread, five joins/reaps, restored owned-frame counts and no dynamic reservations. Every outcome must return all physical pages after component teardown. Successful boots now require 130 user groups and 41 contained user faults.

The local fixture is 8,704 bytes with 25 plain unwind records and no Windows/CRT imports. `dynamic-tls-symbols.log` verifies actual compiler initializer/destructor registration, and `dynamic-tls-build.json` records compiler/input/image evidence. The linker map supplies the initializer location for fault injection. Kernel CI uploads these artifacts.

Release build, runtime-port, the 35-file source audit and hosted reference probe passed. Full upstream source build (including runtime-target) passed with all four Windows-reference execution groups. The complete VM suite passed all 18 scenarios, with 130 required user groups and 41 contained user faults in successful boots, including both RAM profiles and the expected timeout.

## Source integration and remaining boundary

The actual Runtime.WorkstationGC archive contains the TLS adapter object, verified byte-for-byte; it now has 71 members. The upstream source tree remains clean and unchanged. The helper symbols __dyn_tls_init, __dyn_tls_on_demand_init and __tls_guard resolve in the strict source link. That link also exposes the already-existing wit_native_claim_startup glue, deliberately not supplied by the broad diagnostic link.

The remaining count is 134: seven GC environment requirements, five deliberately excluded native transport/startup helpers and 122 other platform/runtime requirements. This is dependency evidence, not a compatibility percentage. _tls_index still requires the future complete runtime metadata/bootstrap object.

Next connect real runtime thread registration/cleanup through the WitOS PAL and the actual ThreadStore. The pinned Windows RuntimeThreadShutdown also checks process-shutdown state; merely adding an FLS-shaped token would not implement that contract. Required PAL services, runtime module bootstrap, GC coordination and exception delivery remain unported.
