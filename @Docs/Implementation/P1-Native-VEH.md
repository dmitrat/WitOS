# P1.8.e: native vectored hardware exception dispatcher

This slice connects a component-private native vectored registry to ABI v32 hardware upcalls. It keeps software RaiseException, full SEH target/collided unwind and managed exception acceptance open. General-protection decoding is also explicitly unsupported by this adapter until a real instruction/status mapping exists; the kernel preserves its original fault.

## Registry and lifetime

AddVectoredExceptionHandler and RemoveVectoredExceptionHandler have native direct/import bindings. The registry holds eight registrations, each with a monotonically generated opaque identity. A nonzero First inserts at the front; zero appends. Registration validates the actual published image and an initialized readonly executable callback, and requires kernel-confirmed compiler TLS. Exhaustion and invalid input return failure; successful operations preserve native error state.

The registry gate is released before invoking a callback. Dispatch snapshots registration identities, then resolves each identity under the gate immediately before invocation. Removal or slot reuse cannot redirect an existing snapshot to a new registration, and self-removal is supported. Code lifetime is the current immutable single image; this does not implement DLL unload or JIT code reclamation. Removing the last registration leaves the fixed kernel dispatcher installed for the component lifetime so no pending upcall is silently unregistered.

Kernel-derived identity detects reentry into a gate held by the same thread. The initial kernel callback is installed only after real registration succeeds. The generated startup overlay checks AddVectoredExceptionHandler's result after RuntimeInstance initialization, returning failure instead of proceeding with a nonexistent runtime handler. Windows reference sources remain unchanged.

## Real dispatch and continuation

The adapter queries the kernel-owned original snapshot and builds the standard EXCEPTION_RECORD/CONTEXT pair. Mappings cover divide error, breakpoint, invalid opcode and page faults, including read/write/execute access parameters. CONTEXT_EXCEPTION_ACTIVE is retained. General protection is not mislabeled from the vector alone.

Handlers run in registry order. ContinueExecution passes the modified context through the same legacy-FXSAVE/debug/XSTATE conversion rules used by PAL and then through kernel continuation validation. ContinueSearch advances to a bounded real frame walk using the archived RtlVirtualUnwind and an enclosing stack lease. Real language-handler pointers and metadata are invoked; GS checking is a genuine consumer. The adapter does not supply a successful __C_specific_handler substitute. Unimplemented collided/target-unwind dispositions, invalid return values or unsafe continuation preserve the original kernel fault through rejection.

Context conversion has been factored into context_conversion.witos.h, shared with PAL get/set/restore. Exception continuation is an explicit conversion mode; ordinary PAL set/restore still refuses exception-active state.

## Acceptance scope

Guest tests exercise real CPU faults through ordering/front insertion, payload/context mapping, continuation, removal/stale removal/self-removal, quotas, invalid callbacks and missing-TLS prerequisites. Separate cases require original-cause containment after an unhandled exception, invalid returned selector, invalid disposition and unsupported general protection. The actual production exception/context/unwind objects are linked and hash-verified; no Windows implementation library is linked into the guest.

All four runtime profiles pass 271 User groups and 65 expected contained faults. Release build, all 20 boot scenarios, source audit/probe/target/source/readiness and the hosted Windows oracle pass. Actual GS-protected frame-search tests verify a valid cookie reaches normal search completion, while corrupting that cookie produces the real GS fail-fast through the dispatcher. The full archive has 134 members; configuration probe 48 objects. Minimal/broad strict link is now 7/13 unresolved. Artifacts: artifacts/p1-native-veh-config-final.log, p1-native-veh-test.log, p1-native-veh-probe.log and p1-native-veh-source-final.log. Source-only reports remain distinct from guest acceptance.

## Still required for P1.8.e

Implement software RaiseException/noncontinuable behavior, real fail-fast bindings, the language-specific SEH scope/filter/finally consumer and target/collided unwind. Integrate the actual RhpVectoredExceptionHandler with RuntimeInstance/ThreadStore and test supported managed/native transitions. Hardware callback success alone does not complete those contracts or demonstrate guest managed .NET.
