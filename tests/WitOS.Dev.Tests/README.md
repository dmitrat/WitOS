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
| `QemuCleanup` | 12 QEMU socket-QMP timeouts with native process/job completion | the pinned QEMU from `setup` |

```powershell
dotnet test tests/WitOS.Dev.Tests --configuration Release --no-build --filter TestCategory=QemuCleanup
```

Generated files stay under artifacts/q0-tests/<run-id>/<test>. Test subprocesses are bounded and hidden.

Run one test or fixture with a name filter:

```powershell
dotnet test tests/WitOS.Dev.Tests --configuration Release --no-build --filter Name=AssemblyPackageCanonicalBytesTest
dotnet test tests/WitOS.Dev.Tests --configuration Release --no-build --filter FullyQualifiedName~Native.VirtualGapTests
```

Do not rebuild while a gate uses the tool or the test assemblies on Windows. Wait for active gates, build once, then use `--no-build` for their invocations.

## Native harnesses

The boot package's reference reader (`PackageReader.c`, the kernel's parser until plan step K8.4a; the guest's reader is the libc's) receives readonly trailing-guard inputs, including truncations with repaired total-length fields, and the actual firmware transport runs against fault-injected protocol callbacks (`AssemblyPackageCanonicalBytesTest` for the writer, `AssemblyPackageNativeGuardedTest` for 9750 parser cases, 14 firmware publication/rollback cases and native byte extraction). `VirtualGapPropertyCasesTest` runs the common kernel's gap search on hosted property cases.

The harnesses of the kernel policy RFC 0011 §8 moved to the system layer (the file view, paths and directories of `src/Runtime.Native`, PE exports and imports, and the Windows references of the DLL lifecycle and DLL TLS) left with that policy at plan step K8.4a.

None of these tests establishes guest execution; the guest suites of `test` and `test --arch arm64` do.
