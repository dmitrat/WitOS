# P1.8.e: native software exceptions

This slice supplies actual RaiseException capture/dispatch/continuation and kernel-owned software nesting. Full language-specific SEH target/collided unwind, fail-fast API integration and managed EH remain open. Guest managed .NET has not executed.

## ABI v33 scope lifecycle

EXCEPTION_BEGIN (59) accepts a complete caller context, exact size and opaque 32-bit software code; it returns a generation-bearing token. It validates the whole input, CPU state, owner, stack bounds, selectors, flags and RX continuation before publishing anything. Failure leaves the result zero and does not create pending state. A caller with outstanding walk leases cannot begin a nonlocal software scope.

Each thread retains its current exception plus up to three parents. A nested software continuation restores only its parent, keeping the outer EXCEPTION_ACTIVE state. A root continuation clears it. Tokens are nonrepeating; query/continue operate only on the current top token. Hardware faults inside a pending scope remain contained rather than recursively consuming unbounded stack. Rejection/fault during a software scope reports its saved opaque software code and terminates with WIT_EXCEPTION_SOFTWARE_FAILURE_EXIT. Thread/component teardown clears every parent record.

## Real caller capture

The x64 RaiseException entry saves original GPRs, caller continuation RIP/RSP, flags and x87/SSE state into a standard CONTEXT. Its special stack allocation has real unwind metadata. The direct entry and readonly import slot share this capture implementation. The adapter obtains authoritative identity/bounds/nesting metadata from the kernel, converts the captured state through the shared validated context mapping, starts a software scope and runs the real dispatcher. Successful handling reaches the original caller through kernel IRET/FXRSTOR; no success-return stub is used.

The record carries the code, flags, at most fifteen argument words and a software-origin flag. A null argument pointer means zero parameters even when the supplied count is large. Unsupported flags or a non-null array exceeding the parameter bound fail explicitly. Argument pointers remain native caller-owned inputs; no managed marshaling or heap substitute is introduced.

The initial software context identifies the caller continuation site. Native handler search now uses return PC minus one for a software starting frame and for subsequent unwound frames, while retaining the actual continuation PC in CONTEXT. A real frame test exposed and verifies this correction: otherwise the return address immediately after RaiseException was mistaken for an epilog and its handler was skipped. C-specific scope-table handling remains incomplete.

## Noncontinuable semantics

The hosted Windows oracle established a distinction that a blanket noncontinuable check would miss: explicit vectored continuation can return even with the noncontinuable flag, whereas attempted frame-handler continuation produces EXCEPTION_NONCONTINUABLE_EXCEPTION. The native dispatcher follows that split. A real language-handler fixture exercises the frame path, and the secondary record retains the originating record. Repeated invalid frame continuation is bounded and terminates rather than recursing forever. This does not replace __C_specific_handler with a fake implementation.

## Evidence

Guest tests cover direct/import raises, all fifteen parameters, null-pointer/count semantics, software-in-software nesting, software raised inside a real hardware handler, restoration of the outer pending scope, preserved native error/errno and no residual active state after return. Negative kernel-begin cases check invalid identity/selectors/flags/RIP/size without publication. Separate cases distinguish VEH noncontinuable continuation, invalid parameter count and rejected frame continuation with a real secondary record.

The Windows reference independently verifies parameter handling, nested continuation, VEH behavior and a genuine compiler SEH filter/catch path. It can be run independently with runtime-exception. Passed: Release build, source audit/probe/target/source/readiness, the Windows exception oracle, all 20 boot scenarios and all four runtime profiles (273 User groups / 65 expected contained faults each). Full native archive: 134 members; configuration probe: 49 objects. Minimal/broad strict link is 6/12 unresolved. P1.8.e remains open.

Artifacts: artifacts/p1-raise-config-final.log, p1-raise-test.log, p1-raise-probe.log, and artifacts/runtime-exception-reference/reference.log/reference.json. The hosted reference is not guest runtime evidence.

## Remaining work

Implement __C_specific_handler scope/filter/finally behavior and actual target/collided unwind, preserve proper call-site PC treatment, complete fail-fast bindings and general-protection mapping, and integrate full RuntimeInstance/ThreadStore/GC exception paths. Bounded native software exceptions alone do not complete P1.8.e or the managed compatibility contract.
