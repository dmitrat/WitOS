# ADR 0004: Bounded guest PE image loading

**Status:** Introduced in WitOS 0.0.8; extended with plain unwind metadata and image handoff in 0.0.9.
**Date:** 2026-09-17.
**Scope:** Native ring-3 images. Guest .NET is not running.

## Decision

Add a guest PE32+ loading path alongside the earlier one-page raw-code fixtures. Embed complete test files, validate their bytes inside the guest, create a fresh private address space, map sections with final permissions, apply relocations and publish the component only after its startup state and main thread exist.

The common kernel's `pe.c` validates file structure into a bounded plan without allocating memory. The x64 `user_image.c` maps that plan using private user pages. UEFI types and services do not enter either path.

| Alternative | Decision |
| --- | --- |
| Keep extracting a single .text page on the host | Retained for earlier regression fixtures; cannot test guest section loading or relocations |
| Bounded native PE profile | Chosen: exercises the format/calling convention measured by the NativeAOT experiment without implying Windows compatibility |
| Accept arbitrary PE/DLL/TLS/import graphs | Deferred until module lifetime, binding, TLS and exception contracts exist |

There is one image per new component. This is an internal kernel creation API, not a filesystem loader or a new user syscall. Current ABI v5 adds a readonly [image description](M2-Native-Module-Bootstrap.md) in the startup block.

## Accepted profile and bounds

| Property | Current rule |
| --- | --- |
| Format | AMD64 PE32+, native-subsystem executable; no DLL semantics |
| File / mapped size | At most 1 MiB / 256 KiB |
| Sections | 1–16; read permission required; RWX, shared and special caching flags rejected |
| Alignment | Section alignment 4096; file alignment a power of two from 512 through 4096 |
| Headers | Complete section table, at most one 4096-byte page |
| Entry | Inside initialized bytes of an RX section, not headers, BSS or padding |
| Directories | Base relocations, bounded plain x64 function/unwind metadata; file-backed debug directory as opaque data |
| Unsupported | Imports/IAT/delay imports, exports, TLS, exception handlers/chained unwind, load configuration/CFG, resources, certificates, CLR metadata and other nonempty directories |
| Relocations | At most 2048 entries including padding; ABSOLUTE padding and DIR64 only |

The parser checks signatures, optional-header sizes, directory pairs, raw-file bounds, virtual bounds, integer overflow, raw overlap and page overlap. Section names do not grant permissions. Disjoint sections may leave gaps, which remain unmapped; sections cannot share a page. SizeOfImage must match the rounded end of the final mapped section.

ImageBase must be a nonzero, 64 KiB-aligned lower-canonical preferred address with a bounded extent. The caller chooses an actual 64 KiB-aligned base within the separate image window:

```text
0x0000008000100000 <= actual base < 0x0000008000200000
```

The complete image must fit. This window does not overlap startup data, legacy fixture data/code or per-thread stacks/TLS. Dynamic memory calls operate in another arena and cannot change these image mappings.

PE stack/heap size fields do not change the current fixed thread-stack and memory policies. PE flags/checksums do not authenticate an application.

## Mapping and relocation semantics

Headers map read-only/NX. Code maps RX, read-only data maps R/NX, writable data maps RW/NX. These are the user-visible rights from the first mapping; no temporary RWX user mapping is created.

Physical allocation zeroes each page. Initialized raw bytes are copied, and BSS/remaining page bytes retain zero. Raw file padding is included within the mapped extent where present. Valid entry and relocation targets use their stricter initialized-range checks.

The loader copies and fixes bytes through supervisor-only physical aliases while the new address space is inactive. Kernel source spans are immutable and truthful: a future loader receiving user-controlled buffers must first own a stable snapshot. Current input bytes are embedded complete files, with no concurrent writer and IF clear.

DIR64 blocks and targets must be increasing. Duplicate/overlapping eight-byte patches, patches into headers or the relocation directory, out-of-file targets and unsupported relocation types are rejected before allocation. Patches may be unaligned or cross a page boundary, but all eight bytes must be initialized bytes in one section.

This profile accepts only internal preferred-image pointers (including the one-past-image address). Each result is computed as:

```text
actual base + (original pointer - preferred base)
```

That supports relocation in both directions without signed-delta overflow. Original pointer bytes are read from the immutable file, not an image modified by earlier fixups. A different load base requires a relocation directory; an image without relocations can load at its preferred base only.

