# P6.4 native library foundation

Date: 2026-09-30. Initial foundation: user ABI 41; current shutdown interface: user ABI 46, boot ABI 4. This is a native guest loader and actual corehost PAL leaf implementation. It is not guest CoreCLR startup and does not complete P6.4.

## Ownership and publication

`WIT_CALL_LIBRARY` loads immutable package files into a component-owned sparse reservation. The kernel validates the DLL profile before allocation, copies headers/declared sections, applies validated DIR64 fixups, and publishes headers/metadata read-only, code RX and data RW/NX. Image gaps remain uncommitted. Publication of a generation-bearing handle occurs only after final protections and instruction publication. Allocation or handle failure releases the entire unpublished image.

Four module records share existing component handles, backing and reservation quotas. Repeated loads of the same package entry share a token and increment references. Final unload releases pages and invalidates the generation. Generic CLOSE is rejected; the library operation owns reference accounting. Ordinary memory commit/decommit/reset/protect/release and code alias/sparse-map operations cannot mutate or alias the protected image reservation. Writable data exports retain their declared permissions.

The UP bootstrap syscall path has interrupts disabled. Its large PE validation scratch record is static to avoid exceeding the fixed kernel stack when nested with allocation rollback arrays. This is not an SMP/reentrant loader. Component teardown still owns all module backing, including abrupt exits.

## Exports and current profile

Common-kernel parsing supports bounded, case-sensitive named exports, aliases, ordinal-only exports, EAT holes and readable data/BSS exports. Tables and names must lie inside initialized read-only export metadata. Counts, ordinal ranges, ordered names and target section bounds are checked. Relocations cannot modify export/unwind metadata. Lookup uses immutable package bytes, never caller-editable mapped data.

At the ABI 41 foundation, imports, delay imports, forwarders and static TLS were explicitly unsupported. ABI 43 adds unbound sibling-library imports as described below; delay imports, forwarders and static TLS remain unsupported. The initial loader also rejects any entrypoint. The current image quota is 256 KiB, the file quota 1 MiB and export capacity 512. These are prototype limits; real CoreCLR images require measured expansion and dependency/lifecycle support.

The kernel returns image/unwind ranges but does not register runtime unwind tables or execute DLL initializers. Callers must quiesce code/readers and remove any user-space unwind registrations before final unload. Automatic DLL initialization, dependency graphs, TLS notifications and module-unwind lifetime remain open.

## Actual hosting PAL

`host_library.witos.cpp` implements the hash-verified upstream signatures of `pal::load_library`, `pal::get_symbol` and `pal::unload_library`, using native path/CWD resolution and the kernel module handles. Successful operations preserve native last-error. Failed loads return a null output, missing symbols report `ERROR_PROC_NOT_FOUND`, and invalid void unload fails fast rather than hiding lost ownership. WitOS uses explicit references/release, as the Unix corehost PAL does; it does not adopt the Windows PAL's permanent module pinning policy.

The guest probe uses legal constexpr C++ standard-library strings and calls an actual exported function. This demonstrates the real leaf signatures without substituting a throwing allocator, C++ exception runtime or forged string layout. Module discovery is implemented below. Full C++ host dependencies and startup/binding integration remain unresolved.

## Validation checkpoint

- Release build: no warnings/errors.
- Both 128/512 MiB storage boots: actual named/ordinal/alias/data exports, relocations, reference/reload/stale-handle behavior, OOM rollback, RX write fault, generic memory and alias denial, real PAL load/call/unload, and exact teardown accounting passed.
- Hosted export parser: 3088 trailing-guard cases, Windows comparisons of actual named/ordinal/alias/data targets, ordinal-only/no-export controls, explicit imports/TLS/delay-import rejection and every file truncation passed.
- Actual hosted library PAL with real C++ strings and Windows DLL execution passed, including reference/error contracts and invalid UTF-16/oversized symbol cases.
- All 20 kernel scenarios, executable-memory 128/512 profiles, runtime-audit/probe/target, full source build/configuration and all four managed boot profiles passed.
- 500 bounded libFuzzer/ASan runs passed with both runtime-full and library profiles, a real DLL seed and validated export lookups. This does not establish exhaustive parser coverage.
- Full host suite initially reported 39/41: two process-completion failures (`artifacts/p6-library-host.log`). An isolated retry exposed a separate test-harness link dependency, now corrected with a dedicated HOST_LIBRARY_PAL_TEST define. Final suite: 40/41; only Q1.ControlIgnoresCancellation failed with process wait=258, exit=-1 and empty job. The root cause remains unresolved; strict process signaling, cleanup deadlines and acceptance rules are unchanged. The final failed log is `artifacts/p6-library-host-final.log`.

