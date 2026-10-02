# P6.3 assembly delivery and readonly storage

Status: P6.3 completed. Guest acceptance at 128/512 MiB and all required common regressions passed. This is assembly transport, not guest CoreCLR/JIT execution.

## Delivery and immutable ownership

`coreclr-storage` packages the managed DLLs from the pinned 10.0.8 framework, an ordinary SDK-built CoreClrProbe.dll, its unmodified deps/runtimeconfig JSON and an empty-file control: 176 files / 63,092,380 payload bytes. The CoreLib product VMR is checked against the runtime lock; the package manifest records SHA-256 for every original byte sequence. PE files without a CLR header are excluded. Stored framework bytes are reference payloads, not proof that their platform-dependent BCL code runs in WitOS.

The initial embedded 60 MiB EFI payload booted and passed at 512 MiB, but firmware could not load it at 128 MiB. A separate file also exposed failure of one contiguous physical allocation (loader phase 7). Delivery now uses WITOS.PAK beside EFI/BOOT/BOOTX64.EFI. Firmware reads at most 1 MiB per call into up to 128 individually allocated extents, below the existing 4 GiB physical ceiling, then closes all file handles before the final memory map / ExitBootServices sequence. Errors free every allocated extent; no partial descriptor is published.

Boot ABI v4 carries only physical base/length extents and logical byte length, never UEFI structures. The x64 layer validates the descriptor array, reserved-memory ownership, bounds and non-overlap, then assembles a contiguous supervisor-only readonly/NX view. The common kernel validates the full package before publishing it. User CR3s share only the validated supervisor storage branch; it is never used as a user or scratch page-table branch. Firmware memory remains reserved. The package stays kernel-owned for the boot lifetime; file handles own no backing pages.

The FAT16 builder preserves 32 MiB volumes for small inputs; the complete framework uses a 128 MiB volume with 8 KiB clusters. The kernel EFI file remains small. App payload delivery does not AOT-compile or rewrite DLLs.

Firmware declarations were checked against [UEFI Loaded Image](https://uefi.org/specs/UEFI/2.10/09_Protocols_EFI_Loaded_Image.html), [EDK II SimpleFileSystem](https://github.com/tianocore/edk2/blob/master/MdePkg/Include/Protocol/SimpleFileSystem.h) and its [boot services layout](https://github.com/tianocore/edk2/blob/master/MdePkg/Include/Uefi/UefiSpec.h).

## Package format

The private little-endian WITPAK01 format has a 32-byte header: eight-byte magic, version 1, header size 32, entry count, entry size 32 and exact total length. Each 32-byte entry holds UTF-8 name offset/length (u32), payload offset/length (u64), then zero flags/reserved words (u32). Names are contiguous after the index and sorted by ordinal UTF-8 bytes. Payloads follow at eight-byte boundaries; padding must be zero. Empty files and the canonical empty package are supported.

Names are case-sensitive relative paths, preserved without Unicode normalization. Empty/dot/dot-dot components, backslashes, colons, ASCII controls, invalid UTF-8 and duplicates are rejected. Limits are 1024 files, 1024 UTF-8 bytes per name and 128 MiB total. The independent common C parser validates the complete index, exact contiguous ranges, sorting and padding before publication. Failed open/find/get operations preserve output fields. No cache trusts mutable descriptors.

## Guest IO contract

User ABI v39 adds private FILE call 66 with a copied 64-byte request. OPEN accepts a counted canonical UTF-8 path; LENGTH, READ, READ_AT and signed SEEK use a component-owned generation-bearing readonly handle. READ_AT does not move the cursor. READ/SEEK cursor updates are serialized with interrupts disabled; failed calls preserve cursor and destination. Reads validate the entire requested destination before writing, even at EOF. Zero-byte reads are no-ops; each call is bounded to 64 KiB. Negative/overflowing seeks, unsupported operations/flags and reserved fields fail explicitly.

The native adapter in System.Native/file.c invokes real syscalls and preserves caller outputs on failure. This does not yet implement CoreCLR's full native file/mapping PAL, filesystem writes, asynchronous IO or host resolution policy; those remain integration/BCL work in P6.4?P6.7.

## Executed acceptance and remaining gates

- 9748 readonly guard-boundary native parser cases include every truncation of a writer-produced package (both original and repaired declared lengths), structural/UTF-8/padding/overlap failures, bit mutations, native lookup and byte-exact payload extraction.
- 14 hosted firmware cases run the actual loader against controlled protocol implementations: partial reads, oversized counts, missing/open failures, first/later allocation failure, read/EOF/close failures, complete rollback and transactional descriptor publication. Service offsets are asserted independently.
- Both 128/512 MiB guests stream all 176 files twice through native handles. Per-file length and FNV-1a checks cover every delivered byte; SHA-256 provenance is recorded separately by the host, not claimed as guest cryptographic validation.
- Guest checks cover EOF/beyond-EOF, signed seek, whole-buffer rejection, a destination aliasing the copied request, wrong type, foreign/stale handles, quota exhaustion/recovery, automatic close and exact page reclamation. Three actual CPL3 faults prove read/write/execute denial for supervisor storage. Plain PE admission remains strict; the test's isolated fault entry avoids compiler-generated chained unwind in its large IO workload.

Latest capability evidence: artifacts/p6-storage-scatter3-guest.log (both profiles), artifacts/p6-storage-parser-depth.log; byte/FAT proof: artifacts/p6-storage-byte-evidence.json. Release, 36 host groups / 555 PE inputs, all 20 kernel scenarios, code-memory regression and audit/probe/target/config/source/boot gates passed for user ABI v39 / boot ABI v4. Final source and gate hashes are in artifacts/p6-stage3-evidence.json. No guest CoreCLR startup or general filesystem compatibility is claimed.
