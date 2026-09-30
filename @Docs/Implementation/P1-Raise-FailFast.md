# P1.8.e: RaiseFailFastException

This slice adds the real nonreturning direct/import binding. It bypasses vectored/frame handlers and native TLS/atexit/exit notifications. It does not supply Windows Error Reporting or a debugger and does not complete managed exception support.

## Arm before touching optional pointers

User ABI v34 adds FATAL_ARM (60) and FATAL_REPORT (61). The x64 entry arms a component-private irreversible fatal state before reading an optional record/context or allocating the diagnostic stack frame. The syscall number is generated from the shared ABI header. Any subsequent user fault bypasses exception upcalls, and any component completion retains the armed fatal code. Only the initiating kernel thread identity can publish a diagnostic report.

The generic 872-byte kernel report carries an opaque code/flags/address, fifteen bounded parameters and a WitThreadContext diagnostic snapshot. It contains no Windows structure definitions and provides no context-resume authority. Publication copies a complete validated user range before replacing the stored report. The native adapter commits a valid record's cause before reading an optional context, and again before optional output. An invalid context pointer therefore cannot replace a previously captured record code with a secondary access violation.

The adapter uses the existing captured console capability for one bounded message and exits even when output is denied. It uses no heap, compiler TLS, registry lock, C++ exception dispatch or cleanup. Its dedicated /Od /GS- profile avoids recursive dependence on the facilities being reported; this is not a blanket removal of production runtime GS protection. Actual archive/object identity is verified.

## Record and context policy

A supplied record preserves its code, flags, address and bounded parameter values. A supplied CONTEXT is copied as diagnostic data, including GPR and x87/SSE values, without imposing execution/restore restrictions or modifying the caller's object. With no context, the adapter records a real kernel-confirmed current context; it does not claim that this generated snapshot exactly matches the caller.

Windows debugger-reference testing resolved an ambiguity in the published flag description: with no record, the address is generated even for flags zero; FAIL_FAST_GENERATE_EXCEPTION_ADDRESS replaces a supplied address as well as filling a null one. The WitOS adapter follows that observed policy using the actual caller return address.

## Acceptance

The Windows-only child-process debugger oracle observes second-chance exception records and contexts. It verifies default/supplied codes, generated/supplied addresses, supplied RIP/R12 and bypass of both VEH and compiler SEH handlers. No Windows implementation is linked into the guest.

Guest cases cover direct/import calls, default record/context, supplied context with deliberately non-resumable diagnostic addresses, generated address replacement, operation without compiler TLS, closed-console failure, invalid record/context pointers and invocation from an already active hardware handler. Native TLS destructors, atexit and thread-exit notification are registered as negative controls and must not run. The kernel checks the retained report and original exit code, all resource cleanup and diagnostic write counts.

Artifacts: artifacts/p1-raise-failfast-config.log, p1-raise-failfast-test.log and artifacts/runtime-failfast-reference/reference.log/reference.json. Passed: Release build, audit/probe/target/source/readiness, Windows debugger oracle, all 20 boot scenarios and all four runtime profiles (274 User groups / 65 expected contained faults each). Full archive: 135 members; configuration probe: 50 objects; minimal/broad unresolved: 4/10. The hosted oracle can also be run with runtime-failfast.

Remaining P1 requirements include __C_specific_handler/SEH target and collided unwind, general-protection mapping and the actual startup/thread attachment/hijack contracts. Source linking and native fatal behavior do not prove guest managed .NET execution.

Primary API reference: [RaiseFailFastException](https://learn.microsoft.com/en-us/windows/win32/api/errhandlingapi/nf-errhandlingapi-raisefailfastexception). The flag/address detail above is backed by the actual Windows debugger reference, rather than inferred from its ambiguous wording.
