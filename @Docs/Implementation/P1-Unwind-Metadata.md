# P1.8 bounded unwind metadata and read primitives

The native unwinder now has reusable validation and bounded-read primitives, exercised on the host before guest integration. The kernel loader policy is unchanged, and the guest RtlVirtualUnwind symbol remains unresolved. Metadata validation alone does not prove a stack walk or handler execution.

## Metadata validation

The validator consumes an immutable image descriptor and mapped image bytes. It validates the descriptor/ranges before reading entries, requires readonly initialized metadata and readable executable function/handler ranges, and checks the complete exception directory with a 4096-entry quota. Sorted function ranges cannot overlap. Integer range checks avoid RVA/address wrapping and image-boundary reads.

Version 1 and version 2 metadata are supported. Checks include complete opcode operands, prolog offsets, frame-register rules, large allocation/save-offset alignment, version-2 epilog records, incompatible handler/chain flags, chain frame consistency, cycles and the upstream 32-link limit. A malformed primary frame cannot be repaired by a frame-setting opcode in a child record. Validation publishes its output record only after success.

Real image inspection corrected two assumptions from the earlier minimal parser: SET_FPREG OpInfo is not consumed by the upstream algorithm (MASM and compiler encodings differ), and runtime helper stubs legitimately save volatile GPR/XMM registers using the standard save opcodes. Register indices remain bounded by their four-bit encoding. The authoritative frame register is the checked header field. These changes are confined to the new validator; the kernel has not started accepting handler/chained images.

Handler routine addresses are checked as code. Language-specific handler data is only located, not claimed to be validated generically. GS/SEH handlers must validate their own payload formats before those consumers are enabled.

## Bounded reads

The stack reader accepts only 8- or 16-byte reads wholly within a supplied stable readable stack interval, with subtraction-based bounds checks. The instruction reader accepts at most 32 bytes entirely inside an initialized readonly executable image range. Refusal does not modify output. Guard-page endpoint and overflow cases are tested.

These primitives do not discover ownership, pin a thread or prove that an arbitrary supplied interval is mapped. The guest adapter must obtain authoritative bounds and lifetime from the kernel and must route every upstream stack/instruction access through checked readers. The upstream algorithm is not yet connected to these primitives, so direct reads in the hosted reference are not being represented as safe guest behavior.

## Evidence

The hosted suite passes 20 metadata cases and 8 bounded-read cases, including malformed/truncated operands, unknown versions/opcodes, writable metadata, non-code handlers, cyclic/excessive chains, version-2 records, invalid primary frames, overflow addresses and an inaccessible guard page. Failure leaves result storage unchanged.

The complete reference executable's metadata is validated, followed by all 2593 entries of the standard-CoreLib NativeAotBoot reference image. The latter contains version-1 plain, chained and handler records plus two version-2 records. RuntimeReadiness now invokes that full-image validation after producing the reference, recording its actual image hash. The existing 14 differential unwind cases still compare the unchanged upstream algorithm with Windows, including an executing compiler GS/SEH frame.

Validation passed: Release build, source audit, hosted probe, final target/source/readiness pipeline, all 20 boot scenarios and four runtime profiles (257 user groups / 55 expected contained faults). The final hosted gate includes all 20 metadata and 8 bounded-read cases plus full-image validation; the 14 differential unwind cases remain passing. No guest unwinder or protected production-context execution is claimed, and P1.8 remains open.

Artifacts: artifacts/runtime-unwind/reference.json, reference.log, image-reference.json and image-reference.log; artifacts/p1-unwind-metadata-source-final.log, p1-unwind-metadata-config.log, p1-unwind-metadata-test.log and p1-unwind-metadata-probe.log.

References: pinned dotnet/runtime win64unwind.h and AMD64 unwinder; [Microsoft MASM SETFRAME](https://learn.microsoft.com/en-us/cpp/assembler/masm/dot-setframe?view=msvc-170). The next consumer must preserve the documented undefined establisher-frame behavior in epilogs described in P1-Unwind-Reference.

Follow-up: [P1-Checked-Unwinder](P1-Checked-Unwinder.md) now connects these readers to the separate checked algorithm and verifies transactional output. The original boundary described above remains historical; guest binding and kernel-confirmed stack lifetime are still pending.
