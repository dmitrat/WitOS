# Native GC affinity parsing

WitOS 0.0.44 implements `GCToOSInterface::ParseGCHeapAffinitizeRangesEntry` by calling the real upstream `ParseIndexOrRange`. It adopts the upstream Unix-style flat CPU-index syntax; Windows processor-group prefixes do not apply to the WitOS topology. ABI v18 and the single-online-CPU restriction remain unchanged.

## Semantics and limits

This is configuration parsing, not affinity application or online-CPU validation. The full upstream range parser accepts indices within its `MAX_SUPPORTED_CPUS` representation (1024 in this profile), even if those CPUs do not exist. Actual GC startup still needs to validate requested sets against supported topology before using them. `CanEnableGCCPUGroups` remains false. Nothing here implements processor groups, server GC, scheduler affinity or SMP.

The platform adapter is a direct call to the real parser with native caller-owned pointers and NUL-terminated input. No synthetic success, alternate CoreLib, allocation or Windows service is involved. The upstream parser uses the existing Windows-codegen 32-bit `strtoul` contract. Its cursor updates, sign/whitespace acceptance and overflow errno behavior are preserved.

The full `ParseGCHeapAffinitizeRanges` body also remains unchanged, including behavior callers must account for:

- A single entry parser accepts a numeric prefix; the full parser rejects unsupported suffixes, such as `0:0` or trailing spaces.
- Reversed ranges and indices outside the 1024-bit representation fail. Duplicate indices are idempotent.
- Failure after a valid prefix can leave a partial set and mask. Callers must discard failed results, not infer rollback.
- An empty string succeeds with an empty set in the pinned implementation.
- A pre-existing nonzero mask bypasses parsing the range string when processor groups are disabled.
- The full set retains indices above 63; the accompanying pointer-sized mask folds their bits modulo 64. It is not a standalone representation of the full set.

Native last-error is preserved. Native errno remains ordinary compiler TLS: successful conversions retain it and overflow sets ERANGE. Parsing before C++ TLS constructors is supported when static compiler TLS already exists; operation without compiler TLS is not claimed.

## Source and guest evidence

The complete upstream runtime continues compiling its original `gcconfig.cpp`. Windows-reference sources are unchanged. The WitOS adapter object is verified inside the full source archive, copied and hash-checked before linking into the guest platform fixture.

For the native probe, both parser bodies are extracted without corrections from hash-verified `gcconfig.cpp`, with its original header prefix and MIT notice. Their generated source hash and input provenance are recorded alongside the existing GCConfig prefix slice. Full collector dependencies remain in the runtime archive, outside this configuration probe.

Modes 39/40 exercise numeric entries, ranges, sign/whitespace, 32-bit overflow, unchanged outputs/cursor on entry failure, complete and partial sets, bitset boundaries, invalid group syntax, empty input and mask precedence. A readonly string ends at the last byte before an uncommitted page. Three native workers repeat parsing across yields with independent errno/last-error sentinels. The main path also executes before native TLS constructors, and normal cases run at both image bases.

Kernel checks require the expected thread create/join/reap counts, exit code, no console writes, unchanged owned pages, no live handles/events and complete page recovery. These are native configuration checks, not collector or managed runtime execution.

## Validation

All four runtime-config profiles passed 212 user groups and 54 expected contained faults each: qemu64 at 128/512 MiB and Nehalem/max at 256 MiB. The platform fixture has 17,408 file bytes and 59 plain unwind records, with no OS/CRT imports. The configuration archive has 20 exact source objects, the full workstation archive 89 members, and minipal 11.

Source audit verified all 66 pinned files. Hosted NativeAOT probe, native C-host target/reference and minimal executable checks passed. The strict broad link boundary drops from 71 to 70 unresolved symbols; minimal startup drops from 65 to 64. Four GC OS requirements remain in the broad list. Guest managed execution remains pending.

All 19 ordinary boot scenarios passed, retaining 178 required user groups and 51 expected contained faults in successful boots. Release solution builds completed with zero warnings and errors. Generated logs, images and source evidence remain under ignored artifacts/.
