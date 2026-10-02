# P6.2 executable memory and VMToOS adapter

Status: P6.2 completed in the tested x64/UP capability profile. All required gates passed; evidence: artifacts/p6-stage2-evidence.json. This is not guest CoreCLR/JIT execution.

## Memory contract

The architecture backend protects owned dynamic pages as RX, rejects every W+X combination, preserves ordinary non-executable memory-call behavior and publishes instructions with serializing CPUID on the sole online x64 CPU. Publication validates the entire committed RX span. Generic reset rejects executable backing before mutation.

User ABI v38 adds CODE_MEMORY call 65 with a fully copied and validated 64-byte request. Bounded reservation, aliases, protection, publication, sparse views/reset and whole-range access validation preserve state on invalid size, reserved fields and ranges. Memory-info v2 is 112 bytes and reports the separate near-code arena: 0x0000008001000000 through 0x0000008040000000 (exclusive), within rel32 reach of the native image. The pure bounded range selector passed 368640 exhaustive small-model cases plus six overflow/contract cases.

Separate RW/NX and RX/RO aliases share component-owned backing. W^X means no individual virtual mapping is both writable and executable, matching the selected upstream double-mapping model. Aliases cannot expose fixed images/stacks, supervisor or foreign memory, or chain through another alias. Each backing page is counted once. Decommit/release refuses live aliased backing; teardown invalidates translations, prunes empty private tables and frees backing exactly once.

Sparse views reserve address space without consuming RAM. Later backing commits update all registered views in one serialized transaction, with rollback of new backing, aliases and private tables on failure. Source release is blocked even for an uncommitted live view. Recycling zeros existing backing without committing untouched pages.

## Native adapter and acceptance

`src/Runtime.CoreClr/doublemapping.witos.cpp` implements the pinned VMToOSInterface contract. The upstream header has canonical SHA-256 290569807f98c8d73d4eff11b5469654daf1f76b444d6fa9ea2ede708acf4779. Four component-private mapper records, sixteen views and 64 MiB sparse logical backing per mapper are explicit prototype quotas. Tokens do not repeat within the component. Closing a mapper retains backing through live views; RW views must close before the RX block. Reuse observes zeros, and stale identities are rejected. Optional template methods remain unavailable, as in the pinned Windows implementation.

Registration and mapper release share a lifetime gate, preventing registration between the release check and unmapping. Registered code cannot be freed. Reader rundown and actual current/foreign frame walks are described in [dynamic unwind](P6-Dynamic-Unwind.md). The runtime must still quiesce executing code before release.

`coreclr-memory` builds an import-free native component and boots it in CPL3 at 128 and 512 MiB. It verifies actual code execution, write/NX faults, transactional protection, alias coherence and ownership, sparse late commitment, rollback across allocation boundaries, mapper close/reuse and complete teardown. It also checks dynamic exception dispatch, target/collided unwind and malformed context rejection. The x64 fixture is test code, not a substitute JIT. Latest complete capability matrix: artifacts/p6-collided-final-coreclr-memory.log.

## Remaining integration boundaries

The adapter is not yet linked into a source-built guest CoreCLR image. Upstream ExecutableAllocator can fall back to RWX if mapper creation fails; the guest must preserve failure because the kernel rejects RWX. Full runtime integration and actual IL execution remain P6.5. Mutable JIT metadata never uses the static immutable-image unwind cache.

The Windows QEMU process-signal delay remains an observed intermittent host issue. Failed shutdown checks are retained as failures; timeout and native process/job completion requirements have not been relaxed.
