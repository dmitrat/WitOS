# WitOS — Lessons and Reusable Ideas from BootTo.NET
## Research Notes / Implementation Guidance
### Draft v0.1

## 1. Purpose

This document records the parts of [BootTo.NET](https://github.com/nifanfa/BootTo.NET) that are worth studying while implementing WitOS.

BootTo.NET is not a direct implementation model for WitOS. It solves a different problem: it creates a rich C# execution environment inside UEFI, using a custom CoreLib and an IL-to-LLVM compiler.

WitOS has different long-term goals:

```text
standard upstream .NET
+
small WitOS kernel
+
explicit hardware/kernel boundary
+
managed system services
+
standard BCL compatibility
+
eventual CoreCLR support
```

The goal of studying BootTo.NET is therefore:

```text
learn from proven implementation techniques
reuse ideas where they fit
accelerate early bring-up
avoid repeating solved boot/tooling problems
```

—not to adopt its runtime architecture wholesale.

---

# 2. Executive Summary

The most useful areas to study are:

```text
1. UEFI entry and managed-code handoff
2. C# UEFI bindings
3. QEMU + EDK2 development workflow
4. EFI/DXE driver loading
5. early graphics/input/network bring-up
6. async/Task scheduling in a firmware environment
7. validation of C# language/runtime features
8. native/managed interop boundary
9. file/network compatibility shims
10. IL-to-native pipeline as a comparison point
```

The most important things **not** to adopt as WitOS architecture are:

```text
custom CoreLib as the long-term BCL
custom .NET-compatible runtime surface
IL2LLVM as the WitOS application runtime
UEFI as the permanent operating-system substrate
DXE drivers as the permanent WitOS device model
```

---

# 3. Repository Entry Points

Main repository:

- [BootTo.NET](https://github.com/nifanfa/BootTo.NET)
- [README](https://github.com/nifanfa/BootTo.NET/blob/master/README.md)

Related compiler:

- [IL2LLVM](https://github.com/nifanfa/IL2LLVM)
- [BootTo.NET IL2LLVM notes](https://github.com/nifanfa/BootTo.NET/blob/master/IL2LLVM/README.md)

The repository is particularly useful because it contains both:

```text
managed C# code
native entry/runtime helpers
UEFI bindings
QEMU tooling
working examples
```

---

# 4. UEFI Entry Point and Managed Handoff

## Why it matters

WitOS M0 needs a minimal and reliable path from firmware to the kernel.

BootTo.NET demonstrates a very small native UEFI entry point that receives the EFI system table and transfers execution into managed code.

Study:

- [EfiApplication/EfiApplication.c](https://github.com/nifanfa/BootTo.NET/blob/master/EfiApplication/EfiApplication.c)
- [ConsoleApp1/Program.EFI.cs](https://github.com/nifanfa/BootTo.NET/blob/master/ConsoleApp1/Program.EFI.cs)

The pattern is approximately:

```text
UEFI
 ↓
EfiMain()
 ↓
capture EFI_SYSTEM_TABLE / Boot Services
 ↓
managed_EfiMain()
 ↓
C#
```

For WitOS the equivalent should probably become:

```text
UEFI
 ↓
WitBoot.Uefi
 ↓
collect boot information
 ↓
construct WitBootInfo
 ↓
ExitBootServices
 ↓
WitOS kernel entry
```

### What to reuse conceptually

- tiny native entry point;
- explicit handoff structure;
- UEFI-provided allocation during early bootstrap;
- ability to move almost immediately into C# tooling/code where appropriate.

### What not to copy architecturally

BootTo.NET stays dependent on UEFI services for much of its runtime.

WitOS should use UEFI primarily for **bootstrap**, then cross into its own kernel model.

---

# 5. C# UEFI Bindings

BootTo.NET includes an extensive C# mapping of UEFI structures and protocols.

Directory:

- [uefi-cs](https://github.com/nifanfa/BootTo.NET/tree/master/uefi-cs)

Useful files include:

- [efi.cs](https://github.com/nifanfa/BootTo.NET/blob/master/uefi-cs/efi.cs)
- [efiapi.cs](https://github.com/nifanfa/BootTo.NET/blob/master/uefi-cs/efiapi.cs)
- [efifs.cs](https://github.com/nifanfa/BootTo.NET/blob/master/uefi-cs/efifs.cs)
- [efigpt.cs](https://github.com/nifanfa/BootTo.NET/blob/master/uefi-cs/efigpt.cs)
- [efi_pxe.cs](https://github.com/nifanfa/BootTo.NET/blob/master/uefi-cs/efi_pxe.cs)

## Potential value for WitOS

These files are useful as a reference for:

```text
EFI_SYSTEM_TABLE
EFI_BOOT_SERVICES
GOP
filesystem protocols
PXE/network protocols
loaded-image protocols
device paths
memory maps
runtime services
```

They can help define the first `WitBoot.Uefi` adapter without translating the UEFI specification from scratch.

## Important caveat

Do not assume these bindings should become the permanent WitOS hardware API.

They are firmware-facing structures.

The desired boundary remains:

```text
UEFI structures
      ↓
WitBoot.Uefi adapter
      ↓
WitBootInfo / UHI bootstrap representation
      ↓
WitOS
```

---

# 6. QEMU + EDK2 Development Workflow

This is one of the most immediately reusable ideas.

Study:

- [README — building and running](https://github.com/nifanfa/BootTo.NET/blob/master/README.md)
- [Qemu.targets](https://github.com/nifanfa/BootTo.NET/blob/master/Qemu.targets)
- [Directory.Build.targets](https://github.com/nifanfa/BootTo.NET/blob/master/Directory.Build.targets)

BootTo.NET integrates:

```text
Visual Studio/MSBuild
       ↓
build
       ↓
create disk image
       ↓
launch bundled QEMU + EDK2
       ↓
debug/run
```

It also handles QEMU acceleration selection and creates a disposable UEFI variable environment.

## Recommended WitOS adaptation

WitOS should use the same philosophy but keep the implementation independent:

```text
dotnet build
    ↓
Wit ImageBuilder
    ↓
Wit QemuRunner
    ↓
QEMU + OVMF/EDK2
    ↓
serial test protocol
    ↓
CI pass/fail
```

Possible projects:

```text
tools/
    Wit.ImageBuilder/
    Wit.QemuRunner/
    Wit.TestRunner/
```

BootTo.NET is a useful reference for how to make OS development feel like normal application development.

---

# 7. DXE Driver Loading as a Bring-Up Technique

BootTo.NET dynamically loads DXE drivers from the EFI filesystem.

Relevant code:

- [ConsoleApp1/Program.EFI.cs](https://github.com/nifanfa/BootTo.NET/blob/master/ConsoleApp1/Program.EFI.cs)

The repository loads drivers for functionality including:

```text
DNS
HTTP
TLS
USB
mouse
audio
```

This is interesting for WitOS because it offers an optional **early bring-up path**.

## Possible WitOS use

During very early experiments, a firmware-hosted harness could temporarily use UEFI/DXE functionality to test:

```text
graphics
input
storage
network
TLS
```

before WitOS-native drivers exist.

For example:

```text
UEFI
 ↓
Wit Development Harness
 ├── GOP
 ├── UEFI filesystem
 ├── UEFI network
 └── UEFI input
```

This can accelerate experiments.

## Long-term rule

DXE must not become the final WitOS driver architecture.

The production direction remains:

```text
hardware
 ↓
UHI / low-level capabilities
 ↓
managed WitOS driver
 ↓
typed WitOS resource
```

---

# 8. Firmware as a Hardware-Personality Layer

BootTo.NET is useful as an existence proof for an idea close to the WitOS UHI concept.

UEFI already does something structurally similar:

```text
hardware-specific driver
       ↓
standard firmware protocol
       ↓
consumer
```

This does not mean UEFI itself is sufficient for WitOS.

It does suggest that the broader idea is practical:

> hardware-specific knowledge can terminate below a stable protocol boundary.

For WitOS, study UEFI/DXE protocol design for inspiration when refining:

```text
Universal Hardware Interface
device descriptors
firmware-backed providers
legacy compatibility backend
```

The goal is not to clone UEFI protocols, but to learn from an ecosystem that already uses standardized firmware-level interfaces.

---

# 9. Async and Task Scheduling

BootTo.NET contains a custom async/task scheduler suitable for an environment without a conventional operating system.

Study:

- [ConsoleApp1/TaskScheduler.cs](https://github.com/nifanfa/BootTo.NET/blob/master/ConsoleApp1/TaskScheduler.cs)
- [System/Threading/Tasks](https://github.com/nifanfa/BootTo.NET/tree/master/ConsoleApp1/System/Threading/Tasks)
- [Task.Delay.cs](https://github.com/nifanfa/BootTo.NET/blob/master/ConsoleApp1/System/Threading/Tasks/Task.Delay.cs)
- [ConsoleApp1/Runtime.cs](https://github.com/nifanfa/BootTo.NET/blob/master/ConsoleApp1/Runtime.cs)

## What to learn

Useful questions:

```text
How is async continuation scheduled without an OS?
How are timers represented?
How are locks implemented?
What minimum runtime services does async/await actually need?
Which assumptions from normal .NET immediately become visible?
```

This is especially relevant to WitOS M3.

## What not to adopt

WitOS must not redefine the semantics of:

```text
Task
Thread
ThreadPool
```

for ordinary .NET applications.

BootTo.NET's scheduler should therefore be treated as:

```text
implementation study
+
bootstrap lesson
+
test-case generator
```

not as the final WitOS scheduling API.

---

# 10. Runtime Validation Suite

One of the most valuable parts of the project is not the runtime itself but the set of features the author explicitly validates.

Study:

- [LanguageFeatureValidation.cs](https://github.com/nifanfa/BootTo.NET/blob/master/ConsoleApp1/LanguageFeatureValidation.cs)
- [GarbageCollectionValidation.cs](https://github.com/nifanfa/BootTo.NET/blob/master/ConsoleApp1/GarbageCollectionValidation.cs)
- [DateTimeValidation.cs](https://github.com/nifanfa/BootTo.NET/blob/master/ConsoleApp1/DateTimeValidation.cs)

These files are a useful checklist for WitOS M3/M6.

A WitOS managed-runtime conformance suite should test at least:

```text
allocation
GC
exceptions
finally
delegates
generics
interfaces
boxing/unboxing
static initialization
strings
arrays
DateTime
encoding
async/await
Task.Delay
locking
reflection where supported
P/Invoke/runtime exports
```

BootTo.NET has already exposed many of the edge cases that appear when C# is executed outside a conventional OS environment.

---

# 11. Custom CoreLib — Study It, Do Not Adopt It

Study:

- [ConsoleApp1/CoreLib.cs](https://github.com/nifanfa/BootTo.NET/blob/master/ConsoleApp1/CoreLib.cs)
- [ConsoleApp1/ConsoleApp1.csproj](https://github.com/nifanfa/BootTo.NET/blob/master/ConsoleApp1/ConsoleApp1.csproj)
- [Directory.Build.targets](https://github.com/nifanfa/BootTo.NET/blob/master/Directory.Build.targets)

BootTo.NET deliberately removes the standard reference assemblies and supplies its own implementations of types in `System`.

This is useful for understanding:

```text
the minimum runtime surface needed by C#
what compiler-generated code expects
what basic BCL pieces are required very early
```

But this is explicitly **not** the WitOS long-term path.

WitOS requirement:

```text
ordinary netX.0 application
+
standard BCL
+
upstream-compatible runtime
```

Therefore:

```text
BootTo.NET CoreLib
    → research reference only

WitOS
    → NativeAOT / CoreCLR port
```

---

# 12. IL2LLVM — Useful Comparison, Not Runtime Strategy

BootTo.NET compiles its IL with a custom compiler:

- [BootTo.NET/IL2LLVM](https://github.com/nifanfa/BootTo.NET/tree/master/IL2LLVM)
- [IL2LLVM README](https://github.com/nifanfa/BootTo.NET/blob/master/IL2LLVM/README.md)
- [Standalone IL2LLVM repository](https://github.com/nifanfa/IL2LLVM)

The pipeline is approximately:

```text
C#
 ↓
IL using custom CoreLib
 ↓
IL2LLVM
 ↓
LLVM
 ↓
native object
 ↓
EFI binary
```

## What is worth studying

- how IL metadata is translated into native structures;
- how runtime exports/imports are resolved;
- native object generation;
- multi-architecture LLVM targeting;
- what runtime support is required after AOT compilation.

## Why WitOS should not depend on it

WitOS has a stronger compatibility requirement:

```text
upstream .NET runtime
standard BCL
existing NuGet applications
```

The preferred route remains:

```text
NativeAOT for system components
CoreCLR for general applications
```

IL2LLVM is valuable as engineering reference and as an independent comparison point.

---

# 13. Native/Managed Runtime Boundary

Study:

- [EfiApplication/EfiApplication.c](https://github.com/nifanfa/BootTo.NET/blob/master/EfiApplication/EfiApplication.c)
- [NativeLib/Runtime.asm](https://github.com/nifanfa/BootTo.NET/blob/master/NativeLib/Runtime.asm)
- [NativeLib/CpuHelpers.c](https://github.com/nifanfa/BootTo.NET/blob/master/NativeLib/CpuHelpers.c)
- [ConsoleApp1/NativeLib.cs](https://github.com/nifanfa/BootTo.NET/blob/master/ConsoleApp1/NativeLib.cs)
- [ConsoleApp1/Runtime.cs](https://github.com/nifanfa/BootTo.NET/blob/master/ConsoleApp1/Runtime.cs)

Questions worth studying:

```text
How small can the native substrate be?
How are managed exports exposed to native code?
How are native symbols imported from C#?
What assembly/runtime helpers are unavoidable?
What ABI assumptions leak into managed code?
```

These are directly relevant to the WitOS rule:

> keep native/architecture-specific code narrow and move upward into managed code as early as possible.

---

# 14. Graphics

BootTo.NET provides managed graphics code and uses UEFI GOP.

Study:

- [System/Drawing/Graphics.cs](https://github.com/nifanfa/BootTo.NET/blob/master/ConsoleApp1/System/Drawing/Graphics.cs)
- [ConsoleApp1/Program.cs](https://github.com/nifanfa/BootTo.NET/blob/master/ConsoleApp1/Program.cs)
- [ConsoleApp1/Program.EFI.cs](https://github.com/nifanfa/BootTo.NET/blob/master/ConsoleApp1/Program.EFI.cs)
- [NES/GameRender.cs](https://github.com/nifanfa/BootTo.NET/blob/master/NES/GameRender.cs)

## Potential WitOS lessons

For early graphical bring-up:

```text
UEFI GOP framebuffer
      ↓
simple managed framebuffer API
      ↓
test graphics/input
```

could be useful before `virtio-gpu` and the WitOS compositor exist.

This can provide an early visual diagnostic mode.

## Long-term distinction

WitOS graphics must evolve toward:

```text
display/GPU resource
      ↓
presentation service
      ↓
compositor
      ↓
application surfaces
```

not remain a direct GOP framebuffer application.

---

# 15. Network Stack and UEFI Networking

Study:

- [System/Net/Sockets/Socket.cs](https://github.com/nifanfa/BootTo.NET/blob/master/ConsoleApp1/System/Net/Sockets/Socket.cs)
- [ConsoleApp1/Program.EFI.cs](https://github.com/nifanfa/BootTo.NET/blob/master/ConsoleApp1/Program.EFI.cs)
- [ConsoleApp1/FtpServer.cs](https://github.com/nifanfa/BootTo.NET/blob/master/ConsoleApp1/FtpServer.cs)
- [uefi-cs/efi_pxe.cs](https://github.com/nifanfa/BootTo.NET/blob/master/uefi-cs/efi_pxe.cs)

BootTo.NET demonstrates that a surprisingly rich network environment can be assembled through firmware protocols.

## WitOS use

This is useful in two ways:

1. **Temporary bring-up:** test high-level networking before a WitOS-native network stack exists.
2. **Compatibility-backend research:** explore whether firmware/network protocols can form part of a legacy UHI backend for constrained environments.

## Not a final architecture

WitOS M7 still requires a normal operating-system network path and standard `.NET System.Net` semantics independent of UEFI.

---

# 16. File I/O Compatibility Layer

Study:

- [ConsoleApp1/NativeFileIO.cs](https://github.com/nifanfa/BootTo.NET/blob/master/ConsoleApp1/NativeFileIO.cs)

This code is particularly useful as an example of adapting a constrained environment to interfaces expected by existing native software such as Doom or Quake.

Lessons relevant to WitOS:

```text
compatibility adapters can be narrow
existing applications often need fewer primitives than expected
buffering strategy can hide expensive underlying operations
POSIX/C-like file operations can be layered over higher-level managed APIs
```

This is conceptually similar to WitOS compatibility views:

```text
native compatibility API
      ↓
WitOS storage/file capability
```

The implementation itself should not define WitOS filesystem semantics.

---

# 17. Complex Applications as Integration Tests

BootTo.NET runs much more than "Hello World".

Repository areas worth inspecting:

- [doomgeneric](https://github.com/nifanfa/BootTo.NET/tree/master/doomgeneric)
- [quakegeneric](https://github.com/nifanfa/BootTo.NET/tree/master/quakegeneric)
- [NES](https://github.com/nifanfa/BootTo.NET/tree/master/NES)

This is an important project-management lesson.

A runtime can pass many small tests and still fail when confronted with a large real program.

WitOS should similarly use representative integration workloads:

```text
M3:
non-trivial NativeAOT service

M6:
real console application
Roslyn
NUnit/xUnit
Math.NET

M7:
ASP.NET Core / Kestrel

M9:
existing Avalonia application
```

Real applications should complement synthetic conformance tests.

---

# 18. BootTo.NET as a Development Harness

A useful idea for WitOS is a temporary project such as:

```text
WitOS.Tools.FirmwareHarness
```

Architecture:

```text
UEFI/EDK2
    ↓
C# firmware harness
    ├── GOP graphics
    ├── input
    ├── EFI filesystem
    └── EFI network
```

Purpose:

```text
prototype managed APIs
validate C# runtime assumptions
exercise QEMU
build diagnostics
experiment before native drivers exist
```

The harness must be explicitly treated as temporary development infrastructure, not as the WitOS kernel/runtime architecture.

---

# 19. Recommended Immediate Experiments

## Experiment A — WitBoot.Uefi

Build the smallest EFI application that:

```text
boots in QEMU
reads the UEFI memory map
gets framebuffer information
loads a separate kernel image
constructs WitBootInfo
calls ExitBootServices
jumps to kernel entry
```

References:

- [EfiApplication.c](https://github.com/nifanfa/BootTo.NET/blob/master/EfiApplication/EfiApplication.c)
- [uefi-cs](https://github.com/nifanfa/BootTo.NET/tree/master/uefi-cs)
- [Program.EFI.cs](https://github.com/nifanfa/BootTo.NET/blob/master/ConsoleApp1/Program.EFI.cs)

This should directly contribute to WitOS M0.

---

## Experiment B — Reproduce the QEMU Developer Loop

Create:

```text
build
 ↓
disk/image generation
 ↓
QEMU startup
 ↓
serial capture
 ↓
automatic exit
```

References:

- [Qemu.targets](https://github.com/nifanfa/BootTo.NET/blob/master/Qemu.targets)
- [Directory.Build.targets](https://github.com/nifanfa/BootTo.NET/blob/master/Directory.Build.targets)

Goal:

```text
one command
→ boot WitOS
→ verify expected serial marker
→ return CI exit code
```

---

## Experiment C — Managed Runtime Checklist

Convert BootTo.NET's validation ideas into a WitOS conformance suite.

References:

- [LanguageFeatureValidation.cs](https://github.com/nifanfa/BootTo.NET/blob/master/ConsoleApp1/LanguageFeatureValidation.cs)
- [GarbageCollectionValidation.cs](https://github.com/nifanfa/BootTo.NET/blob/master/ConsoleApp1/GarbageCollectionValidation.cs)
- [DateTimeValidation.cs](https://github.com/nifanfa/BootTo.NET/blob/master/ConsoleApp1/DateTimeValidation.cs)

Target:

```text
tests/
    ManagedRuntime/
        Allocation
        GC
        Exceptions
        Generics
        Delegates
        Async
        Timers
        Synchronization
        DateTime
        Encoding
```

---

## Experiment D — Firmware-Backed Graphics Diagnostic

Use UEFI GOP only for early diagnostics.

References:

- [Graphics.cs](https://github.com/nifanfa/BootTo.NET/blob/master/ConsoleApp1/System/Drawing/Graphics.cs)
- [Program.cs](https://github.com/nifanfa/BootTo.NET/blob/master/ConsoleApp1/Program.cs)

Target:

```text
WitOS boot
 ↓
framebuffer acquired by boot adapter
 ↓
kernel/system diagnostic output
```

This is not the future compositor.

---

# 20. Mapping to WitOS Milestones

| WitOS milestone | BootTo.NET material worth studying |
|---|---|
| M0 — Boot | EFI entry, `uefi-cs`, QEMU/EDK2 tooling |
| M1 — Kernel Core | Little direct reuse; only native/managed ABI ideas |
| M2 — Isolated Execution | Little direct reuse; WitOS-specific |
| M3 — Managed System | runtime boundary, async scheduler, language/GC validation |
| M4 — UHI / Devices | DXE/UEFI protocols as reference and temporary compatibility backend |
| M5 — Storage | UEFI filesystem only as bootstrap/reference; WitOS managed storage remains separate |
| M6 — Standard .NET | BootTo.NET useful mainly as a list of runtime pitfalls; do not adopt custom CoreLib |
| M7 — Networking | UEFI networking/DXE for experiments; `Socket` compatibility lessons |
| M9 — Graphics | GOP/framebuffer for early bring-up; not compositor architecture |

---

# 21. What We Should Potentially Reuse Directly

Subject to licensing clarification, candidates include:

```text
UEFI structure/protocol definitions
small portions of boot-entry patterns
QEMU/MSBuild workflow ideas
test scenarios
diagnostic patterns
```

Even here, prefer adapting ideas into WitOS-owned abstractions rather than creating permanent dependencies.

---

# 22. What We Should Use as Inspiration Only

```text
DXE driver loading
firmware networking
GOP graphics
custom Task scheduler
native file compatibility layer
custom GC/runtime support
```

These are valuable demonstrations of how to bootstrap capabilities in a constrained environment.

They should inform WitOS, not define it.

---

# 23. What We Should Explicitly Not Adopt

```text
custom CoreLib as WitOS BCL
custom System.* semantics
IL2LLVM as the standard WitOS runtime
UEFI Boot Services as permanent OS services
DXE as the normal WitOS driver system
firmware filesystem as the WitOS storage architecture
single-address-space firmware execution as the WitOS security model
```

These conflict with core WitOS goals.

---

# 24. Licensing Warning

At the time this document was prepared, the GitHub repository metadata for:

- [BootTo.NET](https://github.com/nifanfa/BootTo.NET)
- [IL2LLVM](https://github.com/nifanfa/IL2LLVM)

did not expose a repository license.

Therefore, until the author clarifies licensing:

```text
study code
learn from architecture
reimplement ideas independently
```

is the safer research workflow, while:

```text
copy source into WitOS
ship derived code
create permanent source dependency
```

should be avoided.

If direct reuse becomes attractive, contact the author and request an explicit license.

---

# 25. Suggested Reading Order

1. [README](https://github.com/nifanfa/BootTo.NET/blob/master/README.md) — overall project and workflow.
2. [EfiApplication.c](https://github.com/nifanfa/BootTo.NET/blob/master/EfiApplication/EfiApplication.c) — native-to-managed EFI handoff.
3. [Program.EFI.cs](https://github.com/nifanfa/BootTo.NET/blob/master/ConsoleApp1/Program.EFI.cs) — UEFI initialization and DXE loading.
4. [uefi-cs](https://github.com/nifanfa/BootTo.NET/tree/master/uefi-cs) — UEFI protocol bindings.
5. [Directory.Build.targets](https://github.com/nifanfa/BootTo.NET/blob/master/Directory.Build.targets) and [Qemu.targets](https://github.com/nifanfa/BootTo.NET/blob/master/Qemu.targets) — build/QEMU loop.
6. [TaskScheduler.cs](https://github.com/nifanfa/BootTo.NET/blob/master/ConsoleApp1/TaskScheduler.cs) — async operation without a conventional OS.
7. [Runtime.cs](https://github.com/nifanfa/BootTo.NET/blob/master/ConsoleApp1/Runtime.cs) and [NativeLib](https://github.com/nifanfa/BootTo.NET/tree/master/NativeLib) — managed/native substrate.
8. [CoreLib.cs](https://github.com/nifanfa/BootTo.NET/blob/master/ConsoleApp1/CoreLib.cs) — runtime assumptions; useful as research, not as WitOS template.
9. [LanguageFeatureValidation.cs](https://github.com/nifanfa/BootTo.NET/blob/master/ConsoleApp1/LanguageFeatureValidation.cs), [GarbageCollectionValidation.cs](https://github.com/nifanfa/BootTo.NET/blob/master/ConsoleApp1/GarbageCollectionValidation.cs), [DateTimeValidation.cs](https://github.com/nifanfa/BootTo.NET/blob/master/ConsoleApp1/DateTimeValidation.cs) — turn edge cases into WitOS tests.
10. [IL2LLVM](https://github.com/nifanfa/IL2LLVM) — study AOT implementation techniques while keeping the WitOS runtime strategy independent.

---

# 26. Architectural Takeaway

BootTo.NET is valuable to WitOS primarily because it demonstrates that the path:

```text
UEFI
 ↓
C#
 ↓
graphics / input / network / filesystem
 ↓
non-trivial applications
```

can be made surprisingly short.

WitOS should exploit that knowledge to accelerate bring-up without confusing the bootstrap mechanism with the final operating-system architecture.

The recommended interpretation is:

> **BootTo.NET is a valuable implementation laboratory for UEFI-hosted C# and early platform bring-up, not a template for the WitOS runtime, kernel, or compatibility model.**

The most useful concrete lesson is:

> **Use existing firmware and tooling aggressively to reach a running system early; replace firmware-hosted capabilities with WitOS-native mechanisms only when the relevant architectural layer is ready.**
