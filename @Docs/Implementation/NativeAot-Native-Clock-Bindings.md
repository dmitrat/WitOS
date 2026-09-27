# Native CoreLib clock bindings

**Status:** WitOS 0.0.42; experimental ABI v18.

QueryPerformanceCounter, QueryPerformanceFrequency and GetTickCount64 now have real WitOS implementations for the Windows-codegen NativeAOT/CoreLib boundary. They do not import Windows implementations or supply a general Windows API personality.

## Kernel contract

Call 32, MONOTONIC_QUERY, accepts a destination address, exact size 8 and a selector: counter (0) or frequency in Hz (1). It returns status plus 8 bytes copied on success, zero on failure. Invalid size is rejected before sampling; unsupported selectors are rejected explicitly. The existing register-return clock calls remain unchanged.

Sampling, complete destination validation and copy-out run with interrupts disabled under the existing single-CPU/serialized-mapping contract. The common user-copy routine validates both pages before any write. Unaligned/cross-page writable destinations are supported; readonly, no-access, uncommitted, out-of-range and overflowing destinations fail without a partial prefix write. The operation allocates no memory and needs no compiler TLS or PAL initialization.

The result is from the existing HPET monotonic domain. It is distinct from delivered PIT ticks and from UTC. The ABI version bump is intentional; the experimental handoff is not a frozen SDK.

## Native binding contract

Small x64 facades export the actual three native names and readonly __imp slots. The portable native adapter uses actual upstream PAL/Windows SDK declarations. QPC/QPF return nonzero on successful complete output. Kernel failure maps to native last-error; successful calls preserve prior error. Unexpected successful copy counts fail fast instead of being accepted. errno is independent.

GetTickCount64 returns monotonic milliseconds via the implemented minipal clock, using its overflow-safe conversion. It neither supplies calendar time nor resets the clock. These functions work before compiler TLS/environment/PalInit and during native worker execution.

Both adapter and facade are included in the full source-built WitOS archive. Their exact object bytes are verified against the archive, copied into the guest artifact directory and hash-checked before linking. Direct-entry thunks in the test fixture exercise the public symbols, while SDK calls exercise their import bindings. No test-specific implementation provides the clock values.

## Guest validation

The compact CPU/platform fixture now adds two groups:

- NativeClockBindings brackets direct/import performance counters and millisecond values with real minipal samples, checks frequency equality, then repeats calls across three workers with independent errno/last-error and yields. Both image bases execute.
- NativeClockAtomicCopy performs the same early checks with no compiler TLS. It rejects invalid sizes/selectors/null/wrapped pointers and an output pointing at a readonly import slot. A destination split across two pages is rejected while its second page is uncommitted; all writable prefix bytes stay poisoned. After both pages are committed the same unaligned destination succeeds. Making the second page readonly or no-access again rejects the entire output without modifying the first page.

Every case requires the expected thread counts, original owned-page accounting, no leaked handles/events and full physical-page recovery. No additional hardware faults are expected from invalid output pointers.

## Results

All four runtime-config profiles passed 207 groups and 54 expected contained faults each: qemu64 at 128/512 MiB, Nehalem and AVX-capable max at 256 MiB. The separate platform fixture remains import-free. Source audit (66 files), hosted probe, source/native reference runs and minimal startup checks passed. All 19 ordinary QEMU scenarios passed, retaining 178 required user groups and 51 contained faults in successful boots. Release solution builds completed without warnings or errors.

The source archive contains 87 members and the configuration archive seventeen source objects. Broad unresolved dependencies drop from 77 to 74; minimal startup drops from 71 to 68. Full guest runtime bootstrap, collector execution, managed threads/exceptions and CoreCLR/JIT remain pending.
