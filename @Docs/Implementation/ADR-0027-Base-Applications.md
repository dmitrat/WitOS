# ADR 0027: Base applications — a layer of directories, apphost launchers, pinned upstream sources, Arlecchino

**Status:** Accepted 2026-10-18 by the author's decision.
**Date:** 2026-10-18.
**Scope:** How WitOS delivers and updates the applications an early system is worked with — PowerShell, CSharpRepl
(plan step C2.5), a file manager, and WitOS's own console tools — within the rules of RFC 0010; and which toolkit those
tools are built on.

## Context

The system is one image: the boot package holds the kernel's root task, the system layer, the services and the .NET
install, read-only, rebuilt by the tool and replaced whole. Applications are not part of it. Today one application
exists beside it: PowerShell, whose pinned archive (`src/Substrate/powershell.lock.json`) the tool lays on the data
volume under `/data/pwsh`, started as `dotnet /data/pwsh/pwsh.dll`. Nothing in the guest installs or updates an
application, nothing makes it a command, and there is no network until M7.

RFC 0010 fixes the principles: an application is an ordinary directory and copying it installs it (§3–§9); no
installation database is the source of truth (§5, §36); a package is a distribution envelope, not the execution model
(§20–§22); versions may sit side by side with a `current` choice and keep the previous for rollback (§124–§127); offline
updates are first-class (§132); NuGet stays the library ecosystem (§121–§122). It defers the package archive, the
registration, the update protocol and the store (§212).

Three applications are wanted next, and what was measured of them on 2026-10-18:

- **CSharpRepl** (`csharprepl`, MPL-2.0) is a .NET tool: a NuGet package whose `tools/net10.0/any` directory is a
  portable framework-dependent application.
- **A file manager.** Arlecchino.Commander (MIT, Midnight Commander's layout) and CSharpFar (MIT, Far Manager's) both
  run as portable applications by reading their code: Commander falls back deliberately where an unknown Unix lacks a
  pseudo-terminal, inotify or a trash; CSharpFar takes its Linux host with termios through the libc. Both publish
  only Linux self-contained or NativeAOT binaries, which cannot run on WitOS; a build from their sources by the stock SDK
  without a RID can. Both start commands through `$SHELL` or `/bin/sh`, which WitOS does not have.
- **WitOS's own console tools** (an installer, a service monitor, setup screens) need a toolkit. Arlecchino (MIT, the
  framework Commander is built on) was reviewed: a coherent core — a cell grid with a diffing renderer, an input
  decoder, a frame thread that owns all state — under nullable, warnings-as-errors and public-API analyzers, 1,170
  tests with an 85% coverage gate, the diff renderer checked against a VT model on every test frame, input and output
  through System.Console alone, AOT-compatible, a headless test host. Against it: one author, 37 thousand library lines
  written in eight weeks, an API that settled only in September, polling where events would do (a 60 Hz frame loop and
  an 8 ms `KeyAvailable` poll), selection drawn by background colour alone. Terminal.Gui (MIT, eight years, 137
  thousand lines) is mature but heavy, breaks its public API in minor releases, and its default driver computes a 0×0
  screen on an unknown Unix (it asks `TIOCGWINSZ` by the BSD value whenever `IsLinux()` is false); its `dotnet` driver,
  chosen by `TUI_CONFIG`, runs unchanged.
- The display's emulator draws white on black and ignores SGR and the private modes (C2.2), so every TUI's selection,
  focus and alternate screen are invisible there.

## Decision

1. **Three layers.** The system is the boot package, updated whole with the image. Base applications are a layer of
   RFC 0010 directories on the data volume. Every other application lives wherever its owner copies it (RFC 0010 §8).
   No base application enters the boot package or the system's start, as PowerShell never did (RFC 0020 §59).
2. **The layout.** A base application is `/data/apps/<ApplicationId>/<version>/`, the files as its source publishes
   them, with `witos-app.json` beside them: the identity, the version, the source and its SHA-256, the entry assembly
   with its arguments, the commands it provides and the framework it needs — the first concrete form of RFC 0010 §28's
   manifest, for this layer only; RFC 0010 keeps the general format deferred. `/data/apps/<ApplicationId>/current`
   names the version in use; it is a file until the volume has links (D7). State stays outside the version
   directories (the home directory), so an update never touches it (RFC 0010 §128).
3. **Commands are launchers made from upstream's apphost.** `host.native` already builds `apphost` for witos. For each
   command of the version in use, `/data/apps/bin/<command>` is that apphost written for the application's entry
   assembly as the SDK writes a .NET tool's shim (the path in the apphost's placeholder), so a base application is
   executable by path (RFC 0010 §4) and by name through `PATH`, which `/etc/environment` gives `/data/apps/bin`
   before `/dotnet`. Switching `current` rewrites the application's launchers.
