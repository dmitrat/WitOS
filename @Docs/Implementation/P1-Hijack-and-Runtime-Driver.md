# P1.8.g / P1.9 / P1.10: real hijack binding and linked guest driver

Follow-up: [P1-First-Managed-Boot](P1-First-Managed-Boot.md) records actual managed Main/GC execution, extended admission and full teardown. The execution limitations below describe this earlier link-only checkpoint. Separate worker/rendezvous acceptance remains open.

The minimal standard-CoreLib executable now strictly links with zero unresolved symbols and no OS imports. A separate image with the real WitOS handoff entry is also linked and inspected. Neither full image has executed in the guest yet. P1.8.f/g/h and startup execution remain open until the actual runtime and GC pass together.

## Real context capabilities and hijack

The pinned Thread::Construct body again calls the genuine upstream DuplicateHandle operation. WitOS supplies a generation-bearing thread-reference capability, not a thread ID or pseudo handle. Atomic output-on-success preserves upstream INVALID_HANDLE_VALUE on failure. Thread::Destroy keeps its real CloseHandle path.

The source-built PalHijack uses the actual suspend/context/HijackCallback/resume fallback. It validates current compiler-TLS availability before errno access, requests the complete supported x87/SSE context, rejects service/exception-active snapshots and balances every successful SuspendThread with one ResumeThread. Failed resume is fatal rather than leaving an owned suspension hidden. Normal returns preserve caller last-error and errno. There is no fake special APC delivery; the upstream Thread callback and suspend-redirection feature remain intact.

Both Thread::HijackReturnAddress overloads acquire a native walk scope across StackFrameIterator construction and all return-address pointer uses/writes. This scope does not enclose the preceding Redirect attempt, so the existing kernel prohibition on context mutation under a live lease remains unchanged. These are explicit hash-tracked adaptations of pinned source; Windows reference sources remain unchanged.

Guest construct-only probes now verify actual handle identity, rights, stability and release through four rounds of worker-slot reuse. An additional worker fills the handle table, calls real Construct and checks the INVALID_HANDLE_VALUE fallback; freeing capacity and calling SetGCSpecial again must not silently reconstruct the record. The construct-only fixture explicitly closes its owned handles and does not claim to run ThreadStore/GC detach.

The measured simultaneous requirement is nine handles: console/self, main context capability, three joins and three worker context capabilities. The old quota was eight; it is now sixteen. Exhaustion tests derive their bound from the shared quota and continue to verify failure and recovery. The configuration fixture now has 129 unwind records and explicitly uses the existing runtime PE profile; the ordinary PE limit remains 128.

## Strict image and real handoff

The diagnostic wmain image links with actual transport/TLS inputs and normal executable/bootstrap/CoreLib roots. The broad audit deliberately omits transport and retains exactly six symbols: _tls_index, wit_native_call, wit_native_claim_startup, wit_native_fail_fast, wit_native_try_lock and wit_native_unlock. They are resolved in both complete images; they are not six remaining OS implementations.

boot_driver.witos.cpp is compiled without GS only for the pre-cookie entry. It publishes the validated immutable image and readonly environment, seeds the real security cookie, validates complete C/C++ initializer tables, enters native TLS, executes initializers and calls upstream wmain with a valid one-element argv. After return it records the result and invokes the existing orderly process-exit path. Runtime objects retain GS. Managed module/TypeManager/GC initialization remains entirely in upstream bootstrap, not in the kernel or driver.

The linked map proves wit_native_start is the PE entry and retains both wmain and __managed__Main. Static audit checks complete readonly initializer tables, null sentinels and initialized RX callback targets. The current image has zero C and seven C++ initializer callbacks. PE/import/TLS metadata and all source/library/object hashes are recorded separately from the Windows reference.

| Artifact | Mapped bytes | Unwind records | TLS template |
| --- | ---: | ---: | ---: |
| Windows reference | 954,368 | 2,593 | reported separately |
| WitOS wmain diagnostic | 991,232 | 2,781 | 648 bytes |
| WitOS handoff driver | 995,328 | 2,800 | 640 bytes |

The handoff driver has 436 DIR64 relocations, zero TLS callbacks and no OS imports. Its explicit bring-up environment sets DOTNET_GCHeapHardLimit=400000 (hexadecimal 4 MiB). This is an upstream GC configuration setting, not a replacement collector.

## Evidence

Release build, source audit/probe/target/source/readiness and Windows reference gates pass. All twenty boot scenarios pass with the sixteen-handle quota. Four native runtime profiles pass with 284 User groups / 66 expected contained faults each, including the updated actual Construct capability and exhaustion cases. These regressions do not execute the full new PalHijack/attachment/driver with a live collector.

Native archive: 141 members; config probe: 51 objects. Minimal/broad link: 0/6 as explained above. Logs: artifacts/p1-hijack-build-final.log, p1-hijack-config-final.log, p1-hijack-test-final.log, p1-hijack-probe.log and p1-driver-source-final.log. Images, map, initializer audit and hashes: artifacts/runtime-readiness/readiness.json and guest-driver/{WitOS.NativeAotBoot.pe,WitOS.NativeAotBoot.map,image.json}.

## Required next execution slice

Load and execute the real handoff image to close the outstanding runtime acceptance, using the P2/P3 integration dependencies already listed in PLAN.md. Current admission limits are 256 KiB per image, 320 runtime unwind records and 128 total owned pages. The real image alone spans 243 image-sized pages before fixed stacks, TLS, private page tables, native heap or GC backing; it cannot fit those limits.

Increase budgets from the measured image/workload and retain ordinary-profile quotas where useful. Preserve reserve/commit rollback, no-access ownership, teardown, readonly metadata and unmapped image gaps. The alternate image address also requires checking the user VA window. WitPeImage contains an inline metadata-range array and is currently local in the loader; review kernel stack/workspace costs before raising its range capacity. Kernel stacks are currently 64 KiB. Avoid unbounded allocator work and keep all quota failures tested.

Then execute native publication/initializers -> real RhInitialize/GC/finalizer handshake -> attachment/hijack/managed bootstrap and verify actual attach/detach, roots and shutdown behavior. The standard managed startup wrapper waits for foreground threads and runs Environment.ShutdownCore before wmain returns; background/finalizer and process-shutdown behavior still need real guest acceptance. Do not mark P1 complete based on successful linking.
