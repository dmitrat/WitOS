# P1.8 validated kernel context mutation and restore

User ABI v29 adds atomic register replacement for a suspended thread and a separate non-returning restore operation for the current thread. The implementation transfers real execution through the existing checked IRET/FXRSTOR path. PAL/Windows CONTEXT conversion, debug-state policy, exception/unwind and actual runtime attach/shutdown remain separate open requirements.

## Authority and validation

Set requires a live generation-bearing reference with SET_CONTEXT rights, a positive suspend count and a ready target with no pending kernel wait. It refuses current-thread set; current execution transfer uses Restore instead. Restore accepts only the current running, unsuspended thread. Pending service frames cannot be redirected. No mutation implicitly resumes a target.

Both operations first copy the entire 720-byte input into kernel storage with interrupts disabled. Before touching the saved frame they validate version/size, target identity, actual owning stack bounds, expected state/suspend count, exact supported flags and reserved fields. These caller metadata fields are cross-checks, never the source of authority. CS/SS must be the actual user selectors. RIP must resolve to user-executable memory, and RSP must lie in the selected thread's own writable fixed stack. Kernel/supervisor mappings remain inaccessible. RFLAGS accepts only the existing user-return arithmetic/direction/ID bits with IF and the fixed bit set; IOPL and other unsupported controls are rejected.

The saved frame pointer is separately checked against its owning kernel stack. All GPR/control/FP writes occur only after every validation succeeds. Invalid inputs leave the previous saved context byte-for-byte unchanged.

## Floating-point state

The kernel now captures MXCSR_MASK from a zeroed aligned hardware FXSAVE area at boot, using the architectural 0xFFBF fallback only when the reported mask is zero. A user-supplied mask cannot authorize extra bits. Snapshots publish that canonical mask. Set/restore reject unsupported MXCSR bits and noncanonical reserved/software bytes, including FOP reserved bits and x87-slot padding. The normalized FXSAVE64 state is committed only after validation. This avoids a user-triggered privileged FXRSTOR fault from invalid MXCSR.

## Current-thread restore

A successful restore replaces the current saved return frame and reinstalls its kernel-owned FS/GS bases. The dispatcher returns that frame directly without overwriting restored RAX/RDX or RFLAGS with ordinary syscall results. Failure follows the ordinary status-return path. This is a real control transfer, not a successful return from a function named Restore.

## Evidence

A separate context-mutation image stays within the unchanged loader limits. Its assembly fixture captures a live context, restores to a named landing point and checks restored RAX, R12, RFLAGS, XMM6, x87 ST0 and MXCSR. The fixture then restores its caller's FP/nonvolatile state. The path also runs without compiler TLS.

For another thread, an uncooperative assembly loop is stopped through the real scheduler. Tests replace its RIP, GPR and SSE state while suspended, query the committed result, resume it and verify the values observed by the landing code. Negative cases cover rights, current-thread misuse, pending kernel waits, invalid metadata/version/counts, kernel selectors, unsafe flags, non-executable RIP, stack guard bounds, MXCSR/mask tampering, reserved FP bytes, cross-page input, exited/stale references and unchanged snapshots after each rejection. Valid input can be supplied from readonly user memory. Supervisor checks require native thread lifecycle and complete page/handle recovery.

Validation passed: Release build, all 20 ordinary boot scenarios, runtime-audit/probe/target/source/readiness and four runtime profiles with 255 user groups and 55 expected contained faults each. Full native archive: 125 members; config probe: 42 exact objects. The isolated mutation fixture is 27648 bytes with 85 plain unwind records. Minimal/broad link remains 14/20 unresolved; P1.8 is open. This slice does not yet bind PalGetCompleteThreadContext/PalSetThreadContext/PalRestoreContext or claim managed GC rendezvous. The production GS context-storage object still needs actual handler/unwind execution; the earlier distinct probe remains identified as such.

Logs: artifacts/p1-context-set-config.log, artifacts/p1-context-set-test.log, artifacts/p1-context-set-audit.log and artifacts/p1-context-set-probe.log.

Reference: [Intel SDM, MXCSR_MASK and reserved-bit validation](https://cdrdv2-public.intel.com/835781/325462-sdm-vol-1-2abcd-3abcd-4.pdf), section 11.6.6.
