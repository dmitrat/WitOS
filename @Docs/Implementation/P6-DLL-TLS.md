# P6.4 multi-module DLL TLS work

Date: 2026-10-02, closed 2026-10-03. ABI48 static DLL TLS is implemented; the full regression matrix passed in CI and again on the consolidated Q2 code. Dynamic DLL TLS callbacks/constructors and admission with already-live peers remain open.

## Existing boundary to extend

The current compiler TLS page stores the GS table pointer at offset 0x58, slot vector at 0x80 and main-module data at 0x100. Slot zero belongs to the main image. The 4-thread fixed layout has a 0x20000 stride; any extra TLS mapping must be checked against stack guards, the peer page and fixed image windows before being allocated. Kernel records must own every block and its generation; caller-writable vector/index bytes cannot become ownership authority.

The next backend must validate real PE TLS metadata/fixups before allocation, capture relocated template bytes before publication, reserve distinct module slots, and supply independent initialized/zero data to each thread. It must roll back all block/header/page-table additions on failure, clear active translations before reuse, preserve the main TLS/raw FS contract and reclaim module blocks on thread/module teardown. Loading into already-live threads and dynamic constructors/destructors require explicit integration; they are not implied by static copying.

## Genuine compiler fixture and Windows evidence

`tests/User.X64/library_tls.c` uses actual __declspec(thread) integer, pointer and zero data, and exports getters/setters/address/index. Its pointer initializer refers to an ordinary image variable, requiring real template relocation. `tests/WitOS.Dev.Tests/LibraryTls.c` checks main/worker isolation, distinct addresses, zero bytes and the relocated pointer's value.

A standard Windows CRT-linked control passed. A custom metadata image with no imports loaded and entered, but retained index zero and returned wrong TLS values; adding an entrypoint or a callback table alone did not correct that result. In this environment, the same custom image passed after adding one real import. Replacing the Windows-only import with the existing WitLibraryFixture LibraryAdd dependency also passed, including the native subsystem profile. This is observed reference-loader behavior, not an inferred general Windows rule or a requirement to change WitOS semantics.

The successful custom reference links no Windows CRT/OS implementation into its DLL; its dependency is the project's own native fixture. Evidence is under artifacts/dll-tls-reference/, notably reference-standard.log, reference-directory.log, reference-import.log and reference-native-import.log. The failing controls are retained. At this reference-only stage, these fixtures were not yet integrated into guest builds; the subsequent ABI48 backend is described below.


The successful own-dependency fixture is now reproducible through `--dll-tls-reference`, using NativeTlsLibraryImage and a machine-readable source/DLL/executable hash report. The command and full host-suite group passed. At that reference-only stage, guest admission still rejected DLL TLS; ABI48 adds the separate static-only profile below. Windows comparison is preparation, not guest support.


## Static guest backend (ABI48)

The dedicated library TLS profile validates the existing PE TLS directory, real VA fixups and bounded initialized/zero template, retaining explicit refusal of nonempty callbacks. Main slot zero is preserved; each library slot uses a separate nonzero compiler index. The kernel captures the relocated template before publication and creates independent per-thread backing. Fixed layout assertions keep the vector before main TLS data and the new pages away from peer/stack/image regions.

Kernel records own every module block and header. Loading into a C component without main TLS creates a compiler header as needed; unloading its last DLL TLS block removes that header and restores GS state. A main TLS header/data block remains alive. Caller-writable vectors and module index bytes do not define ownership. Thread-info v3 separates existing CompilerTls main-readiness semantics from the actual CompilerTlsHeader, avoiding accidental errno/runtime access through a DLL-only header.

Module load/abort/unload and thread creation/reaping include the extra pages in rollback. A new failure sweep exposed delayed OOM after successful thread creation because notification plans were still allocated lazily. Entry-bearing workers now reserve a real readonly notification page and separate attach/detach handles as part of atomic creation. The handles retain distinct generations; the page stays thread-owned until reaping.

Targeted 128/512 MiB guest runs passed two-module isolation, real template-pointer relocation, zero data, fresh worker/reused-slot state, main-slot-zero coexistence and cleanup after corrupting writable compiler-vector hints. Module-load OOM rollback passed. The worker sweep now loads DLLs first, consumes real quota to leave exactly 0..19 pages, requires rollback at every boundary, and succeeds at 20 pages including notification resources. No limits were raised and failed creation preserves the native-ID output.

The new full-image TLS profile passed 4613 guarded admission/truncation/metadata cases under ASan, including retained rejection in the older library profile. The full matrix passed in CI on `main` ([nativeaot run 36972286145](https://github.com/dmitrat/WitOS/actions/runs/36972286145)). After the Q2 consolidation it passed again locally on the same ABI48 behavior: host tests 69, x64 `test` 20 and ARM64 `test` 14 scenarios, `release`, audit/probe/target/source, PE corpus/coverage/fuzz/imports ASan, QemuCleanup, coreclr-source/host/host-files/functions/memory/storage, `runtime-config` and a full `runtime-boot` rebuild. The static slice is closed; no final P6.4 or guest CoreCLR completion is claimed.
