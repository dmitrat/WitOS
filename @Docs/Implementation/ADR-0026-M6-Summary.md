# ADR 0026: The M6 summary: the profile by fact, a static libwitos, the upstream submission after T4

**Status:** Accepted 2026-10-15 with plan step N4.5, applying the defaults PLAN.md's phase V recorded.
**Date:** 2026-10-14.
**Scope:** What phase N's last step fixes about the documents: the feature profile of RFC 0015 §8, the form of
`libwitos` in RFC 0011 §9.1 and §10.2, when the `TargetOS=witos` patch set is proposed upstream (RFC 0015 §4), and
the public compatibility matrix of Strategy §145 through which WitOS claims Gate B (Strategy §61).

## Context

Phase R ported upstream .NET in its Unix form (`TargetOS=witos`) and phase N proved standard .NET in the guest on
both ISAs: the compatibility suite (N1), representative NuGet libraries and their own test suites (N2), .NET 8 and
.NET 10 side by side (N3), and the application environment (N4: time zones, `/tmp`, file modes and times, processes
with pipes between them, the terminal of RFC 0020, and the applications published on Windows run as those bytes).

Three documents still describe the plan rather than the fact:

- RFC 0015 §8 gives a "feature profile by phase": a first guest profile (invariant globalization, no processes, a
  write-only console) and a "later" column. Every "later" cell of that table is now a fact or an explicit absence.
- RFC 0011 §9.1 and §10.2 name `libwitos.so` and `libwitos.so.1` with symbol versions `WITOS_1.x`. The library ships
  static (`libwitos.a`, linked into the services through `DirectPInvoke`), outside ABI-2 1.0 (N3.3), and no consumer
  outside the system layer exists.
- RFC 0015 §4 leaves open when to propose the patch set upstream. The patch set is measured (R8: 25 and 28 files on
  `release/8.0` and `release/10.0`, rebased by `runtime-rebase`), and the layer-2 triple is still `*-linux-musl`, so
  every upstream site that means the Linux kernel carries `!defined(TARGET_WITOS)`.

Strategy §145 asks for a public compatibility matrix from M6 on, and §61 for the claim "WitOS runs .NET" once M6 is
done.

## Decision

1. **The feature profile is stated by fact.** RFC 0015 §8 replaces its table of phases with the profile as of N4:
   each area with what the guest proves on both ISAs and the step that proved it; an absent area fails explicitly and
   appears in the compatibility suite's recorded differences, as before. The public form of the profile is
   [Compatibility-Matrix.md](Compatibility-Matrix.md), which the `compatibility-matrix` command renders from the suite's
   own list of checks, `expectations.json` and `build/compatibility-matrix.json`, and which a host test keeps current, so
   that the matrix never says more than the gates prove.
2. **`libwitos` stays static until it has a consumer outside the system layer.** RFC 0011 §9.1 names the WitOS
   capability API `libwitos`, a static library of the sysroot today, and §10.2 keeps the rule for the day it ships
   shared: `libwitos.so.1` with its major in the soname and symbol versions `WITOS_1.x`. The first consumer outside the
   system layer (an application of phase N's later steps, or a service another party builds) makes it shared, with an
   ADR that records the version it starts at; until then ABI-2 1.0 is the libc, the C++ runtime and the loader
   (`build/abi2.json`).
3. **The upstream proposal of `TargetOS=witos` is filed together with T4**, the own triple `*-unknown-witos` in LLVM.
   Filed before T4, the target would arrive defining `__linux__` and excluding the Linux kernel's sites one by one,
   which upstream would rightly read as a Linux variant rather than a platform. Until then the patch set is maintained
   per release branch (RFC 0015 §7, N3.1) and carried across tags by `runtime-rebase` (R8.1); the proposal's content is
   the patch set itself, the RID graph entries, the measured `tryrun.cmake` answers and the compatibility matrix. The
   trigger for T4 stays what the plan says: when the `*-linux-musl` triple begins to obstruct.
4. **Gate B is claimed.** With N4 complete on both ISAs, WitOS claims "WitOS runs .NET" (Strategy §61) in the README,
   with the matrix as the statement of what that means and the recorded differences as its limits.

## Consequences

- The vision documents change by this ADR alone: RFC 0015 §4 (the contribution paragraph) and §8 (the profile),
  RFC 0011 §9.1 (the row of the capability API) and §10.2 (item 2). Section numbers stay.
- `build/compatibility-matrix.json` is the one place that states a capability beyond the suite; a status there is
  `Supported`, `Partial`, `Absent` or `Planned`, with the step and the fact behind it, and the host test refuses any
  other.
- A later step that changes a status (M7's networking, phase P's server GC) edits the JSON, re-renders the matrix and
  commits both, as it edits `expectations.json` and `Compatibility-Differences.md` together today.

## Alternatives considered

- **Ship `libwitos.so.1` now.** No consumer outside the system layer would bind to it, and symbol versions without a
  consumer are a promise nobody tests. Rejected until the consumer exists.
- **Propose the patch set upstream now.** The set is small and measured, but the triple defines `__linux__`; the
  review would turn on that, not on the platform. Rejected until T4.
- **Leave RFC 0015 §8 as the plan.** The table would keep stating as "later" what the guest proves today, and the
  README would have to say more than the RFC. Rejected: the RFC states the fact, the plan keeps the history.

## Related

- [ADR 0024](ADR-0024-Three-Layers-and-Unix-Form-Runtime.md): the three layers and the Unix form.
- [N1-Compatibility-Suite.md](N1-Compatibility-Suite.md): the suite, its gates and the applications published on Windows (N4.4).
- [Compatibility-Differences.md](Compatibility-Differences.md): each difference with the check that verified it.
- [Compatibility-Matrix.md](Compatibility-Matrix.md): the rendered matrix.
