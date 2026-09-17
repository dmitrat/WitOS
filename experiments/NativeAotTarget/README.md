# NativeAOT target/bootstrap experiment

This is a **hosted Windows experiment**, not a WitOS guest runtime.

Run from the repository root:

```powershell
dotnet run --project tools/WitOS.Dev --configuration Release -- runtime-target
```

The project uses the same locked .NET 10.0.8 compiler/runtime baseline as NativeAotProbe. The command publishes a static archive and a shared library, verifies COFF/PE structure and native package provenance, and runs the shared library from a C process with no CoreCLR.

The exports exercise initialization, allocations/GC, exceptions and ThreadStatic state across native threads. Two strict links intentionally omit the runtime or OS/CRT libraries to expose unresolved dependencies. Expected missing-symbol failures are recorded, not suppressed with stubs.

Results, source arguments, native inputs and logs are under `artifacts/runtime-target/`. [The implementation decision](../../@Docs/Implementation/NativeAot-Target-Bootstrap.md) describes measured results, limitations and next guest work.

Static and Shared variants must use separate native intermediate and output directories, as the runner does. Their same-named .lib files otherwise represent different artifacts (static object archive versus DLL import library).
