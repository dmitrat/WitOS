# P1.8: checked upstream AMD64 unwinder

Follow-up: [P1-Guest-Unwinder](P1-Guest-Unwinder.md) records actual production GS context/scope/unwinder execution and the explicit runtime PE profile. The results and limitations below describe this earlier implementation checkpoint; managed execution and full exception dispatch remain open.

Completed boundary: P1.8.c, hosted algorithm adaptation. Guest binding, kernel-confirmed stack lifetime, handler execution and production-context execution remain P1.8.d/e. Guest managed .NET has not executed.

## Implementation

The pinned upstream AMD64 algorithm is compiled twice. The original reference changes only its environment include. A separate checked copy uses exact, single-occurrence replacements for stack reads, instruction-buffer access and unwind-info lookup; class/wrapper names are isolated by the environment header. Source audit still verifies all four canonical upstream unwind files. The generated copies and adapter input hashes are recorded in `artifacts/runtime-unwind/reference.json`.

The checked wrapper validates the entire readonly exception directory and all reachable metadata before accepting a canonical function-table entry. A caller-provided substitute pointer is rejected without dereferencing it. Metadata extents include alignment and chain/handler trailers. Direct metadata access by the upstream algorithm is confined to those prevalidated immutable extents; it is not permission to execute language-specific handlers or read their payloads.

All stack reads go through checked 8/16-byte readers. Instruction fetches validate initialized readonly executable ranges, reject offset overflow, and consume a bounded per-call budget of 65,536 accesses. Metadata/module/function lookups share that budget. Whole-image validation is separately bounded by 4,096 function entries, 33 records per chain and the metadata opcode-count field. The original upstream unwind operations and version-1/version-2 decoding remain intact.

A thread-local scoped state records the image and supplied stable stack range. CONTEXT, handler data, establisher frame, handler pointer and nonvolatile context pointers are staged locally and published only on success. Final RSP must remain within the stack interval, including its upper endpoint after the final pop; a reported handler's establisher must be inside it. Epilog establisher-frame values remain undefined as in the existing reference contract.

## Failure and native compilation

Hosted access failure throws a specific test exception. This exercises restoration of scoped state and lets the harness verify that every caller output remains unchanged. A subsequent valid unwind must succeed after failures. This exception transport is implemented only in the host test, not in the production adapter.

The same checked adapter, validator and upstream copy also compile separately with `/GS`, `/GR-`, `/EHs-c-`, `/Zl`, `/O2`, `/W4 /WX`, without test-environment headers. The native-object audit rejects Windows imports and hosted C++ exception runtime dependencies. Actual object hashes and remaining references are recorded in `native-objects.json`: GS helpers, compiler TLS, native byte helpers and the explicit failure callback remain real dependencies. These are compile-only objects, not a linked or booted guest runtime. No throwing implementation is supplied to the guest; failure delivery must not recursively invoke the unwinder being implemented.

## Acceptance evidence

- 20 differential cases compare checked adaptation, original upstream and Windows: prolog/body/epilog, large stack allocation, frame pointer, XMM, chain/handler discovery, an executing compiler GS/SEH frame, five version-2 function/epilog positions and a partially populated context with valid ContextFlags and poisoned unused fields.
- 13 negative cases require unchanged full output state: guard-page return-address read after an earlier register restore, initial/empty/out-of-range stack, bogus function pointer, unsupported handler flags, out-of-function PC, truncated instruction, malformed metadata, invalid frame pointer, invalid machine-frame RSP, XMM read across a guard page and explicit work-budget exhaustion. The budget test requires the budget failure reason, not any generic refusal.
- Existing 20 metadata and eight bounded-read cases remain mandatory. Full-image validation passes all 2,593 entries of the standard-CoreLib NativeAotBoot Windows reference.
- Release tool build, runtime-audit (71 files, included in runtime-unwind/source), runtime-probe, runtime-target/source/readiness passed. Minimal/broad diagnostic links remain 9/15 unresolved. All 20 boot-regression scenarios passed; these guest binaries do not yet include the new unwinder.

Artifacts: `artifacts/p1-checked-unwind.log`, `p1-checked-unwind-source-final.log`, `p1-checked-unwind-probe.log`, `p1-checked-unwind-test.log`; `artifacts/runtime-unwind/reference.json`, `reference.log`, `native-objects.json`, `native-build.log`, `image-reference.json` and `image-reference.log`.

## Next guest boundary

The current adapter deliberately receives a trusted readable image and a trusted stable stack interval. Bounds checks alone cannot prove ownership, mapped backing or stability. The guest binding must acquire that authority from kernel-owned state for the current or suspended target thread; a writable CONTEXT/TLS value cannot provide it. Foreign stack lifetime must survive preemption, attempted resume/set-context, thread exit and slot reuse for the whole walk. Ordinary counted suspension is not by itself an exclusive lifetime token.

The existing guest loader still rejects handler/chained metadata. Its policy must change together with tested consumers and rollback/malformed-image tests. `__imp_RtlVirtualUnwind` remains unresolved; language-specific GS/SEH dispatch and actual protected context objects have not executed in WitOS. Returning a handler address in a hosted comparison does not close those requirements.

## Actual code-manager call contract

Pinned CoffNativeCodeManager callers can leave HandlerData uninitialized and populate only the CONTEXT fields used for that frame. The wrapper therefore byte-copies context/pointer structures, starts its private HandlerData slot at null and writes the caller slot only if a handler was found. The no-handler path preserves caller bytes without reading their prior pointer value.

The partial-context differential test compares the entire result and uses valid CONTEXT_FULL flags. Deliberately poisoning ContextFlags as well initially exposed a reference difference: current Windows rewrites those unsupported flag bits while the pinned upstream algorithm preserves them. That observation is not used to alter the algorithm or skip result fields; the Windows comparison now supplies valid flags and continues to poison unrelated register fields. Guest callers are not required to initialize unused fields by this wrapper.