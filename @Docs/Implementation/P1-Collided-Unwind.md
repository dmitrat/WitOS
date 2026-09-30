# P1.8.e.3: escaping software exceptions and collided unwind

Follow-up: [P1-Gs-Seh-and-Gp](P1-Gs-Seh-and-Gp.md) completes the selected native combined-GS/SEH and GP boundary. Remaining-work statements below describe this earlier checkpoint.

Actual compiler filter/finally frames now support software exceptions escaping a callback, including repeated collisions and preservation of an enclosing active unwind. This is native SEH acceptance, not managed exceptions or runtime startup. Combined GS/SEH metadata and general-protection translation remain P1.8.e.4. Nested hardware faults still follow the explicit contained-failure policy.

## Atomic kernel continuation (user ABI v35)

WIT_CALL_EXCEPTION_UNWIND (62) accepts the current exception token and a complete versioned WitUserExceptionTransfer (736 bytes). RetireThroughToken identifies the current record or an ancestor to retire inclusively. The kernel selects the older record to preserve; the adapter does not need to guess an unobserved parent's token.

With interrupts disabled, the kernel checks exact sizes/version, ownership of both tokens in the current thread's pending chain, scheduling state, absence of held/owned stack leases, the entire readable request, and the existing full register/stack/selector/flag/FXSAVE policy. It copies and validates before changing either registers or the chain. A successful transfer clears retired slots and resumes via the existing checked return path, preserving supplied RAX/RDX/flags. Ordinary EXCEPTION_CONTINUE still pops exactly one record.

Mode 149 exercises four pending records, retirement to two different surviving ancestors, complete retirement, and a subsequent real compiler catch. Thirteen invalid token/version/size/range/register cases plus a live-lease refusal leave the pending record byte-for-byte unchanged. The actual assembly landing verifies restored RAX, R12, XMM6 and condition flags. The test obtains pseudo-reference/structure size from shared C headers rather than duplicated assembler constants.

## Described callback frame and dispatcher resumption

The scope engine calls actual filters/finalizers through a small x64 assembly bridge with explicit search/unwind metadata. Its stack frame retains the paused dispatch identity. The bridge handler validates that identity against the same thread's live native dispatch chain, requires the paused walk scope to be closed, reads the slot through bounded stack access and checks forward progress.

Nested search resumes from the paused logical frame's saved pre-unwind context. It carries EXCEPTION_NESTED_CALL across the affected frame. During phase-two unwind the bridge supplies the saved dispatcher and ScopeIndex, allowing the new walk to resume after an interrupted finally. The cursor was advanced before that callback, so it is not replayed. Termination handlers receive the current unwind context.

After a collision the active dispatcher records the oldest superseded exception token and removes abandoned native dispatch links. Repeated collisions propagate an already selected older cutoff. At the final target, it closes its walk lease and uses the atomic kernel transfer. Older active scopes remain intact; returning into their callback allows the existing token/owner check and lease reacquisition to succeed.

## Compiler acceptance

Windows reference and guest fixtures assert these exact traces and cleanup counts:

| Case | Trace | Finalizers / handlers |
| --- | --- | --- |
| Exception escapes a filter | 20,22,23 | 1 / 1 |
| New exception interrupts exceptional finally | 30,31,32 | 2 / 1 |
| New exception interrupts local return | 40,41 | 1 / 1 |
| Two successive collisions | 50,51,52,53 | 3 / 1 |
| Collided inner unwind returns into retained outer finally | 60,30,31,32,61,62 | 3 / 2 |

Guest modes 150-154 run at both image bases. Each then handles a fresh exception and verifies no pending exception remains. Kernel-side acceptance checks exit result, report, handles/events, walk leases, pending records and page accounting. User.ExceptionScopeTransfer and User.CompilerCollidedUnwind are mandatory runner markers. All previous returning nested-callback cases remain enabled.

## Measured prototype budgets

The new fixture initially exceeded the runtime PE profile's 256-record cap (258 records). The bounded runtime-only cap and tracked metadata ranges are now 320; the final fixture has 265 records and 85,504 file bytes. The ordinary PE cap remains 128. The malformed-image suite still verifies refusal at runtime cap + 1 before allocation, alongside metadata/relocation rejection tests.

Repeated collision also exhausted the old 32 KiB fixed user stack: the third software exception faulted at 0x000000800001CFE8, below the old 0x000000800001D000 bottom. Fixed stacks now span 64 KiB, from 0x0000008000015000 to the unchanged 0x0000008000025000 top. TLS, thread stride, image windows and page capacity remain unchanged. Existing size-derived zero-fill/guard/OOM tests cover the enlarged stack; the PAL discovery assertion explicitly expects 64 KiB. This is bounded committed stack memory, not automatic growth or managed stack-overflow recovery.

## Evidence and next work

Release build, all 20 kernel integration scenarios and runtime-config pass. All four runtime profiles (qemu64 128/512 MiB, Nehalem/max 256 MiB) pass with 279 User groups and 65 expected contained faults each. Audit/target/source/readiness and Windows compiler oracles pass through runtime-config. The separate hosted runtime-probe passes. A subsequent full runtime-source run also passes with the new mandatory combined GS/SEH Windows oracle; see artifacts/p1-collided-source-final.log. Native archive remains 137 members; config probe 51 objects; minimal/broad unresolved boundary remains 3/9. Guest managed execution remains false.

Logs: artifacts/p1-collided-build.log, artifacts/p1-collided-config.log, artifacts/p1-collided-probe.log; Windows traces and input hashes: artifacts/runtime-seh-reference/reference.log and reference.json. The final full boot-matrix result is recorded in artifacts/p1-collided-test.log and root PLAN.md.

Next: P1.8.e.4 combined GS/SEH payload validation and GP policy/translation, followed by the full P1.8.e gate. Attachment/hijack/startup remain P1.8.f/g and P1.9/P1.10. Five native compiler cases do not substitute for the later actual runtime/GC and managed EH acceptance.
