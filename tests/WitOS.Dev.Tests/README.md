# Host tooling regressions

Run on the development host:

```powershell
dotnet build WitOS.slnx --configuration Release
dotnet test tests/WitOS.Dev.Tests --configuration Release --no-build
```

This NUnit 4 suite fails on any failed assertion. It exercises real child processes (including inherited pipes), compiler argv, boot verdicts, image conversions, the pinned upstream patches, documentation, manifests and workflows. Tests that need Windows or MSVC carry `[Platform(Include = TestPlatforms.WINDOWS, ...)]` and are skipped on a Linux host.

Fixtures follow the folders of the tool: `Host`, `Images`, `Kernel`, `Runtime`, `Substrate` and `Upstream`. `Native` holds the C harnesses next to the fixtures that build and run them, `Repository` checks documentation, manifests and workflows, and `Support` holds shared infrastructure. Process tests start the separate `tests/WitOS.Dev.Tests.Child` executable, which the build copies next to the tests.

Explicit categories run only when selected; each needs extra prerequisites:

| Category | Contents | Needs |
| --- | --- | --- |
| `PeImportsAsan` | Import descriptor and DLL TLS cases built with AddressSanitizer | the pinned LLVM, extracted by `setup` |
| `QemuCleanup` | 12 QEMU socket-QMP timeouts with native process/job completion | the pinned QEMU from `setup` |

```powershell
dotnet test tests/WitOS.Dev.Tests --configuration Release --no-build --filter TestCategory=QemuCleanup
```

Generated files stay under artifacts/q0-tests/<run-id>/<test>. Test subprocesses are bounded and hidden.

Run one test or fixture with a name filter:

```powershell
dotnet test tests/WitOS.Dev.Tests --configuration Release --no-build --filter Name=AssemblyPackageCanonicalBytesTest
dotnet test tests/WitOS.Dev.Tests --configuration Release --no-build --filter FullyQualifiedName~Native.PeImportsTests
```

Do not rebuild while a gate uses the tool or the test assemblies on Windows. Wait for active gates, build once, then use `--no-build` for their invocations.

## Native harnesses

The boot package is the kernel's: the independent common package parser receives readonly trailing-guard inputs, including truncations with repaired total-length fields, and the actual firmware transport runs against fault-injected protocol callbacks (`AssemblyPackageCanonicalBytesTest` for the writer, `AssemblyPackageNativeGuardedTest` for 9750 parser cases, 14 firmware publication/rollback cases and native byte extraction). `VirtualGapPropertyCasesTest` runs the common kernel's gap search on hosted property cases.

The other harnesses test the kernel policy RFC 0011 §8 moves to the system layer and its user side in `src/Runtime.Native`; they leave with that policy at plan step K8.4:

- `FileViewFailureTransactionsTest` runs the native file-view adapter against deterministic hosted syscall failures, including failed rollback cleanup.
- `NativePathNormalizationTest` runs the UTF-8 lexical path resolver on 622 readonly guard-boundary cases and an oversized-length atomic-rejection case; `NativeDirectoryEnumerationTest` runs 84,568 small-pattern/name comparisons plus Unicode-scalar matching and immutable iterator/CWD/error checks.
- `NativeLibraryExportsTest` builds a no-entry native DLL and runs 3088 guarded PE/export cases, compared with Windows LoadLibrary/GetProcAddress for named, ordinal, alias and data exports.
- `NativeImportDescriptorsTest` verifies the unbound AMD64 import-descriptor parser with 2582 readonly trailing-guard cases beside 8206 full-PE cases from linker-generated imported and cyclic DLLs; `PeImportsAsan` repeats it with the pinned LLVM and AddressSanitizer and adds the opt-in DLL TLS profile's 4613 full-image cases.
- `NativeDllThreadReferenceTest` and `NativeDllTlsReferenceTest` build real no-CRT DLLs with thread notifications and with compiler TLS as Windows oracles of the kernel's DLL lifecycle.

None of these tests establishes guest execution; the guest suites of `test` and `test --arch arm64` do.
