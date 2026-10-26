# R9 — The shared framework in ReadyToRun

The author asked for ReadyToRun on 2026-10-26 because PowerShell started slowly on VMware. H5.3 first removed the larger
costs: the HPET clock and the uncached volumes ([H5.3-Session-Speed.md](H5.3-Session-Speed.md)). Then the JIT was the
largest remaining part of a start: 10,197 compilations at 0.29 ms each. This step makes the guest's shared framework
what upstream ships for Linux: CoreLib and the libraries with ReadyToRun code beside their IL, which the runtime takes
instead of compiling.

## 1. What the guest already accepted

The ReadyToRun format names an image's operating system by a mark in the PE machine field
(`IMAGE_FILE_MACHINE_NATIVE_OS_OVERRIDE`).

- `pedecoder.h` chooses the mark CoreCLR expects by the compiler's macros. The musl triple of layer 2 defines
  `__linux__`, so WitOS's CoreCLR expects Linux's mark, 0x7B79.
- S8.2 already ran Linux's images from the SDK's archives on x64 and ARM64. On x64 the relocations lie in `.data`; on
  ARM64 the code needs a writable window, which S8.2 gave it.
- So the guest needs nothing new: only images.

The tree's crossgen2 knew `--targetos witos` (R1.3, with ILC), but stopped in two switches over the target's OS:

- the machine mark (`TargetExtensions.MachineOSOverrideFromTarget`);
- the perf map's signature (`PerfMapWriter.TranslateTargetDetailsToPerfmapConstants`).

Both threw `NotImplementedException` for `TargetOS.WitOS`.

## 2. The changes

- **Two patches of crossgen2** (`patches/runtime/release-10.0`): `TargetOS.WitOS` takes Linux's machine mark and
  Linux's perf map token. One ReadyToRun format serves Linux and WitOS, as one runtime build already reads both.
- **`runtime-witos`** builds the shared framework in ReadyToRun after the hosts (`BuildReadyToRunAsync`), before the
  first scenario of the install (`runtime-dotnet`):
  - The host's compiler (`BuildCompilerAsync`, R1.3) is built there, earlier than before. Its JITs for the x64 Linux
    host are laid where a cross build lays its cross components: `witos.<arch>.Release/x64` holds
    `libjitinterface_x64.so` and the JITs for x64, ARM64 and riscv64 (V2.4's community JIT).
  - Upstream's `clr.nativecorelib` compiles CoreLib with `crossgen2_inbuild` and `--targetos:witos`.
  - Upstream's CoreCLR runtime pack (`Microsoft.NETCore.App.Runtime.CoreCLR.sfxproj`, whose `PublishReadyToRun` is on
    for every OS but NetBSD, illumos, Solaris and Haiku) compiles the libraries.
  - The pack's platform manifest is the one its files give (`/p:UseTemplatedPlatformManifest=false`). The witos build
    leaves `libclrinterpreter.so` in CoreCLR's shared framework output, which upstream's Release template does not
    list.
  - The pack's libraries go to `artifacts/bin/witos-readytorun/<arch>` in the tree. The step requires a ReadyToRun
    CoreLib there.
- **The install** (`FrameworkFilesAsync`) takes CoreLib and the libraries from there; without them it stops and names
  `runtime-witos`. `.NET 8`'s tree (N3) keeps its IL framework, by the same function's `readyToRun: false`.
  - The corerun scenarios keep the IL CoreLib on purpose: they judge the JIT's path.
  - ILC takes the IL libraries as before.
- **`runtime-readytorun --arch <arch>`** repeats the step alone on the tree an earlier `runtime-witos` built and patched,
  as `runtime-services` repeats the services' step.

## 3. What it gives

On each of the three ISAs the pack carries 92 of its 172 libraries compiled, CoreLib among them. The other 80 are type
forwarders (`netstandard`, `mscorlib`, `System.Runtime`, …), which have no code to compile. The package grows by about
37 MB. The package lies in memory, so ReadyToRun images there cost no copy, unlike a volume's files (H5.3 §6).

On VMware (two vCPUs, the session's disk), with PowerShell's assemblies still IL:

| | IL framework | ReadyToRun framework |
| --- | --- | --- |
| The prompt after the machine starts | 17.0 s | 16.1 s |
| A child `pwsh -NoProfile -Command exit` | 2.5 / 2.2 s | 2.4 / 2.0 s |
| The child's JIT | 10,197 methods, 3.0 s | 4,902 methods, 2.4 s |

The framework's methods were the cheap ones. What the JIT still compiles at a start is PowerShell's own code: its
archive is IL alone (C1.3), and the interactive session had compiled 18,279 methods by its prompt and the probes.

## 4. The gate

| Check | Result |
| --- | --- |
| `runtime-readytorun --arch x64`, `arm64`, `riscv64` (container) | the pack and CoreLib for each ISA, 92 of 172 libraries compiled |
| `runtime-witos --arch x64` (container) | every scenario on the ReadyToRun framework: the hosts, R6's portable application, upstream's `src/tests` slices and library tests, the services, the terminal, the display, PowerShell, the commander, the installer, CSharpRepl, the compatibility suite, ASP.NET Core, the representative libraries and the processors |
| CI's `linux-host` legs | the three ISAs |
| host tests | the patch set against the pin (`UpstreamPatchesTests`, `RuntimeWitosTests`), the consistency tests |

`runtime-libraries` boots with a gigabyte now. Two of its Roslyn tests read every library of the framework whole as a
reference (`MetadataReference.CreateFromFile`). ReadyToRun images are 2.5 times their IL, and the package that holds them
lies in the guest's memory. With 512 MiB, 266 of them usable where about 300 had been, the reads met
`OutOfMemoryException`. A Linux machine meets the same, since its framework is ReadyToRun too.

The patch set holds 42 files: 18 of CoreCLR against FreeBSD's 31 and Haiku's 18, and 6 of the native libraries.

## 5. What is not yet

- **PowerShell's own code**, and the other base applications'. Compiling the layer's IL with the tree's crossgen2 for
  each ISA amends ADR 0027, whose layer takes upstream's publications as they are. That is the author's decision.
  Measured on VMware with PowerShell's 59 top-level assemblies compiled by `--targetos witos` in a scratch copy:

  | | R9 | R9 and PowerShell's assemblies |
  | --- | --- | --- |
  | The prompt after the services' readiness | 6.5 s | 5.2 s |
  | A child `pwsh -NoProfile -Command exit` | 2.4 / 2.0 s | 1.8 / 1.3 s |
  | The child's JIT | 4,902 methods, 2.4 s | 266 methods, 0.4 s |
  | The interactive session's JIT by its prompt and the probes | 18,279 methods, 8.7 s | 6,475 methods, 3.6 s |

  The rest is the modules under `Modules/` (PSReadLine among them), which the trial left IL, the dynamic methods
  PowerShell builds from scripts, and the hot methods' second tier.
- **The ASP.NET Core shared framework** stays IL (W3.2). The same crossgen2 could compile it.
- **Profile-guided optimization data.** Upstream's builds take mibc profiles from optimization packages, which exist for
  Linux, not for witos. The images are compiled without them.
