# P1.8.f: source-built PAL attachment bindings

Follow-up: [P1-Hijack-and-Runtime-Driver](P1-Hijack-and-Runtime-Driver.md) resolves PalHijack and links a real handoff image. Runtime/GC execution acceptance remains open. This document retains the earlier attachment-binding checkpoint.

PalInitComAndFlsSlot and PalAttachThread now have real WitOS implementations in pal_attach.witos.cpp. The full source-built archive retains RuntimeThreadShutdown and the complete ThreadStore/GC detach dependencies. Minimal/broad strict-link inventories are now 1/7 unresolved; PalHijack is the sole minimal dependency.

This is source/link evidence plus regression coverage. The existing native runtime-config fixtures do not execute these new PAL entrypoints with a live collector. P1.8.f remains open until actual runtime attachment, list membership and GC allocation-context cleanup pass in the guest. No shutdown/collector stub was added to manufacture a smaller executable probe.

## Binding contract

Initialization is a serialized, component-lifetime attempt on the upstream finalizer thread. Kernel thread discovery confirms compiler TLS before raw runtime TLS or errno access. The adapter requires the real ThreadStore TLS metadata (index zero and the recorded SECTIONREL offset), validates the complete pinned RuntimeThreadLocals within the kernel-reported TLS page and checks that the notification code belongs to the published native image.

The first caller claims initialization, enters MTA through the real native COM binding and publishes readiness only after success. No registry gate is held across COM entry. Concurrent/repeated attempts return a checked error without replacing readiness; COM failure becomes a terminal failed attempt. Successful initialization preserves native last-error and errno. The upstream FinalizerStart handshake remains unchanged: signal initialization completion, stop on failure, attach only after success.

PalAttachThread checks readiness and requires its argument to be exactly the current raw upstream Thread record at the validated TLS offset. It registers a private exit notification; null, wrong record, duplicate/late registration or any other void failure terminates the component. The adapter does not construct another Thread, publish list membership, or substitute a native thread ID for a runtime record.

The notification mechanism retains the kernel-derived generation-bearing owner and pops the slot before calling the adapter wrapper. The wrapper rechecks the current record and invokes the actual RuntimeThreadShutdown. Its full managed-exit callback, ThreadStore lock/removal, Thread::Detach -> GC FixAllocContext and Thread::Destroy path remain linked. The existing upstream process-shutdown fallback is preserved; apartment cleanup follows runtime notification through the separate native platform-cleanup slot.

## Evidence

- Release solution build passes with zero warnings/errors.
- Source audit/target/source/readiness and Windows references pass. Archive verification requires the protected adapter object to be byte-identical and retain both wit_native_thread_on_exit and actual RuntimeThreadShutdown dependencies.
- Native archive: 140 members; config probe: 51 objects. Minimal/broad unresolved: 1/7.
- All 20 kernel integration scenarios and all four native runtime profiles pass (284 User groups / 66 expected contained faults each). These are existing regression suites, not guest execution evidence for the new attachment binding.
- Hosted runtime-probe passes; hosted managed execution remains separate from guest execution.

Logs: artifacts/p1-attachment-build.log, artifacts/p1-attachment-source.log, artifacts/p1-attachment-config.log, artifacts/p1-attachment-test.log and artifacts/p1-attachment-probe.log. Strict unresolved names and source/archive hashes remain in runtime-readiness/runtime-source reports.

## Integration dependency and next implementation

A complete image is required to test attach/detach with the actual collector. The remaining PalHijack must use real thread capabilities and retain the upstream safe-state checks. This is joint P1.8.f/g and P3 work; neither checkbox can close just because its symbol resolves.

The current pinned-source adaptation still deliberately removes DuplicateHandle from Thread::Construct, leaving INVALID_HANDLE_VALUE. Native thread-reference APIs are now implemented, so that adaptation must be replaced with the real capability path. The kernel DuplicateHandle transport validates the destination before publication and leaves it unchanged on failure; retain the explicit INVALID_HANDLE_VALUE fallback. Keep full Thread::Destroy cleanup and update the earlier construct-only probe to validate/close its owned capability explicitly without pretending it performs GC detach.

The pinned build enables FEATURE_SUSPEND_REDIRECTION and FEATURE_SPECIAL_USER_MODE_APC. Ordinary alertable callbacks do not implement special asynchronous APC delivery. Use the real suspend/get-context/HijackCallback/resume fallback with exception/service-active rejection and balanced suspension. Preserve suspend redirection; do not disable it merely to satisfy a link check.

A walk lease cannot enclose all of HijackCallback: its Redirect path legitimately changes the target context, while the kernel refuses context mutation under a live lease. The enclosing lifetime scope belongs around the actual Thread::HijackReturnAddress stack walk and all uses of its returned original-stack pointers, after redirection has been considered. Preserve the existing no-mutation-under-lease invariant. Broader GC root walks also need real lifetime integration before GC acceptance.

After PalHijack links, the existing wmain diagnostic root still is not a WitOS handoff thunk. Build the actual startup driver and measure/load the real image; then verify main/worker attachment, ordinary/detached exits, failures, reuse, allocation-context cleanup and process-shutdown policy. Keep P1.8.f, P1.8.g and the associated P3 criteria open until that execution evidence exists.
