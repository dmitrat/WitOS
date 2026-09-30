# P1.8.f: real runtime attachment and shutdown boundary

Follow-up: [P1-Pal-Attachment-Bindings](P1-Pal-Attachment-Bindings.md) implements the PAL source bindings and records a 1/7 minimal/broad link boundary. Actual runtime/GC attach/detach acceptance remains open. This note preserves the pinned call-site investigation and required integration criteria.

## Actual upstream order

- Runtime/FinalizerHelpers.cpp: FinalizerStart calls PalInitComAndFlsSlot on the newly created finalizer thread. It signals g_FinalizerDoneEvent, exits on failure, and calls ThreadStore::AttachCurrentThread only after success. Preserve this handshake; initializing an unrelated main-thread apartment is not equivalent.
- Runtime/windows/PalMinWin.cpp: PalInitComAndFlsSlot enters MTA before creating the FLS callback slot. PalAttachThread accepts void* and registers the actual Thread pointer; duplicate registration is fatal. WitOS needs the lifecycle contract, not a general Windows FLS facility.
- Runtime/threadstore.cpp: AttachCurrentThread obtains the real raw compiler-TLS Thread record, refuses detached/reused state, returns early only for a genuinely initialized attached thread, calls PalAttachThread, constructs the record, takes the actual ThreadStore lock and publishes list membership.
- Runtime/startup.cpp: RuntimeThreadShutdown(void*) verifies current-thread correspondence in upstream assertions and calls ThreadStore::DetachCurrentThread on ordinary thread shutdown. It keeps the process-shutdown fallback instead of detaching during unsafe teardown.
- Runtime/threadstore.cpp and thread.cpp: detach may run the managed exit callback, enters preemptive mode before the ThreadStore lock, removes the real thread record, calls Thread::Detach -> GCHeapUtilities::GetGCHeap()->FixAllocContext, then destroys the record. These GC dependencies must remain real.

The WitOS startup overlay currently preserves RtlDllShutdownInProgressFallback. It compares g_threadPerformingShutdown with the actual current runtime thread. Module lookup intentionally cannot bind ntdll; this checked upstream fallback remains necessary. Do not replace it with unconditional true/false or force GC detach during the process's shutdown callback.

## Available WitOS mechanisms

src/Runtime.NativeAot/tls.witos.cpp already provides one private runtime notification per thread. Registration checks published image, kernel-confirmed compiler TLS, lifecycle phase, callback code range and duplicate/reentrant use. The kernel-provided generation-bearing ThreadId remains the owner. Notification pops its callback/context before invocation, holds no shared gate across the callback and runs after C++ TLS cleanup (after atexit for process exit).

Native MTA participation in native_com.witos.cpp has separate platform cleanup. It runs after the runtime notification and preserves apartment lifetime through detach. There is no need to add a speculative public FLS API, emulate a Windows TEB or encode thread identity into a writable TLS slot.

## Implementation and acceptance sequence

1. Implement serialized component initialization on the finalizer thread, with checked native TLS/MTA prerequisites and failure publication consistent with the upstream handshake. Determine explicit repeat/failure behavior from the one-time upstream caller; never reset a live registry.
2. Register the actual upstream shutdown callback and Thread pointer through the private notification slot. Validate kernel owner/compiler-TLS state before TLS access, reject null/invalid or duplicate attachment, and fail fast on void registration failure. Do not construct or publish a second Thread record in the PAL adapter.
3. Keep RuntimeThreadShutdown/ThreadStore/GC dependencies intact in the full archive. A source-linked registration helper or injected test callback proves only registration mechanics, not runtime attachment.
4. Execute actual attach/list membership and detach with initialized runtime/collector: main thread, workers, ordinary and detached exits, failures and slot reuse. Confirm allocation-context cleanup and exactly-once lifetime. Verify process-shutdown fallback separately from ordinary thread exit.
5. Update link policy/inventory and source/guest evidence, then close P1.8.f only when those integration criteria pass. P1.9/P1.10 image construction and P3 runtime/GC work may be required to provide this executable test environment; do not substitute GC stubs to break that dependency.

Pinned local sources: .tools/upstream/runtime-10.0.8/src/coreclr/nativeaot/Runtime/{FinalizerHelpers.cpp,windows/PalMinWin.cpp,startup.cpp,threadstore.cpp,thread.cpp,Pal.h}. Effective overlays are prepared under artifacts/runtime-config/source and must remain hash-tracked. The canonical runtime revision and package/source pins remain unchanged.
