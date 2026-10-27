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
- **The libc gives an executable package mapping a copy on its first write.** CI's ARM64 leg met this first:
  - ARM64's images carry twelve base relocations in `.text`, as S8.2 measured for the SDK's. The image never loads at
    its preferred base, so CoreCLR writes them through a writable window.
  - On a volume (S8.2) that worked, since a volume's code is a copy in a shared memory file. The framework now maps from
    the package, and the libc refused to make an executable package mapping writable: plain memory never executes.
  - `runtime-dotnet` stopped with `Failed to load System.Private.CoreLib.dll (error code 0x8007000C)`.
  - Now `privatize` turns the pages the write reaches into a copy in a shared memory file of the process's own
    (`copy_package_code`, filled through a window of the package, as D5.3 fills a volume's code), whose chunks carry
    both rights, while the rest of the mapping stays the package's pages. S8.2's flip then works, and the copy is
    published when executable.
  - The first form copied the whole mapping, and the arm64 compatibility suite's children ran out of memory in a guest
    of 512 MiB: the framework's images hold 55 MB of code on arm64, and every process copied the code of each image it
    loaded, where their relocations touch 178 pages in all, one or two an image. Linux copies the written pages of a
    private file mapping alone, and the libc now does the same: the copy is a file of the library's own sized once
    (`__wit_shared_anonymous_fixed`), whose chunk objects take the pages it needs, where a chunk is otherwise 64 pages,
    so a copy of one page costs one page.
  - The libc's tables are the root task's writable segment, which the flat format bounds at a MiB
    (`WIT_FLAT_MAX_SEGMENT_BYTES`): the two fields the fixed file added to `SharedFile` put the segment 584 bytes over,
    so the record is packed (its flags and mode in bytes) to 320 bytes, 8 below what it was, and the segment lies some
    16 KiB below the bound. The next table that grows raises the bound or moves a table out of the image.
  - `libc_hello.c` checks it: of a file's three pages of code mapped together, the one written and made executable
    again still runs, and the pages around it stay the file's. Before, it checked the `EACCES`.
- **riscv64 keeps the IL framework** (`KernelArchitecture.ReadyToRunFramework`), as the NuGet libraries' suites and .NET
  8 stay on x64 and ARM64 until a RISC-V board is a release target.
  - Its images build, load and relocate.
  - With `DOTNET_ReadyToRun=0` the same install passes `runtime-dotnet`.
  - Running their code ends in a fault whose resume address the kernel refuses (`EXCEPTION_CONTINUE`: `BAD_ADDRESS`,
    pc 0x10080545030), and `dotnet` ends by signal 6.
  - Whether the community RISC-V port's ReadyToRun code or the guest's fault path is at fault is a step of its own.

## 3. What it gives

On each of the three ISAs the pack carries 92 of its 172 libraries compiled, CoreLib among them, and the install takes
it on x64 and ARM64. The other 80 are type
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
| `test` of the three ISAs (container, with the page-granular copy) | 32 scenarios on each; the libc scenario's three checks of a copied code page |
| `runtime-dotnet --arch riscv64` (container) | the IL framework, since its ReadyToRun code faults (§5) |
| `runtime-witos --arch arm64` (container, with the page-granular copy) | 30 scenarios in 71 minutes, the compatibility suite among them, whose children ran out of memory with a copy of the whole mapping (§2) |
| `runtime-witos --arch x64` (container, before the page-granular copy) | every scenario on the ReadyToRun framework: the hosts, R6's portable application, upstream's `src/tests` slices and library tests, the services, the terminal, the display, PowerShell, the commander, the installer, CSharpRepl, the compatibility suite, ASP.NET Core, the representative libraries and the processors; after the copy's change, x64's `test`, `runtime-dotnet` and `runtime-compatibility` in the container and the whole command in CI's x64 leg |
| CI's `linux-host` legs | the three ISAs |
| host tests | the patch set against the pin (`UpstreamPatchesTests`, `RuntimeWitosTests`), the consistency tests |

`runtime-libraries` boots with a gigabyte now. Two of its Roslyn tests read every library of the framework whole as a
reference (`MetadataReference.CreateFromFile`). ReadyToRun images are 2.5 times their IL, and the package that holds them
lies in the guest's memory. With 512 MiB, 266 of them usable where about 300 had been, the reads met
`OutOfMemoryException`. A Linux machine meets the same, since its framework is ReadyToRun too.

The patch set holds 42 files: 18 of CoreCLR against FreeBSD's 31 and Haiku's 18, and 6 of the native libraries.

## 5. What is not yet

- **PowerShell's own code**, and the other base applications'. Compiling the layer's IL with the tree's crossgen2 for
  each ISA at the distribution's build would amend ADR 0027, whose layer takes upstream's publications as they are.
  The author decided against it on 2026-10-27 (§6); the layer's code is compiled where it is installed instead (R9.2).
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

## 6. The decision of 2026-10-27: no precompilation in the distribution

The author doubted precompilation: it binds a distribution to a platform, against the idea of the .NET layer, and it
complicates a custom distribution with a subset of the base applications and subsystems for an embedded device. They
asked whether a cache filled at a program's first run, as they remembered Windows doing, would serve instead. The facts
of the discussion:

- The first-run cache is .NET Framework's: NGEN at an installation and, since 4.5, Auto-NGEN in the background into the
  machine's native image cache. CoreCLR has no such cache: the JIT's output lives in the process's memory and carries
  its addresses, and the one form of compiled code on disk is ReadyToRun, which crossgen2 produces ahead of time. A
  cache of compiled code is therefore ReadyToRun images produced where the program is installed, not a runtime
  feature; a cache inside CoreCLR would change the JIT, which RFC 0015 §4's order of preference forbids.
- A ReadyToRun image is bound to its ISA: CoreCLR refuses an image of another machine without falling back to its IL
  (W3.2). Where the images lie decides what that binds. The shared framework is part of the system image, built for
  each ISA with its native runtime, hosts and libraries, as upstream's runtime pack is, and upstream's packs are
  ReadyToRun on every platform: R9 binds nothing new. The applications of ADR 0027 are where portability lives: their
  publications are IL and run on every ISA, and the layer stays so.
- A subset: the images are compiled one assembly at a time, each self-contained, and 80 of the 172 are type forwarders
  without code, so a distribution leaves libraries out as it would IL ones. The one cost is memory: ReadyToRun images
  are 2.5 times their IL and the package lies in memory (+37 MB), so an image for a small memory may take the IL
  framework by the install's existing choice (`FrameworkFilesAsync`'s `readyToRun`).

The decision:

1. The framework stays ReadyToRun in the system image; the IL framework becomes an option of the install.
2. The applications' layer stays IL, the publications as upstream makes them. §5's compilation at the distribution's
   build is not taken.
3. R9.2 becomes the first-run cache in its honest form, compilation at the installation on the device: `witos-apps`
   runs crossgen2 on the device when it installs an application and writes the compiled publication into a derived
   directory, `<version>/native-<arch>/`, which may be deleted and made again, and the launcher `use` writes points at
   it where it exists; the host's `apps` fills the same directory by cross-compilation when it writes a disk. The step
   is optional — a disk without the directory runs on the JIT, and riscv64 waits for R9.3 — and needs crossgen2 for
   the guest (the target ISA's JIT and `libjitinterface_<arch>.so`, `clr.alljits+clr.tools -os witos`) and a
   measurement nothing has made: PowerShell's 59 assemblies take minutes on the x64 host, and a Pi 4 under the JIT is
   estimated at 15–30 minutes, a background task after an installation, never the first run itself.
4. The order stays the author's of 2026-10-26: their session on VMware first, then R9.2.
