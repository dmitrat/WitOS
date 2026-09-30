# P3.6: real GC allocation and initialization failure

Status: **P3.6 complete.** All mandatory final gates passed. artifacts/p3-memory-validation.json binds the final image/run to 32 managed OOM recoveries, eight actual GC initialization failures and all 20 kernel scenarios.

## Managed allocation failure and recovery

The normal full-runtime workload invokes MemoryFailureProbe through the existing private unmanaged/managed callback. The actual upstream heap hard limit remains 4 MiB. Three attempts to allocate valid 16 MiB byte arrays must throw standard CoreLib OutOfMemoryException. After each exception, a small allocation and real GC succeed with array sentinels and a retained live object intact. No invalid array length or explicit throw substitutes for a failed allocation.

A separate case reserves and commits a component-owned no-access pressure region using the real kernel memory APIs. The successful pressure commit leaves a measured eight-page allowance for private tables and control work. The fixture requires the previously committed dynamic memory plus that allowance to be smaller than the requested 2 MiB object. A managed callback then attempts the real allocation, catches OOM and preserves its live root. After releasing the pressure reservation, allocating the same 2 MiB object and collecting it succeeds.

Kernel acceptance requires at least one real failed MEMORY_COMMIT during the successful full workload. Every NO_MEMORY commit in the syscall path now checks that settled component ownership and physical free-page counts exactly match the pre-call snapshot, while IF is disabled. The fixture also checks public memory accounting bounds before/after the managed operations. Complete component teardown restores the original physical-page count.

There are four managed failures/recoveries per successful image, or 32 across the four RAM/CPU profiles and two bases. The tested workload reported two real failed commits per successful image.

## GC initialization failure

The same actual PE is loaded as a separate private boot test component at each base. After normal image admission, the kernel test constrains its existing owned-page quota to image ownership plus 24 pages. This is a real allocator limit; no GC method, native allocator result or OS return code is stubbed.

Upstream startup reaches g_pGCHeap->Initialize, a real kernel commit fails, and the unchanged failure return propagates through InitializeGC/InitDLL/wmain. Acceptance requires the specific upstream checkpoint, wmain return -1, a positive kernel commit-failure count and complete event/handle/backing teardown. Both addresses are tested before normal successful components, which proves subsequent reuse after failed initialization.

## Automated evidence

RuntimeBootProtocol checks each initialization-failure block separately and requires OOM/recovery plus real commit-failure evidence inside each normal image block. Missing checkpoints, zero failure counts and removed recovery markers are rejected by host mutation tests. The managed/CoreLib and native runtime implementations remain real and strictly linked.

- artifacts/p3-memory-pressure.log: expanded real pressure/recovery matrix.
- artifacts/p3-memory-protocol-boot.log: strict per-base acceptance after parser/negative-test updates.
- artifacts/p3-memory-build.log and p3-memory-host-tests.log: Release and host/PE regressions.
- artifacts/p3-memory-kernel-test.log and p3-memory-final-*.log: mandatory final gates.
- Matching runtime-boot runId/input/acceptance/serial snapshots provide the executed image identity.

These bounded cases cover the selected workstation/non-concurrent profile. They do not claim every possible allocation-failure location, long-running stress, server/concurrent GC or completion of the remaining abrupt-exit policy in P3.8.
