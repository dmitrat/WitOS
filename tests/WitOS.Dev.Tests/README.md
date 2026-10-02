# Host tooling regressions

Run on the supported Windows development host:

```powershell
dotnet run --project tests/WitOS.Dev.Tests --configuration Release
```

This dependency-free .NET console suite exits nonzero on any failed assertion. It exercises real child processes (including inherited pipes), actual CLI failure handling, compiler argv, the per-base guest protocol, attempt history/publication, and workflow path selection. The initial baseline failed five regression groups; see artifacts/q0-baseline-tests.log.

After runtime-source has produced the standard-CoreLib PE, include the hosted native parser corpus and a real compiler invocation with quoted include/define arguments:

```powershell
dotnet run --project tests/WitOS.Dev.Tests --configuration Release -- --pe
```

The C harness links the actual common kernel PE validator. Its 555 readonly/trailing-guard inputs use deterministic seed 0x57314A29. It checks bounded memory safety, not acceptance/rejection of every mutation; some mutations affect ignored bytes. This is not coverage-guided fuzzing or a coverage percentage.

Generated files stay under artifacts/q0-tests/<unique-id>. Test subprocesses are bounded and hidden. The tests do not alter the real runtime acceptance manifest: failure/history cases use isolated roots. A missing-source CLI error is captured as an expected negative case.

Q1 adds 26 structural verdict cases inside that total: pdata/xdata, TLS directories/templates/callbacks, relocation blocks and two valid controls. Failure output names the mutation, byte offset and expected/actual status. Host regressions additionally cover complete protocol envelopes, write/rename failures around the single commit record, interrupted-run recovery, callbacks ignoring cancellation, output limits and named native object manifests.

Optional focused commands:

```powershell
dotnet run --project tests/WitOS.Dev.Tests --configuration Release -- --q1-protocol
dotnet run --project tests/WitOS.Dev.Tests --configuration Release -- --pe-only
dotnet run --project tests/WitOS.Dev.Tests --configuration Release -- --pe-coverage
dotnet run --project tests/WitOS.Dev.Tests --configuration Release -- --pe-fuzz
```

The coverage/fuzz lanes extract the SHA-256-pinned official LLVM 20.1.8 installer under `.tools/`, using 7-Zip; no system installation or PATH modification occurs. The 555-case coverage build uses LLVM branch instrumentation and AddressSanitizer on the actual `pe.c` and shared unwind validator. Its initial branch coverage is 282/486 (58.02%) and 132/228 (57.89%), respectively. This is a hosted native-parser baseline, not whole-project coverage. The full-image corpus does not exercise every plain-profile branch. `native-coverage.json` retains machine-readable counts and `native-coverage-profile.json` identifies the executable and sources.

The separate libFuzzer/ASan smoke lane uses actual runtime PE and truncated-header seeds, 500 runs, seed 1462848041, maximum input 1 MiB, per-input timeout 5 seconds, RSS limit 512 MiB and runner deadline 120 seconds. Findings/corpus/logs remain under the unique ignored output directory. This bounded lane is in CI; it is not exhaustive fuzzing. Larger campaigns can use the generated executable with a separate artifact prefix and an explicitly bounded budget.

`current-run.json` schema 3 is the authoritative runtime attempt record. A success is committed by its single atomic rename and identifies a hash-checked immutable acceptance snapshot in `lastSuccess`. Top-level `acceptance.json` and `last-success.json` are projections; a failed projection write does not undo a committed run. Read the record and verify the referenced hash, rather than trusting a projection alone. The next attempt repairs projections and classifies an uncommitted previous attempt as interrupted while owning `run.lock`. Per-file atomic replacement is a process-crash/failure protocol, not a power-loss durability guarantee.

Do not rebuild the host executable while a `dotnet run` gate is using it on Windows. Wait for active gates, build once, then use `--no-build` for their invocations. Historical logs in protocol tests are explicitly upgraded in memory for the Q1 fixture's worker/hijack counters; they are mutation-test input only, never fresh guest acceptance.

P6 storage checks run as part of the host suite. The independent common package parser receives readonly trailing-guard inputs, including truncations with repaired total-length fields; the actual firmware transport runs against fault-injected protocol callbacks. Focused commands: `--assembly-package` (writer) and `--assembly-package-native` (9750 parser cases, 14 firmware publication/rollback cases and native byte extraction). `coreclr-storage` through WitOS.Dev separately boots actual managed DLL/configuration bytes and readonly file IO in the guest; it does not execute CoreCLR.

