# P1.8.d: actual archived unwinder in WitOS

P1.8.d is complete for the current single-image, single-CPU native profile. The actual source-built protected unwind, scope and PAL context objects execute in QEMU/WitOS. Guest managed .NET, native exception delivery/SEH dispatch and full runtime thread/GC integration remain unimplemented milestones.

## Shared validation and explicit loader profile

The previously hosted-tested metadata parser now lives in the C-compatible common header `witos/unwind_metadata.h`. The native adapter supplies a mapped readonly image view; the kernel supplies a view over immutable PE file bytes using validated section/RVA-to-file mappings. Both use the same opcode, version-1/version-2, chain/cycle/depth, handler-address and range checks. The kernel validates the full directory before allocation and records all visited metadata extents, including chained records, for relocation exclusion. Failed validation discards the provisional plan.

The default restricted PE profile is unchanged and still rejects handler/chained forms. The explicit kernel-selected WIT_PE_UNWIND_RUNTIME profile is used for runtime images with implemented native consumers. It is not a user-controlled SDK switch. Runtime metadata capacity is 256 function entries and 256 distinct metadata ranges; the plain profile remains limited to 128 entries. This change followed an actual fixture measurement of 132 entries exceeding the previous limit. The final expanded fixture has 145 entries, 48,640 file bytes and 65,536 mapped bytes. Image/page/stack/TLS limits were not otherwise increased.

During bring-up, validation also caught an unsorted MASM table: automatic records for a newly appended frame preceded earlier manual records in the merged .pdata fragment. Moving that automatic frame before the manual code/records corrected the producer. The loader's sorted/non-overlapping invariant was retained.

Twelve kernel negative cases run before allocation/publication: unknown profile, runtime table quota, plain-profile rejection of a real GS record, unknown version, non-code handler, unordered/overlapping functions, incompatible chain flags, a cycle, writable metadata, truncated operands at a readonly-section endpoint and relocation into validated metadata. The relocation test first constructs a structurally valid control image whose metadata qword is also a valid image VA; a sorted relocation to an unused adjacent qword is first accepted, then changing only that fixup offset to the metadata header must fail. Both qwords hold valid image VAs, and block/target ordering remains valid. Each rejection checks unchanged page accounting and absence of a published component.

## Guest execution evidence

The new RuntimeUnwindFixture links all six byte-verified production archive objects: scope, guest binding/failure transport, checked adapter, shared-validator bridge, pinned upstream algorithm and direct/import assembly transport. Their GS protection is retained. No Windows implementation library or success stub is linked.

Normal execution checks 35 cases at both supported image addresses: direct/import routes over version-1 prolog/body/epilog frames, large allocation, frame pointer/XMM saves, chained records, handler discovery, version-2 epilog positions and a live compiler GS-protected frame. Comparisons cover the entire CONTEXT, restored XMM6, original saved-register addresses and handler outputs. One route exercises the current-stack fallback with no ambient scope; the other retains an explicit scope. The kernel also checks that the actual import slot is mapped readonly.

The protected compiler frame is captured by an x64 assembly helper with layout assertions against the actual Windows CONTEXT declaration. Unwinding discovers the real __GSHandlerCheck and handler payload. Invoking that consumer on the live frame succeeds; deliberately corrupting its actual encoded cookie produces WIT_NATIVE_GS_FAILURE_EXIT. A stack read beyond the owned range terminates through native fail-fast before an invalid memory read. These are real GS/unwind consumers, not automatic SEH dispatch or managed exception handling.

A second test captures a real protected frame on a worker, parks and suspends it, and enters a foreign scope using its real thread reference. Direct/import unwinds produce identical complete contexts and context-pointer results. The owner consumes the original return-address location after a yield while final Resume remains blocked. After lease release, the worker resumes, runs its own GS consumer and returns normally. The captured-context pointer is published as a volatile pointer; kernel suspension and lease ownership protect the actual backing and lifetime.

The existing context mutation image now links the byte-verified production pal_context, pal_context_storage and unwind_scope objects in place of source-equivalent probes. Existing get/set/restore, aligned storage, SSP/debug/XSTATE rejection, scope nesting, missing-TLS and lost-cleanup cases pass with their GS metadata intact. Earlier standalone storage probes remain historical prerequisites, not the evidence used to close production execution.

## Validation

Passed: Release build; source audit/probe/target/source/readiness; hosted unwind differential/negative/full-image gates; all 20 boot scenarios; four runtime profiles (qemu64 128/512 MiB, Nehalem 256 MiB, max/AVX-advertising 256 MiB). Each runtime profile reports 266 User groups and 55 expected contained faults. The new native fail-fast cases retain their distinct exit causes and resource checks.

Minimal/broad diagnostic links remain 8/14 unresolved. The full archive remains 132 members; the configuration probe now has 46 source objects. Full NativeAotBoot reference metadata still validates all 2,593 entries on the host; that image has not booted in WitOS.

Acceptance snapshot: `artifacts/p1-guest-unwind-acceptance.json` ties the tested boot image, actual object/image hashes and four terminal QEMU results together; it explicitly records nativeGuestExecuted=true and managedGuestExecuted=false. Artifacts: `artifacts/p1-guest-unwind-config-final.log`, `p1-guest-unwind-test-final.log`, `p1-guest-unwind-probe.log`; `artifacts/runtime-config/unwind-image-report.json`, `context-mutation-image-report.json`; `artifacts/runtime-source/witos-unwind-objects.json`; hosted manifests in `artifacts/runtime-unwind/`. Source-only reports are emitted before guest execution; the final image identities and kernel acceptance logs supply the guest evidence.

## Next: P1.8.e

Implement actual exception registration/delivery, RaiseException/fail-fast contracts and __C_specific_handler scope-table dispatch with real language-specific payload validation. The pinned startup registers RhpVectoredExceptionHandler only after RuntimeInstance initialization; its return value currently goes unchecked upstream and needs an explicit port policy. The actual handler expects a real EXCEPTION_RECORD/CONTEXT and uses runtime code managers to translate hardware faults. Do not replace those dependencies with successful callbacks or describe GS-cookie checking as managed EH.

Full-runtime foreign stack walks still need their scopes connected to actual ThreadStore/rendezvous/hijack paths in P1.8.f/g and P3. The native binding and lifetime contract are now executable prerequisites; GC roots, managed attachment and ThreadStore detach remain unproven.
