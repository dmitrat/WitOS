# P1.8 kernel register snapshots

User ABI v27 adds an atomic, read-only snapshot of a live thread's saved user return frame. It is a kernel primitive for later PAL context operations. It does not suspend a thread, stabilize its stack after return, change registers or implement PalGetCompleteThreadContext. Those contracts remain open.

## Authority and lifetime

ThreadContextGet accepts the current-thread pseudo handle or a live generation-bearing thread reference with GET_CONTEXT rights. Query-only and SET_CONTEXT-only references do not grant capture. A join handle is the wrong type. Exited references are rejected before any reaped frame is read, and stale references do not follow a reused thread slot.

The kernel validates the saved frame's alignment and location within the selected thread's owning kernel stack, user CS/SS, owning user-stack bounds and executable RIP. Identity and bounds come from kernel records. Raw TLS identity changes do not affect selection. With interrupts disabled, it builds a zero-initialized 720-byte versioned snapshot and validates the entire destination before copying. Invalid sizes, rights or destinations leave output untouched.

The snapshot reports logical thread state, ownership identity, stack bounds, GPRs, user control state (RIP/RSP/RFLAGS/CS/SS) and the selected FXSAVE64 image. For the current caller it describes the syscall return frame, including the explicit ABI's RAX/RDX/flags clobbers; it is not the pre-call register set. Waiting-thread state is a snapshot of a pending return frame, whose eventual wait result may change after capture. No page/context pin is implied by returning a copy.

## FXSAVE64 sanitization and CPU mode

The existing assembly uses REX.W FXSAVE/FXRSTOR, so the defined layout includes 64-bit FIP/FDP and XMM0-15. Capture copies defined fields only, clears the high reserved FOP bits, each x87-slot padding area, the reserved tail and the software-available region. It never copies the entire uninitialized kernel-stack save area verbatim. A mandatory kernel self-test poisons every source byte and checks both defined data and zeroed padding independently of ordinary zero-filled startup stacks.

The FXSAVE-only profile now also rejects EFER.FFXSR. AMD's fast-FXSAVE mode skips XMM registers at CPL0, so accepting it would contradict this backend's preservation contract. The boot predicate and every profile/capture query inspect actual enabled state; a synthetic predicate test covers FFXSR without enabling it. Existing CR0/CR4 checks and no-CET/PKE/UINTR policy remain in force.

## Guest evidence

The assembly fixture preserves the caller's FP state, establishes real R12/R13 and XMM6 sentinels, loads x87 ST0=1 and changes MXCSR rounding, then enters the real syscall path. Tests verify those values in current-thread and deterministically parked-worker snapshots, not in fabricated contexts. The caller's FP state is restored afterwards.

Tests also cover exact-size and guard-page output, readonly rejection, unchanged output on rights/size/closed-reference errors, writable TLS spoofing, three worker lifetimes with slot reuse, stale references, both image bases and operation without compiler TLS. Kernel checks enforce handle/page recovery and native thread lifecycle. The earlier source-equivalent context-storage probe remains explicitly separate from the protected production object; this slice does not close that GS-handler acceptance gap.

Validation passed: Release build, all 20 ordinary boot scenarios, runtime-audit/probe/target/source/readiness, and four runtime profiles with 250 user groups and 55 expected contained faults each. Full native archive: 123 members; config probe: 40 exact objects. The COM/context fixture is 33792 bytes with 110 plain unwind records. P1.8 remains open. The native link inventory remains 14/20 minimal/broad unresolved symbols because no PAL get/set/restore method is bound to this weaker point-in-time contract.

## Next integration constraint

Pinned PalMinWin.cpp::PalHijack has a real suspend/GetThreadContext/ResumeThread fallback. It calls HijackCallback only when CONTEXT_EXCEPTION_REPORTING is set and neither CONTEXT_SERVICE_ACTIVE nor CONTEXT_EXCEPTION_ACTIVE is set. Thread::HijackCallback also skips preemptive and non-managed execution; only then can Thread::Redirect call PalGetCompleteThreadContext/PalSetThreadContext. The next suspension/context adapter must preserve that distinction for pending kernel waits and cannot advertise safe redirection for an unfinished service frame.

Logs: artifacts/p1-context-capture-config.log, artifacts/p1-context-capture-test.log, artifacts/p1-context-capture-audit.log and artifacts/p1-context-capture-probe.log.

Architectural references: [Intel FXSAVE instruction and layouts](https://cdrdv2-public.intel.com/868140/253666-089-sdm-vol-2a.pdf), [AMD FXSAVE/FFXSR behavior](https://www.amd.com/content/dam/amd/en/documents/processor-tech-docs/programmer-references/26569.pdf), [AMD EFER definition](https://www.amd.com/content/dam/amd/en/documents/processor-tech-docs/programmer-references/24593.pdf).
