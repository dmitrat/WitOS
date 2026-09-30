# P1.8 CPU profile and native context storage

Follow-up: [P1-Guest-Unwinder](P1-Guest-Unwinder.md) records actual production GS context/scope/unwinder execution and the explicit runtime PE profile. The results and limitations below describe this earlier implementation checkpoint; managed execution and full exception dispatch remain open.

User ABI v26 adds a checked current-thread CPU-context profile snapshot. This slice also implements PalAreShadowStacksEnabled, PalGetHijackTarget's non-CET fallback, PopulateControlSegmentRegisters and allocation/initialization of the pinned upstream NATIVE_CONTEXT. It does not implement capture/get/set/restore, suspension, hijacking, stack walking or runtime attachment.

## Kernel-owned profile

The x64 backend verifies FXSR/SSE/SSE2 hardware, active OSFXSR and clear CR0.EM/TS. It rejects active FSGSBASE, OSXSAVE, PKE, CET and user interrupts in the profile predicate. Inherited PKE/CET/UINTR is rejected before paging transition; the existing explicit FSGSBASE/OSXSAVE disable policy is retained. Synthetic predicate checks exercise each unsupported mode without enabling it on hardware. Every ordinary/runtime boot requires Cpu.ContextStateProfile.

The 32-byte versioned snapshot reports the preserved legacy x87/SSE state, the actual 512-byte saved area and CS/SS from the current thread's kernel-owned trap frame. It rereads privileged control state, validates version/size and the entire writable destination and copies with interrupts disabled. Raw FS/GS words provide no authority. Invalid or readonly destinations receive no partial output. This is an enabled/preserved-state contract, not a list of everything advertised by CPUID.

PalAreShadowStacksEnabled queries that checked state; discovery failure cannot masquerade as disabled CET. PalGetHijackTarget returns the supplied default only after the same profile check, matching the pinned upstream non-CET fallback. It does not perform a hijack. PopulateControlSegmentRegisters copies actual user CS/SS and leaves other context fields alone. Native context buffers remain caller-owned; the PAL does not turn arbitrary invalid native pointers into managed exceptions.

## Upstream context storage

NativeContext.h is newly pinned from canonical bytes of commit b82454cad0aaaae3db2cf18fbf2cccc36e201ccc, SHA256 66d85281bc391f94a24446e9c9003c59ef42e4782fcb46d13aabdc3840a930b7. The audit verifies 67 source files. The exact verified header is copied into the adapter include directory; no custom context type replaces it.

PalAllocateCompleteOSContext uses the actual nothrow native heap, reserves alignment slack, returns a 16-byte-aligned NATIVE_CONTEXT and the original allocation pointer for delete[]. It initializes storage and CONTEXT_FULL | CONTEXT_DEBUG_REGISTERS. Allocation failure clears the output and reports an error; success preserves native last-error/errno. These zeros are initialized storage, not captured register values. In particular, the debug-register policy and real register capture are still required before this can serve an active runtime thread context.

## Important verification boundary

The production source-archive object retains compiler /GS. Its emitted GS handler metadata is correctly rejected by the current plain-unwind loader. The runtime-source verifier rejects disabling production GS. For this intermediate slice, the config-probe target separately compiles the same source with its existing /GS- profile. Guest tests validate that source-equivalent probe, not byte identity with the protected production object. The image report explicitly records the distinction, and the production object remains in strict runtime linking.

P1.8 MUST later run the protected production object with real handler/unwind support. Passing these probe tests cannot close that requirement. No loader checks were relaxed and no protected production implementation was replaced.

## Evidence

Guest modes 86-88 verify selectors independently with user-mode MOV CS/SS helpers, CPU-profile atomic output/readonly rejection, allocation before image publication/TLS constructors, alignment and initialized fields, unchanged unrelated registers, real heap exhaustion and recovery, worker allocations, no-compiler-TLS execution and fail-fast on a null void output. Kernel checks require complete page/handle recovery. The existing native GS reference tests remain separate evidence.

Validation passed: Release build, all 20 ordinary boot scenarios, runtime-audit/probe/target/source/readiness and four runtime profiles with 248 user groups and 55 expected contained faults each. Full native archive: 123 members; config probe: 39 exact objects. Minimal/broad strict link: 14/20 unresolved dependencies. The COM/context probe image is 31232 bytes with 107 plain unwind records. P1.8 remains open; no guest managed execution is claimed.

Next: kernel-owned register context operations and their validated native conversion, explicit debug-state handling, followed by suspension/hijack, exception/unwind and genuine attach/shutdown. Protected-object execution is an additional outstanding acceptance gate, not a completed item.

Logs: artifacts/p1-context-storage-config.log, artifacts/p1-context-storage-test.log, artifacts/p1-context-storage-audit.log and artifacts/p1-context-storage-probe.log. Production/probe flags and hashes are recorded in runtime-source/runtime-config reports.

Architectural reference: [Intel SDM volume 3A, control-register definitions](https://cdrdv2-public.intel.com/874249/253668-090-sdm-vol-3a.pdf). Runtime contracts: pinned PalMinWin.cpp and NativeContext.h.
