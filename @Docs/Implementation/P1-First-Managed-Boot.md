# First actual managed .NET execution in WitOS

Historical first-boot checkpoint. The later [P1 completion audit](P1-Completion-Audit.md) records completed worker/rendezvous integration and the current image.

The full NativeAOT executable now runs inside WitOS/QEMU with standard upstream CoreLib and the real source-built runtime/collector. It enters managed Main, allocates objects and a 4096-byte array, runs GC.Collect, verifies that GC.CollectionCount(0) increased and that static/local roots and array values survived, then returns 42 through upstream bootstrap and the native process-exit path.

This is the first guest managed execution checkpoint. It does not yet complete P1's separate worker attach/detach and GC rendezvous/hijack acceptance, the full M3 workload, or portable CoreCLR/JIT application compatibility.

## Executed acceptance

Four profiles pass: qemu64 at 128 and 512 MiB, Nehalem and max at 256 MiB. Each runs the actual full image at both supported bases and checks complete backing/page-table reclamation after each component. The final matrix therefore contains eight successful managed executions. A separate deliberately invalid startup descriptor must fail before output and release the entire loaded image.

The runner requires the normal kernel/isolation baseline, ordered native-startup checkpoints, the real wmain result, managed/GC acceptance markers, relocation/teardown markers, the expected QEMU exit and no panic/unexpected kernel exception/timeout. One marker or exit code alone is insufficient. The managed workload itself requires both the incremented collection count and preserved values.

Commands:

```powershell
dotnet run --project tools/WitOS.Dev --configuration Release -- runtime-boot
# Kernel-only iteration over the last hash-verified, already-built runtime image:
dotnet run --project tools/WitOS.Dev --configuration Release -- runtime-boot-run
```

runtime-boot includes source audit/build, hosted references and strict image construction. runtime-boot-run does not rebuild native runtime sources; it verifies the prepared image hash, rebuilds the kernel and executes the same matrix. CI now runs the guest matrix after its source/configuration gates. This workflow change has been validated locally; no remote CI run or publication was triggered.

## Kernel-selected resource profile

The full-runtime PE profile requires the existing runtime-unwind profile and remains unavailable through a user quota-setting API. Admission still validates immutable kernel-owned PE bytes before component allocation. Default limits remain unchanged.

| Resource | Ordinary / small runtime | Full runtime |
| --- | ---: | ---: |
| Image bytes | 256 KiB | 1 MiB |
| Unwind entries | 128 plain / 320 runtime | 4096 |
| Owned pages (including fixed mappings and private tables) | 128 | 2048 (8 MiB) |
| Dynamic reservations | 8 | 32 |
| Events | 4 | 16 |
| Handles | 16 | 32 |
| Fixed VA window | 2 MiB | 4 MiB |

Full-runtime execution retains a 3000-delivered-tick kernel budget and a 120-second host watchdog. The four-event WaitAny argument bound is unchanged. Kernel stacks remain 64 KiB. Enlarged bounded metadata/work arrays use the existing x64 stack-probing algorithm in the kernel as well as in user images.

Memory query and pressure accounting use the selected component limits. Reserve still consumes no backing; failed commit rolls back only its additions; no-access pages remain owned and reset preserves commitment. Full-profile tests fill event and handle quotas, reject one additional creation without publication, release them, force a large failed commit with exact backing/table recovery, and exercise 129-page no-access commitment/reset/release. Existing default-profile memory, pressure, handle and event regressions remain enabled.

## Upstream GC bookkeeping correction

After initial admission succeeded, the actual collector exposed two issues that native probes could not reveal. First, heap initialization exhausted the previous four-event budget; the kernel reported NO_MEMORY at four live events. Separate full-runtime limits and a bounded sixteen-slot native GC event registry removed that resource constraint without changing ordinary component limits.

Next, a write in gc_heap::make_heap_segment faulted in an uncommitted tail page of the segment-info table. The original Windows NativeAotBoot reference also exits with access violation (0xC0000005) when run with DOTNET_GCHeapHardLimit=400000 and concurrent GC disabled. This reproduces the small-heap profile independently of WitOS.

The pinned get_card_table_element_layout skips alignment when a bookkeeping element has zero size, and also skips alignment of the total extent. With an empty background mark array, the subsequent commit clamp rounds its unaligned start down and leaves part of the segment table uncommitted. The WitOS overlay now preserves the mark-array page boundary even when empty and rounds the final bookkeeping extent. The complete upstream collector remains compiled; no allocator, collector or managed semantic stub replaces it.

Canonical download bytes for gc.cpp, gcwks.cpp, GCHelpers.cpp and FinalizerHelpers.cpp are pinned; source audit now covers 75 files. The original Windows reference build remains unmodified. A mandatory hosted regression compiles the exact original and corrected layout methods against 128 size layouts: the original exposes 64 uncovered tails; the corrected layout covers every segment entry and maintains page boundaries/non-overlap. Actual guest collection under the same 4 MiB hard limit supplies integration evidence.

Failure-only checkpoints in startup/GC/finalizer helpers and the previously empty GCToEE LogErrorToHost callback preserve upstream returns and route errors through the existing bounded console transport. Each full adapted source is recorded in provenance. The successful runtime still uses the original required profile: workstation GC, concurrent GC disabled, large pages disabled, baseline x64/x87/SSE and standard CoreLib.

## Reproducible evidence

Final runtime image: 999,424 mapped bytes, 2,800 unwind records and 640 TLS bytes. SHA-256: 4d5786e112d7df61d6df33aa75010b354eb8e8467230a6d058a7edd070f5f823. The image has no OS imports or PE TLS callbacks. Native archive: 141 members; configuration probe: 51 objects. Minimal/broad link remains 0/6, with only intentionally omitted transport/TLS in the broad audit.

- artifacts/p1-full-runtime-matrix-final.log: four profiles, both bases, success and failure teardown.
- artifacts/x64/runtime-boot/runtime-input.json: exact embedded image hash and build evidence.
- artifacts/x64/runtime-boot/acceptance.json: successful managed/collector execution, kernel disk hash and serial-log hashes; stale success is removed before a new matrix.
- artifacts/runtime-gc-layout-reference/reference.log and reference.json: original/corrected method regression and input hashes.
- artifacts/p1-runtime-profile-test.log: all 20 kernel integration scenarios pass.
- artifacts/p1-runtime-profile-config.log: four prior native runtime profiles pass, each with 284 User groups / 66 expected contained faults.
- artifacts/p1-runtime-profile-probe.log: hosted managed regression passes.

The source-build/readiness reports remain build-only evidence and can still say guest execution was not performed by that command. The separate hash-linked runtime-boot acceptance manifest is the authority for guest execution.

## Remaining P1 acceptance

Follow-up: [P1-Worker-Lifecycle](P1-Worker-Lifecycle.md) closes P1.8.f with 72 real normal/detached worker lifecycles, populated allocation-context cleanup and rollback/reuse. The subsequent [runtime rendezvous](P1-Runtime-Rendezvous.md) closes P1.8.g/h. The first-boot limitations below describe the original checkpoint.

The successful Main proves real runtime/GC/module startup, attachment sufficient for main/finalizer startup, local/static root survival and process-level teardown. It does not by itself prove normal worker ThreadStore detach with populated allocation contexts, detached worker reuse, concurrent register/stack roots or successful forced hijack/redirection. Keep P1.8.f/g/h open and add those targeted integration workloads using the complete runtime, not sliced or dummy cleanup implementations. Managed exceptions/finalization/threads as a combined application suite remain P5; CoreCLR/JIT remains P6.