`artifacts/p6-library-checkpoint.json` binds exact source/gate hashes and authoritative PAL/managed acceptance records. It deliberately records allChecksPassed=false and P6.4 complete=false. No changes have been published.

Implementation follows the PE export layout documented by [Microsoft](https://learn.microsoft.com/en-us/windows/win32/debug/pe-format#the-edata-section-image-only). Generated images and logs remain ignored under `artifacts/`.


## Module discovery (ABI 42, verified slice)

FIND returns an owned reference to an already loaded component module; it does not map a file or run code. Canonical full paths match exactly. Explicit basename lookup is case-sensitive and rejects ambiguous matches before incrementing any reference. PATH copies the complete 1040-byte versioned record only after whole-buffer validation, returning the immutable package key and zero padding. Closed generations remain invalid. Kernel records retain the package name offset/length from the validated immutable entry; caller-writable image bytes do not define module identity or origin.

The native adapter resolves relative/full paths through the existing CWD policy and rejects directory-qualified lookups. Actual `pal::get_loaded_library` and `pal::get_module_path` use real C++ strings. Lookup verifies the requested symbol, constructs the returned path transactionally, and transfers ownership only on success. A scoped reference releases on lookup failure or a real C++ allocation exception. The hosted test injects one bad_alloc through a test-only replacement allocator that otherwise delegates to the Windows CRT; this object is never linked into a guest or production archive. Full C++ heap/EH closure remains required for guest execution of these two higher-level methods; native FIND/PATH are independently tested in the guest.

The package adds test/lib.dll alongside native/lib.dll to exercise actual duplicate basenames with distinct module identities. Hosted normal, missing-symbol, stale-handle, inclusive 4096-character input and bad_alloc cleanup checks passed. Guest 128/512 MiB checks passed, including exact reference counts, two loaded modules with the same basename, failed whole-buffer writes preserving their prefix, stale generations and complete teardown. The first guest run exposed a truncated counted path in the test; it now uses sizeof(literal)-1 and reports the exact failed status.

Release, all 20 kernel scenarios, audit/probe/target, full source/configuration, executable-memory and all four managed boot profiles passed. Independent verification checked all 181 package payloads and the final FAT image against their unchanged sources. The initial host run failed ConcurrentPipesStayIsolated with empty JSON input; the test now saves all four results and checks timeout, exit code 7 and stderr before parsing. The fresh suite passed 42/42 groups and 555 PE inputs. This does not claim the earlier intermittent native process-completion problem is fixed. Both failed and successful evidence are retained. `artifacts/p6-discovery-checkpoint.json` binds the final source/gate hashes and authoritative PAL/managed acceptance records; P6.4 remains open.


## Import-parser preparation (not yet loader support)

`pe_imports.c` independently validates unbound AMD64 import descriptors against an already validated immutable section map. It accepts named and ordinal imports, including absent OriginalFirstThunk with a valid IAT source, and records bounded module/symbol/IAT metadata. It requires initialized read-only metadata and IAT, identical unbound lookup/IAT entries, disjoint IAT destinations, complete terminators and bounded ASCII names. It rejects bound descriptor state and invalid references. The resulting ranges identify import metadata that future relocation handling must protect.

The limits are 16 DLL descriptors and 512 total symbols; positive controls exercise both exact limits. The 2582-case readonly trailing-guard unit corpus and its ASan run passed. Reproduce with `--pe-imports` / `--pe-imports-asan` through WitOS.Dev.Tests. These tests use a synthetic validated section map, not a claimed runnable PE image. Production PE admission still rejects imports. This preparatory checkpoint preceded the full-image and graph implementation below; merely parsing descriptors does not load a dependency.


## Unbound dependency loading (ABI 43, verified slice)

The library-only PE profile now validates import descriptors and optional IAT-directory coverage in the complete immutable image. Existing ordinary/main-image profiles retain their rejection policy. IAT entries must be initialized read-only section data; fixups cannot mutate import/export/unwind metadata. Bound/delay imports, forwarded exports, static TLS and nonzero DLL entrypoints remain explicit failures.

The UP/IF-disabled loader discovers the complete bounded graph before allocating. Dependencies resolve by exact filename in the importing DLL's package directory; there is no Windows DLL fallback or implicit cross-directory search. Discovery reserves an identity before traversing edges, so cycles terminate. All new images are copied/relocated RW/NX, all named/ordinal/data references bind against validated immutable exports, and final RX/RO/RW permissions precede handle/graph publication. An allocation, missing symbol or partial handle-grant failure releases every new image/handle without changing existing modules or their external reference counts.

The four-module quota includes dependencies. Each module tracks external references separately from dependency edges. Final UNLOAD removes one external reference; reachability from all remaining external roots determines which modules survive. This preserves shared dependencies, reaps unreachable cycles, and denies attempts to consume a purely implicit reference. As before, callers must quiesce executing code/readers and unregister user-space unwind registrations before final graph release. Automatic DLL attach/detach, dynamic/static TLS and unwind registration are not supplied by this native graph layer.

Guest 128/512 MiB checks passed for actual linker-generated imports by name/ordinal/data, calls through bound IAT, cycles, retained/shared dependencies, missing sibling/symbol rollback, four-module quota, each multi-image page-allocation boundary, partial handle-grant rollback and an actual readonly-IAT write fault. The package contains 187 immutable files. The main-image startup path does not accept the DLL-only profile.

The hosted suite passed 42/42 groups and the existing 555 PE corpus. The import harness now additionally checks 8206 full-PE cases (two actual DLLs, all truncations and malformed import/IAT/bound/delay records), compares actual calls/cycles with Windows, and repeats under ASan. The earlier 2582 section-map unit cases remain. The fuzz lane now includes actual imported/cyclic DLL seeds and the new validation profile. All kernel/runtime/source/managed gates and 500 libFuzzer/ASan runs passed. The source hashes and authoritative acceptances are bound in artifacts/p6-import-binding-checkpoint.json. P6.4 and guest CoreCLR startup remain open.


## Automatic native module unwind (ABI 44, verified slice)

A separate generation-bearing reader handle holds a module and its dependency graph independently of ordinary external load references. ACQUIRE_READER selects an executable module address and atomically copies its kernel-owned WitLibraryInfo; QUERY_READER revalidates the handle/module generation and repeats whole-buffer copy-out. RELEASE_READER drops that distinct root and collects an otherwise unreachable graph. Generic CLOSE and ordinary UNLOAD cannot consume reader ownership. The 16-reader pool shares the normal handle quota and does not allocate backing pages.

The CoreCLR-facing lookup falls back from explicit dynamic function tables to native modules without requiring manual registration of their pdata. It takes a kernel reader, selects the actual sorted function record and passes the leased view through the existing checked dynamic unwinder. Subsequent chained lookups query kernel identity again instead of trusting a writable cached base/length. No full-image cache is attached to arbitrary modules. Missing metadata is distinct from quota/contract failure: resource failure terminates the component through the real unwind fail-fast path, rather than pretending the frame is a leaf.

Both 128/512 MiB mapper boots passed a real compiled C DLL frame unwind, canonical-record/base checks, explicit lease survival after ordinary UNLOAD, and an exhaustion component that must fail fast. The frame probe supplies its real call-site PC/RSP for an RSP-based frame; it does not implement or claim a complete RtlCaptureContext API. Both storage profiles passed reader quota/type/stale-handle tests, rejection of buffers crossing an unmapped boundary without modifying their prefix, implicit dependency retention and exact teardown accounting. The mapper boot now carries only its native fixture package, not the managed framework.

The lease protects metadata while it is being inspected; callers still must quiesce active code and retain any borrowed handler/function pointers for their use. DLL initialization/TLS and full C++ hosting closure remain subsequent work. Native module unwinding is not managed CoreCLR execution. Release, 42/42 host groups, function-table reference, memory/storage profiles, all 20 kernel scenarios and audit/probe/target/source/config/managed gates passed. artifacts/p6-module-unwind-checkpoint.json binds the final source/gate hashes and authoritative acceptances.


### Suspended-thread DLL frame

The follow-up probe runs the same real compiled DLL frame on a worker and publishes its call-site PC/SP and return tuple with release/acquire atomics. Main suspends the kernel-identified worker, drops the normal load reference while retaining a separate module reader, and unwinds under an owning foreign stack scope. Resume is rejected while that scope is active. After scope closure the worker resumes, returns through the still-mapped DLL, exits and is joined; only then does the final reader release collect the image graph. Forged cached module identity remains rejected. Both 128/512 MiB profiles passed. This extends the probe; shared production code and ABI are unchanged. The required kernel/audit/probe/target follow-up gates are running, tracked in artifacts/p6-module-foreign-checkpoint.json.


## Process entrypoint lifecycle (ABI 45)

The opt-in [DLL process lifecycle](P6-DLL-Lifecycle.md) now executes real entrypoints in user space through protected plans, with attach-once, reverse dependency detach, failed attach rollback and reader-delayed cleanup. Its current admission boundary is explicitly single-live-thread for entry-bearing DLLs. Thread notifications/TLS and normal process-shutdown integration remain open. The full ABI45 matrix passed. ABI46 now integrates normal DLL shutdown into real CRT cleanup after TLS/atexit/runtime notification; guest and Windows process-exit witnesses passed, while its broad regression gates are running.
