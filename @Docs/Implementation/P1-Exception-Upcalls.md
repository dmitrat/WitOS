# P1.8.e: kernel exception delivery and continuation

This slice provides real CPL3 hardware-fault delivery and validated continuation. It is a prerequisite for the native Windows-shaped exception adapter. AddVectoredExceptionHandler, RaiseException, RaiseFailFastException and __C_specific_handler integration remain open; no managed exception support or guest managed execution is claimed.

## Kernel contract

User ABI v32 adds four private calls: EXCEPTION_REGISTER (55: callback/zero, version, zero flags), EXCEPTION_QUERY (56: token, output, exact size), EXCEPTION_CONTINUE (57: token, WitThreadContext, exact size), and EXCEPTION_REJECT (58: token, zero, zero). Registration is component-private and requires readonly executable user memory; it cannot change while any thread has a pending exception.

Each interrupted thread owns one kernel-resident 768-byte version-1 WitUserExceptionInfo. It records a non-repeating token, CPU vector/error/address, raw diagnostic RFLAGS and the complete canonical 720-byte thread context. Query validates the whole output before copy. Only the currently interrupted thread can query or continue its token; a copied token in another thread has no authority. No user-supplied exception record overwrites this backup.

Context snapshots now expose EXCEPTION_ACTIVE alongside existing suspension/service flags. Production PalGetCompleteThreadContext maps it to CONTEXT_EXCEPTION_ACTIVE, preserving upstream's rule that such threads are unsafe to hijack. Ordinary SetContext/RestoreContext are rejected while an exception is pending. Continuation validates owner, stacks, selectors, flags, RX destination, canonical FXSAVE data and all buffers before mutation. It also refuses outstanding stack leases owned by the caller, including foreign-stack walks, so a nonlocal return cannot abandon them silently. The ordinary current-thread restore path shares that owner-lease check.

On successful continuation, the kernel clears pending state and performs real IRET/FXRSTOR without overwriting restored RAX/RDX/condition codes with syscall results. Invalid input leaves the entire original snapshot available for retry. Raw/normal thread exit, thread reuse and component teardown clear pending state; tokens never repeat after reuse.

## Fault entry and stack policy

The x64 CPL3 fault entry now saves all GPRs and baseline x87/SSE state before using registers for dispatch. It removes only the normalized vector/error slots from the saved return layout and uses the existing context restore routine. The CPL0 fatal path remains separate.

Supported upcalls cover divide error, breakpoint, invalid opcode, general protection and page fault. The callback receives token/vector/address on the interrupted thread's normal stack. The kernel validates its whole call frame and at least 4 KiB of callback headroom; it supplies an aligned Windows-x64 entry stack and a zero return address. There is no automatic stack growth or alternate-stack promise. Missing callback, unsupported vector, exhausted token space or insufficient stack room retains contained-fault behavior.

The callback must explicitly continue, reject or terminate. Returning into the sentinel, faulting again or rejecting preserves the original fault vector/error/IP/address for containment instead of replacing it with the callback failure. RFLAGS in the resume context follows the existing allowed-return mask; the original CPU flags are retained separately for diagnostics. Non-page-fault address fields are zero rather than stale CR2 data.

## Acceptance coverage

The guest fixture triggers actual UD2, divide-by-zero, page read, INT3, invalid-segment and instruction-fetch faults. It continues at a real landing label and verifies changed RAX/R12/condition codes/XMM6 plus restored x87 state. Repeated faults require distinct tokens and reject stale tokens after completion. The same path is exercised without a compiler TLS page.

Failure cases cover bad registration/version/flags, guard-page query/copy-in, wrong token/size, invalid selectors/flags/identity/RIP/FXSAVE, registration changes during dispatch, generic-restore bypass, active stack leases and unchanged original snapshots after rejection. A worker cannot access a parent's token. The concurrent-handler case suspends the parent inside its handler, checks actual production PAL EXCEPTION_ACTIVE reporting and rejected SetContext, then handles its own fault while the parent's snapshot remains live.

Five terminal cases check explicit rejection, nested callback fault, callback return, insufficient stack space and unregister-before-fault. They preserve the original UD2 cause and verify complete resource/registry cleanup. These five expected faults are additional to the prior runtime matrix; handled faults do not produce contained-fault success by themselves.

Artifacts: artifacts/p1-exception-upcall-config-final.log, p1-exception-upcall-test.log and p1-exception-upcall-probe.log. All four runtime profiles pass 268 User groups and 60 expected contained faults. Source audit/probe/target/source/readiness and the hosted exception reference pass. The full archive remains 132 members, configuration probe 47 objects, minimal/broad unresolved links 8/14. The final boot result is recorded in PLAN.md.

## Remaining P1.8.e

Build the actual native exception registry/dispatcher on this kernel transport, convert the saved record to standard upstream EXCEPTION_RECORD/CONTEXT, implement validated search/continue/unwind semantics and the real SEH scope-table consumer, and handle registration failure during startup. Keep the real RhpVectoredExceptionHandler/RuntimeInstance/ThreadStore dependencies. This kernel callback is not a successful substitute for those APIs or for managed EH.

A mandatory hosted Windows oracle now verifies first/last registration ordering (including a nonzero value other than one), real software-exception parameters, ContinueExecution, removal/stale removal and self-removal during dispatch. See tests/Runtime.NativeAot/exception_reference.cpp and artifacts/runtime-exception-reference/reference.json/reference.log. This is reference behavior for the next adapter, not a guest VEH implementation. Its ordering/return contract follows [AddVectoredExceptionHandler](https://learn.microsoft.com/en-us/windows/win32/api/errhandlingapi/nf-errhandlingapi-addvectoredexceptionhandler); software exception flags and bounded arguments follow [RaiseException](https://learn.microsoft.com/en-us/windows/win32/api/errhandlingapi/nf-errhandlingapi-raiseexception).