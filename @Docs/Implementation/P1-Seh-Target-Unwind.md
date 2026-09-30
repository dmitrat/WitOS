# P1.8.e: compiler SEH target transfer

Follow-up: [P1-Local-Unwind](P1-Local-Unwind.md) now implements the actual compiler helper and verifies return/goto/leave cleanup in the guest. The missing-helper statements below describe this earlier checkpoint; nested/collided exception orchestration remains open.

This slice defines a real __C_specific_handler and connects the validated scope engine to phase-two unwind and kernel continuation. It is not the completion of full SEH: nested/collided orchestration, _local_unwind and further language-handler edge cases remain open. Guest managed .NET has not executed.

## Search and transfer

A thread-local dispatch frame is established only after kernel-confirmed identity/TLS and an actual current-stack lease. The language handler checks its current dispatch owner, canonical function/table binding, actual handler identity and establisher frame. Filters return real Search/Continue/Target decisions from the existing scope engine.

A positive filter starts a second bounded walk from the original context. The walk invokes actual termination handlers with UNWINDING and, on the selected frame, TARGET_UNWIND. It preserves the pre-unwind context for the selected frame rather than accidentally transferring into its caller. The final context uses the compiler target IP, selected establisher SP, restored nonvolatile state and the exception code in RAX.

Before transfer the adapter validates conversion, closes its own walk lease and restores the prior native dispatch pointer. Kernel continuation then validates the complete state and performs IRET/FXRSTOR. No fake C-specific success, hard-coded catch callback or Windows implementation library is used. Invalid target/handler/frame relationships reject the original exception.

ScopeIndex uses the actual SDK DWORD type and advances before finally invocation. A returned unsupported unwind disposition still fails explicitly; this is not yet a collided-unwind implementation.

## Compiler acceptance

The guest image links byte-verified production handler, scope engine and validator objects. MSVC-generated __try/__except/__finally fixtures test catch-all, a filter reading the original local, abnormal finally before catch, cross-function finally/target unwind, ContinueExecution and normal finally. GetExceptionCode and modified locals are checked at the compiler landing pad. A hardware UD2 path additionally verifies transfer from a kernel upcall into the compiler catch target. Tests run at both supported image addresses and verify no remaining kernel exception state or stack lease.

An unreachable return inside the initial try/finally test caused MSVC to emit _local_unwind. The fallback was moved after the construct while preserving the assertion that RaiseException must not return there. No _local_unwind stub was supplied; support for that distinct return/leave unwind path remains mandatory future work.

Artifacts: artifacts/p1-seh-target-config-final.log, p1-seh-target-test.log and p1-seh-target-probe.log, alongside actual object/image reports under artifacts/runtime-source and artifacts/runtime-config. Passed: Release build, audit/probe/target/source/readiness, runtime-seh, all 20 boot scenarios and all four runtime profiles (275 User groups / 65 expected contained faults each). Full native archive: 137 members; configuration probe: 51 objects; minimal/broad strict link: 3/9 unresolved.

## Still open

Nested exceptions from filters/finalizers, collided unwind and abandonment of older native/kernel dispatch frames require explicit orchestration. Current walk leases deliberately prevent unsafe nonlocal software scopes; that safety rule must not be removed merely to make nesting return success. Add real compiler cases for these paths and _local_unwind before closing P1.8.e. General-protection translation and combined GS/SEH payloads also need their own acceptance where required by the selected runtime.

The minimal link no longer needs a missing C-specific implementation, but linking is weaker evidence than completed exception semantics. Runtime attachment, GC rendezvous and the guest executable driver remain separate P1 requirements.