Headers retain the preferred ImageBase field. The kernel records actual ImageBase, ImageEntry and ImageSize separately and supplies a readonly user descriptor.

Version 0.0.9 accepts up to 128 ordinary x64 runtime-function records and structurally validates their version-1 unwind info. Handler/chained/machine-frame forms remain unsupported. Relocations cannot modify those metadata ranges. This is not an exception unwinder; details are in the [native bootstrap contract](M2-Native-Module-Bootstrap.md).

## Creation and failure ownership

Malformed/unsupported files and invalid load bases return before process/page ownership changes. A live process object cannot be reused through another slot.

After validation, all new pages, page tables and handles belong to the fresh component. Failure at image, startup, stack or TLS allocation destroys that entire unpublished address space and returns NoMemory. The component slot is released, so a later attempt can succeed.

The internal statuses are Ok, InvalidImage, UnsupportedImage, TooLarge, NoMemory, Busy and BadBase. They are not a frozen public ABI.

Only after complete mapping and main-thread setup does the process become Ready. Existing IRETQ validation, timer budgeting, checked syscalls and contained user faults apply to loaded images. Exit/fault teardown frees their owned pages.

## Real fixtures and validation

The host assembles `tests/User.X64/image.asm` and links:

- `PeFixture.pe`: preferred base 0x180000000, real DIR64 fixups;
- `PeFixedFixture.pe`: preferred base at the user image window, relocations stripped.

The guest receives the complete files, including DOS/PE headers and every section. A dedicated page-aligned writable section makes an eight-byte pointer start at page offset 4092. The host checks that this condition survived linking.

Test symbol RVAs come from public symbols in linker maps; tests do not assume that a variable starts at the beginning of a section. This matters because the linker may place content after padding or its own data. Maps and both PE files are retained in CI artifacts.

The guest fixture validates relocated code/data pointers, calls through a relocated read-only pointer, reads the cross-page pointer, scans 8192 BSS bytes, writes its instance ID into private image data and prints via the granted console handle.

The seventeen VM scenarios now require 89 user groups and 33 contained user faults. Sixteen new image groups cover:

| Group | Evidence |
| --- | --- |
| ImageHeadersAndBounds | Truncation, bad signatures/offsets/sizes, count limits, overflow and invalid actual bases |
| ImageUnsupportedFeatures | Machine/format/subsystem/DLL/alignment checks and rejection of unsupported directories |
| ImageSectionsAndEntry | Raw/virtual overlap, entry into data/outside image, invalid raw ranges and RWX rejection |
| ImageRelocationValidation | Malformed/oversized blocks, unknown types, duplicate patches, self-modifying tables and invalid pointer values |
| ImageRelocatedExecution | Actual ring-3 runs at two nonpreferred bases |
| ImageRelocationDirections | A higher preferred base rebased downward; upward rebasing is also exercised |
| ImagePreferredExecution | Relocation-free image runs at its preferred base |
| ImageZeroFillAndPrivate | BSS scan and two simultaneously live images at the same VA with distinct backing pages |
| ImageGapMapping | Disjoint section gap stays absent; busy process-object reuse is rejected |
| ImageAllocationRollback | Exclusively borrowed real frames force failure at every allocation step through main-thread TLS; full counts recover |
| ImageWriteCode / ImageWriteReadOnly / ImageWriteHeaders | Actual writes fault with PF error 7 |
| ImageNxData | Execution from writable image data faults with PF error 21 |
| ImageEndBoundary / ImageGapFault | Reads beyond the image or in a gap fault with PF error 4 |

Every tested protection fault is followed by a successful new PE activation. Physical free counts and closed handle tables are checked on rejection, exit, fault and allocation rollback.

The format fields and DIR64 operation follow the [Microsoft PE reference](https://learn.microsoft.com/en-us/windows/win32/debug/pe-format). The explicit restrictions above describe WitOS's current profile rather than all legal PE files.

## Remaining NativeAOT work

This loader cannot yet accept the hosted NativeAOT DLL: DLL/import/TLS/unwind semantics remain unsupported, its measured image is larger than the profile and the physical quota is still 128 frames per component.

Native image handoff and C startup are now implemented. The [backend direction and first GC memory adapter](NativeAot-Gc-Memory-Port.md) are selected/implemented; next extend the source build, TLS, unwinding/fault and GC coordination mechanisms. Managed-module registration stays inside the real runtime. Keep unsupported features explicit; a successful native PE load is not a .NET runtime port.