`coreclr-host` builds the pinned upstream Windows host and exercises eight actual framework/deps cases with an ordinary net10.0 project dependency. Run `coreclr-source` first to produce its verified source-built CoreCLR/JIT prerequisite. The separate hosting CI lane preserves this order. This reference does not establish guest hosting support.

`--file-view-faults` runs the actual native file-view adapter against deterministic hosted syscall failures, including failed rollback cleanup. The guest `coreclr-storage` suite separately verifies real reservation/handle lifetime, readonly/NX faults and allocation-boundary rollback. These tests do not claim general shared file mappings or guest CoreCLR execution.

`coreclr-host-files` through WitOS.Dev verifies actual pinned corehost PAL file signatures with real C++ strings and the production view adapter on a controlled hosted syscall backend. Its atomic current-run record separates this proof from the additional import-free guest constexpr-string leaf probe in coreclr-storage. Neither closes the full host C++/runtime dependency boundary.

`--native-paths` runs the production UTF-8 lexical path resolver on 622 readonly guard-boundary cases and an oversized-length atomic-rejection case. Guest storage tests additionally prove process-wide CWD, real directory validation and relative PAL file calls. Full C++ getcwd/fullpath/realpath entry points are hosted-tested; their genuine std::wstring allocation/EH dependencies remain explicit.

`--native-directory` runs 84,568 independent small-pattern/name comparisons plus Unicode-scalar matching and immutable iterator/CWD/error checks. Actual PAL readdir overloads are hosted-tested with real vectors/strings; guest storage tests discover the standard framework version directory and app/framework JSON files. General C++ allocation/EH closure is still pending.

The coreclr-host-files reference also exercises actual environment PAL signatures. Its Windows API counting shim forwards to the real OS implementation and proves environment-block release on callback exceptions. Guest environment blocks use the real native heap and are required by the PalEnvironmentBlocks marker in the normal kernel/runtime matrix.

`--native-library` builds an actual no-entry native DLL and runs 3088 guarded PE/export cases, including every truncation, explicit unsupported import/TLS/delay-import directories, ordinal-only and no-export controls. Windows LoadLibrary/GetProcAddress comparisons verify real named/ordinal/alias/data addresses and calls. Guest coreclr-storage separately checks relocation, reference lifetime, OOM rollback, protected image/alias boundaries and actual PAL calls. The coreclr-host-files reference additionally uses real C++ strings and a Windows DLL-backed transport to exercise library PAL errors and lifetime. None of these tests establishes full dependency loading, DLL initialization, dynamic TLS or guest CoreCLR startup.

`--pe-imports` verifies the independent unbound AMD64 import-descriptor parser with 2582 readonly trailing-guard cases. `--pe-imports-asan` repeats it with the pinned LLVM/ASan toolchain and records executable/source hashes. The corpus includes named/ordinal imports, absent OriginalFirstThunk, malformed metadata, IAT mismatches/overlaps and inclusive 16-module/512-symbol quotas. The 2582 section-map units now accompany 8206 full-PE cases from actual linker-generated imported/cyclic DLLs and Windows call comparisons. The dedicated library-import profile validates full images; ordinary main-image profiles still reject imports. Guest coreclr-storage separately verifies dependency graph/IAT publication, cyclic/shared lifetime, missing-module/symbol and allocation/handle rollback, and readonly IAT hardware faults.

The import/DLL reference also starts a separate Windows process which loads the real initialization graph and terminates via ExitProcess. A parent-owned shared-memory witness verifies process-detach ordering and the nonnull reserved argument after the child's address space is gone. Guest coreclr-storage separately runs the actual CRT shutdown path and checks TLS/atexit/runtime-notification ordering before DLL detach and namespace closure.

`--dll-thread-reference` builds a real no-CRT DLL with thread notifications and exercises normal and initially suspended Windows threads. It verifies attach/detach identities and that callbacks have not run before resume; process detach runs on the unload caller. This is a hosted oracle only. The same fixture is prepared for guest integration, which is still pending.

`--dll-tls-reference` builds genuine compiler TLS with an own-native dependency and checks Windows isolation, zero data, addresses and relocated template pointers. `--pe-imports-asan` additionally exercises the opt-in DLL TLS PE profile with 4613 full-image cases. The guest storage matrix separately checks real two-module TLS, main-slot preservation, forged-vector-safe teardown and all worker allocation boundaries; nonempty DLL TLS callbacks remain unsupported.
