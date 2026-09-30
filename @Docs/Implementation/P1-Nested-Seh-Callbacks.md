# P1.8.e: nested exceptions handled inside compiler callbacks

Follow-up: [P1-Collided-Unwind](P1-Collided-Unwind.md) supersedes the escaping/collided limitations below for the tested software paths. It also records the later 64 KiB fixed-stack budget. This document retains the earlier returning-callback checkpoint.

The guest now handles a software exception raised and caught inside an active compiler filter or finally callback, then resumes the original dispatch/unwind. This completes the returning-callback cases only. Escaping exceptions, collided unwind, combined GS/SEH payloads and general-protection translation remain open; guest managed .NET has not executed.

## Scope lifetime

The C-specific scope engine brackets actual filter/finally calls with optional before/after callbacks. Before invoking the funclet, the adapter checks kernel-derived current identity and the active dispatch state, then closes its current-thread walk lease. After the funclet returns, it verifies that the original exception token is again the current kernel record and that its owner is unchanged, then reacquires a current-thread lease. ReopenCurrent requires a successfully closed scope and the same kernel-confirmed owner. No foreign-stack lease, suspend requirement or kernel begin/continue protection is relaxed.

The termination cursor advances before calling a finally. An inner handled exception closes its own walk scope and consumes only its own kernel exception record before entering its compiler catch. The outer record and native dispatch state remain available for callback return. This does not yet implement abandoning an outer callback or its dispatch frames.

## Fixed stack and regression repair

The first nested filter fixture exhausted the old 16 KiB fixed stack: a nested page fault addressed 0x0000008000020E58 below the former 0x0000008000021000 bottom. The stack now extends downward to 0x000000800001D000, retaining top 0x0000008000025000, TLS address, thread stride and unmapped guards. Its size is 32 KiB. Page capacity remains 128; this is not automatic stack growth or managed stack-overflow support.

Updated tests derive stack size and fixed ownership from shared boundaries. Thread OOM tests cover every stack/raw-TLS/compiler-TLS allocation failure; they still verify rollback and recovery. The zero-fill fixture checks the entire configured stack. The deliberately low-stack exception fixture derives its address from the generated ABI include.

The subsequent Child fault was not contained regression was a stale assembly test address: TLS minus 0x6001 was no longer below the enlarged stack. Both guard probes now derive their addresses from WIT_USER_STACK_BOTTOM/TOP, preserving the expected fault and exact-address checks. Native software-failure diagnostics also retain the original exception code while reporting the secondary fault vector/address.

## Evidence

- Release solution build passes with no warnings or errors.
- All 20 kernel integration scenarios pass, including guard-page, containment, OOM and timeout checks.
- All four runtime-config profiles pass: qemu64 at 128/512 MiB, Nehalem and max at 256 MiB. Each has 277 User groups and 65 expected contained faults.
- Modes 147/148 run at both image bases. They check inner/outer handler counts, exact order 10/11/12/13, finally count and absence of an active exception after outer completion. Actual archived runtime objects are linked into the guest fixture; User.CompilerNestedSehCallbacks is a required runner marker.
- Source/target/readiness gates and Windows SEH reference pass as part of runtime-config. A subsequent full runtime-source run with the expanded escaping/collided oracle also passes (artifacts/p1-nested-seh-source-final.log). runtime-probe also passes; its managed execution is hosted Windows evidence only.
- Minimal/broad strict-link boundaries remain 3/9 unresolved. Native archive: 137 members; configuration probe: 51 objects. RuntimeUnwindFixture: 79,360 file bytes and 243 unwind records.

Logs: artifacts/p1-nested-seh-build-final.log, artifacts/p1-nested-seh-test-final.log, artifacts/p1-nested-seh-config-final.log, artifacts/p1-nested-seh-probe.log. Source/archive/fixture identity remains recorded in the standard runtime-source/runtime-config reports.

## Next: escaping and collided unwind

The Windows compiler oracle now additionally checks these cases, with explicit return value, finalizer/handler counts and trace assertions:

| Case | Observed trace | Required cleanup |
| --- | --- | --- |
| Exception escapes a filter to an outer catch | 20,22,23 | One finally, one outer handler |
| Exception raised during exceptional finally replaces the earlier unwind | 30,31,32 | Each of two finally blocks once, one handler for the new exception |
| Exception interrupts local return unwind | 40,41 | One finally, one outer handler; original return is abandoned |

These are mandatory HOSTED checks in runtime-seh, not guest acceptance. Evidence: artifacts/p1-collided-seh-reference.log and artifacts/runtime-seh-reference/reference.log/assembly/JSON.

The guest must next reconcile the live handler dispatcher/cursor with the nested walk, avoid replaying an interrupted finally, and retire only superseded native and kernel exception scopes before nonlocal transfer. Merely restoring a TLS pointer or returning ExceptionCollidedUnwind without the accompanying context is insufficient. The current kernel continuation pops exactly one pending record, so an escaping transfer must not leave an abandoned parent record behind. Add compiler guest fixtures matching the oracle, including normal follow-up exceptions and resource cleanup after the transfer.

Reference: [Microsoft x64 exception handling](https://learn.microsoft.com/en-us/cpp/build/exception-handling-x64?view=msvc-170). Actual nested/collided behavior above is established by executable Windows fixtures, not inferred from metadata parsing.
