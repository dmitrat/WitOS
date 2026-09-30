# P1.6 scoped MTA apartment lifecycle

The initial WitOS profile supports the apartment lifecycle required by the unchanged upstream Windows CoreLib: CoInitializeEx for MTA, CoGetApartmentType and CoUninitialize. It does not provide COM object activation, RPC/marshalling, WinRT, neutral apartments or STA message dispatch. Initial STA requests explicitly return E_NOTIMPL; requests to change an already explicit MTA to STA return RPC_E_CHANGED_MODE. This is the scoped apartment-state option recorded in P1-Startup-Platform-Boundary, not a claim of full Windows COM support.

## Real state and lifetime

A component-private registry binds each participant to the generation-bearing thread identity obtained from the kernel. Writable TLS identifiers cannot select an entry. Initialization requires the published native image and active kernel-confirmed compiler TLS, because orderly cleanup must be registered before MTA participation is published. Missing prerequisites or an occupied cleanup slot fail with E_UNEXPECTED and publish nothing.

First initialization establishes explicit MTA participation and returns S_OK. Repeated compatible initialization increments a checked counter and returns S_FALSE; each CoUninitialize decrements one reference. Uninitializing an uninitialized thread is a no-op. When the current thread has no explicit reference but another participant does, query reports MTA/IMPLICIT_MTA. When no participant remains, query returns CO_E_NOTINITIALIZED with CURRENT/NONE outputs. Null output pointers and invalid initialization arguments return E_INVALIDARG. HRESULT operations preserve native last-error and errno.

Registry access is serialized. The existing yield-based gate is released before callbacks or waits. There are eight bounded participant records, above the current live-thread quota; a record remains reserved through balanced uninitialization/reinitialization until its owner exits. Counter overflow fails without mutation. Query copies a scalar state snapshot after releasing the gate. Output buffers belong to the native caller, as for the other user-space platform routines.

The registry and apartment semantics stay in the hardware-independent native system/runtime layer. No COM state, Windows FLS emulation or object activation service is added to the kernel.

## Cleanup ordering

The native lifecycle retains its one private runtime exit notification. A separate single private platform-cleanup slot now runs after that notification and before the exit syscall. It has the same kernel-owner, initialized-TLS and executable-callback checks. Duplicate registration is rejected. Both callbacks are popped before invocation; reentrancy and late registration remain rejected, while repeated completed notification is idempotent. No gate is held across callback execution.

Order is native TLS destructors, process atexit callbacks where applicable, runtime exit notification, apartment cleanup, kernel exit. Thus the runtime notification can still query/uninitialize its apartment. Cleanup then drops remaining participation and releases the record before thread-slot reuse. Native workers must use the existing orderly native exit path. Raw exits/faults remain abrupt and bypass native cleanup; recovery from an abruptly abandoned participant inside a surviving component is not claimed. Process termination discards the component's private state.

This does not implement PalAttachThread, PalInitComAndFlsSlot, RuntimeThreadShutdown or GC detach. Those full-runtime dependencies remain unresolved for P1.8/P3. The native notification observer used in tests is an ordering probe, not a substitute implementation of RuntimeThreadShutdown. Pinned ThreadStore::DetachCurrentThread invokes the real managed exit callback before taking its lock and cleaning the GC allocation context; that ordering remains a requirement for the later integration.

## Evidence

The native Windows reference verifies initial outputs, null-pointer errors, S_OK/S_FALSE balancing, changed mode and explicit/implicit MTA on a second real OS thread. It is built and run during runtime-source; its ole32/kernel32 dependencies are host-only. No Windows implementation library is used in the guest.

The separate RuntimeComFixture links exact source-archive COM objects and readonly direct/import bindings. Guest modes 78-81 cover both load bases, prerequisites before image/TLS publication, raw-no-TLS failure, MTA reinitialization and underflow, local checked-counter exhaustion, kernel identity despite raw TLS spoofing, synchronized implicit MTA while another thread is alive, repeated worker-slot reuse and cleanup of unbalanced references after the native runtime notification. Process shutdown checks apartment availability in atexit and notification, followed by no remaining MTA participation. A competing platform cleanup registration causes initialization failure without leaked state. All tests preserve error/errno and kernel resource accounting.

Validation passed: Release build, all 20 ordinary boot scenarios, runtime-audit/probe/target/source/readiness and four QEMU runtime profiles with 242 user groups and 54 expected contained faults each. Full archive: 120 members; configuration archive: 36 exact objects. Minimal/broad strict link: 22/28 unresolved dependencies. The separate COM image is 27136 bytes with 88 plain unwind records, inside the existing loader limits. Guest managed execution and full runtime attachment are still unproven.

P1.6's selected native paths and optional-platform policy are complete. P1.7 GC OS policy is next; P1.8 still owns real COM/FLS startup adaptation and runtime thread attachment/detachment.

Logs: artifacts/p1-com-config.log, artifacts/p1-com-test.log, artifacts/p1-com-audit.log, artifacts/p1-com-probe.log and artifacts/runtime-com/reference.log. Exact source/object provenance is retained in runtime-source/runtime-config reports.

References: [CoInitializeEx](https://learn.microsoft.com/en-us/windows/win32/api/combaseapi/nf-combaseapi-coinitializeex), [CoGetApartmentType interpretation](https://devblogs.microsoft.com/oldnewthing/20180208-00/?p=97986), and pinned Thread.NativeAot.Windows.cs, PalMinWin.cpp, threadstore.cpp at b82454cad0aaaae3db2cf18fbf2cccc36e201ccc.
