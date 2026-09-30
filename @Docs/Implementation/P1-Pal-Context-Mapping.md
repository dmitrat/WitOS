# P1.8 PAL context mapping and inactive debug state

Follow-up: [P1-Guest-Unwinder](P1-Guest-Unwinder.md) records actual production GS context/scope/unwinder execution and the explicit runtime PE profile. The results and limitations below describe this earlier implementation checkpoint; managed execution and full exception dispatch remain open.

User ABI v30 and CPU-profile snapshot v2 expose a checked inactive-debug policy and the kernel's hardware MXCSR mask. PalGetCompleteThreadContext, PalSetThreadContext and PalRestoreContext now translate the pinned upstream NATIVE_CONTEXT into real kernel snapshot/set/restore operations. GetSSP/SetSSP follow the explicit non-CET profile. This is still intermediate PAL evidence: production GS-handler execution, unwind/exception delivery and runtime attachment remain open.

## Checked debug and state profile

At boot the x64 backend rejects non-neutral inherited DR7 control, clears inactive DR0-DR3 address slots and verifies their state. Every context-profile query checks that hardware breakpoint control remains disabled and the address slots remain zero. The native context's zero debug fields represent the supported disabled per-thread debug configuration; raw DR6 reserved/status bits are not advertised as a debugger API. Programming nonzero debug registers, branch tracing/vector controls and XSTATE is unsupported and rejected.

The CPU snapshot remains 32 bytes and now reports DebugPolicy and MxcsrMask. Windows CONTEXT declarations stay in the native adapter. Kernel headers and register operations retain their own architecture contract.

## Get, set and restore

Get requires the full legacy integer/control/FP/debug group, a valid aligned native buffer and a suspended target with GET_CONTEXT rights. It obtains actual kernel GPR/FXSAVE64 data, produces the upstream CONTEXT layout, reports CONTEXT_EXCEPTION_REPORTING and marks a pending kernel wait as CONTEXT_SERVICE_ACTIVE. It never presents a running thread as a stable suspended context. Failure before publishing the result leaves the caller's context unchanged.

Set does not require GET_CONTEXT. A separate SET-authorized kernel metadata query exposes identity, owning bounds, state, count and FP mask, while leaving all register fields zero. The adapter converts caller register values onto that metadata and invokes the existing all-or-nothing kernel setter. Pending services, unsupported state, inconsistent standalone/FltSave MXCSR values and nonzero debug/vector state fail explicitly. The kernel still validates selectors, addresses, flags, FP mask and reserved bytes.

Restore uses current-thread metadata, converts the supplied native context and enters the real non-returning kernel restore path. Invalid void calls fail fast, preserving a diagnostic native error where available. Successful restore does not return through a fabricated success path.

The non-CET context has no shadow-stack record. GetSSP returns zero after profile validation. SetSSP accepts zero without changing the context and fails closed on a nonzero request or extended-state context. The pinned EHHelpers caller that advances SSP is guarded by PalAreShadowStacksEnabled. CONTEXT_XSTATE includes the AMD64 architecture bit, so extended-state detection checks the complete flag, not any overlapping bit; the initial overlap bug was caught by guest tests and fixed.

Native context buffers remain caller-owned, as for the other native PAL routines; invalid arbitrary pointers are not converted into managed exceptions by this adapter.

## Evidence and remaining boundary

The full source archive retains /GS on both context implementations; the verifier rejects /GS-. Intermediate guest probes compile the same sources under the existing explicit /GS- profile. This preserves the production target but does not prove execution of its GS handler metadata. That requirement must be closed with actual unwind/handler support before P1.8 is complete.

Guest tests exercise get/set on a genuinely suspended spinning thread, native-to-kernel register conversion, SET-only handles, refusal of GET through a SET-only handle, nonzero debug state, MXCSR coherence, real XSTATE rejection, legacy SSP preservation, invalid restore/SSP fail-fast and an actual PAL restore roundtrip through IRET/FXRSTOR. Register/FP values and resumed execution are checked, not just success markers. The enlarged native frame uses the existing verified __chkstk object. No compiler probe or loader check was disabled.

Validation passed: Release build, all 20 boot scenarios, runtime-audit/probe/target/source/readiness and four runtime profiles with 257 user groups and 55 expected contained faults each. Full native archive: 126 members; config probe: 43 objects. Minimal/broad strict link: 9/15 unresolved symbols. The context fixture is 33280 bytes with 101 plain unwind records. Guest managed execution and full production context execution remain unproven.

Remaining minimal dependencies are PalInitComAndFlsSlot, PalAttachThread, PalHijack, RaiseFailFastException (direct/import), AddVectoredExceptionHandler, RaiseException, __C_specific_handler and RtlVirtualUnwind. The actual upstream AMD64 unwinder in src/coreclr/unwinder/amd64 exposes VirtualUnwind with handler type, function entry, CONTEXT, establisher frame, context pointers and handler result. Its target-memory/module lookup dependencies need a real WitOS adapter; no unwinder implementation is claimed yet.

Logs: artifacts/p1-pal-context-config.log, artifacts/p1-pal-context-test.log, artifacts/p1-pal-context-audit.log and artifacts/p1-pal-context-probe.log. Production/probe commands and hashes remain explicit in runtime-source/runtime-config reports.
