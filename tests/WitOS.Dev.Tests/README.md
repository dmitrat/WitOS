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