4. **Sources are pinned upstream publications.** A catalog `build/apps/<ApplicationId>.json` names, for each base
   application, what its upstream publishes and the SHA-256 of the bytes or the commit: a release archive (PowerShell,
   whose entry moves there from `powershell.lock.json`), a NuGet tool package (CSharpRepl), or a repository commit the
   tool builds with the stock SDK as a portable application without a RID (the file manager). Native parts are built
   from source against the sysroot, as `libpsl-native` is (C1.1). Never a Linux RID's binary.
5. **Delivery comes in stages, each the same layer.**
   - Now: the tool's `apps` command lays the layer on a data disk and updates an existing disk in place: the new
     version beside the old, the launchers rewritten, the old version kept for rollback (C4.1).
   - Then: an installer in the guest, an ordinary .NET application, installs and updates base applications from a
     folder feed on any volume — another disk, `/fat`, a USB disk later — verified against the catalog's SHA-256
     (C4.3; RFC 0010 §132).
   - With M7: the same installer over HTTPS, from GitHub releases and nuget.org.
   - With M8: signatures and trust as RFC 0010 §44–§112 describe.
   No installation database exists: the layer is its directories, and the launchers are rebuilt from the manifests
   (RFC 0010 §36).
6. **No `/bin/sh`.** WitOS's shell is PowerShell. `/etc/environment` sets `SHELL` to `/data/apps/bin/pwsh`, so an
   application that runs commands through `$SHELL` gets PowerShell; one that hard-codes `/bin/sh` fails explicitly,
   and the difference is recorded in `Compatibility-Differences.md`. The system layer's substrate is never an
   application API (RFC 0011 §9), and a POSIX shell would make it one.
7. **WitOS's console tools are built on Arlecchino.** The tools take the NuGet packages pinned exactly (2026.9.3, with
   lock files) and are ordinary portable applications, so each also exercises WitOS's terminal stack. A terminal
   backend of WitOS's own (`IArlecchinoTerminal`, a 14-member seam selected by `UseTerminal<T>()`) lives in WitOS's code
   when a tool needs one. The fork `github.com/dmitrat/Arlecchino` is kept as a safety copy at upstream's tags; WitOS
   builds from it and changes the core only when a need arises that the options and the seam cannot cover (an
   event-driven loop, a reverse-video attribute, 256 colours, dropping the Windows console and the image decoders), with
   the MIT notice kept. Terminal.Gui is a compatibility workload, its UICatalog run unchanged with `TUI_CONFIG` choosing
   the `dotnet` driver, not WitOS's toolkit; the generic fixes it needs (the console's size through
   `Console.WindowWidth` when its own call fails, the `dotnet` driver on an unknown Unix) are proposed upstream.
8. **The display's emulator learns what a TUI needs before the first TUI step** (C2.6): SGR colours and attributes,
   the cursor's visibility, the alternate screen, the string sequences it must swallow, the answers to the device
   attributes and the size queries, wide characters, and the keyboard's modified function keys.

## Consequences

- The plan's phase C gains C2.6 (the emulator) and C4 (base applications): C4.1 the layer, its catalog, its launchers
  and the `apps` command, with PowerShell moved onto it; C2.5's CSharpRepl as the layer's second application; C4.2 the
  file manager, Arlecchino.Commander first, with Terminal.Gui's UICatalog as a second workload and CSharpFar measured;
  C4.3 the installer on Arlecchino. The order is C2.6, C4.1, C2.5, C4.2, C4.3.
- `shell` (C1.4, C2.4) boots with the layer, and `PATH` and `SHELL` change for every process the process manager starts
  from `/etc/environment`.
- RFC 0010 is unchanged; this decision is one implementation within its rules and answers its deferred questions for
  base applications alone.
- WitOS owns no TUI framework until the fork is needed; the price is depending on one author's package, bounded by the
  exact pin and the fork.

## Alternatives considered

- **Base applications in the boot package.** Rejected: an application's update would need a new system image, the
  package is read-only, and RFC 0020 §59 keeps PowerShell outside the system's start.
- **NuGet as the general application format.** Rejected by RFC 0010 §122; NuGet tool packages are accepted as one kind
  of upstream source, unpacked into an ordinary directory.
- **A POSIX shell (`/bin/sh`) for applications that start commands.** Rejected: it would expose the substrate as an
  application API, and PowerShell serves `$SHELL`.
- **Forking Arlecchino now and building from the fork.** Deferred: it would mean owning 37 thousand lines and their
  generators before any need; the safety fork keeps the option.
- **Terminal.Gui as WitOS's toolkit.** Rejected for WitOS's own tools: its weight, its breaking minor releases and its
  default driver's failure on an unknown Unix; kept as a workload.

## Related

RFC 0010 (application packaging and distribution); RFC 0020 §28, §34, §59 (terminal, developer loop, PowerShell as an
ordinary application); ADR 0024 (the substrate private to the system layer); plan steps C1.1–C1.4, C2.2, C2.4, C2.5.
