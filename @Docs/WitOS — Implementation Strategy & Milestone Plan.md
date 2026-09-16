# WitOS  
## Implementation Strategy & Milestone Plan  
### Draft v0.1

## 1. Status

Draft.

This document defines the initial implementation strategy for WitOS.

It does not define a new operating-system subsystem.

Its purpose is to translate the architecture described in RFC 0001–0010 into an executable development sequence in which WitOS becomes runnable as early as possible and remains runnable throughout development.

The central principle is:

> **WitOS must be built as a sequence of vertical, bootable slices rather than as a collection of independently completed subsystems.**

A second principle is:

> **Every major milestone must produce a demonstrably more capable runnable WitOS image.**

---

# 2. Primary Development Objective

The first objective is not:

```text
complete kernel
complete driver model
complete filesystem
complete GUI
complete package manager
```

The first objective is:

```text
create something called WitOS
that boots independently
inside a virtual machine
```

and then progressively expand it.

The initial target environment is:

```text
QEMU
x86-64
```

followed early by:

```text
QEMU ARM64
```

to detect architecture assumptions.

---

# 3. The Runnable-System Rule

The primary branch should remain bootable.

Conceptually:

```text
main
 ↓
build
 ↓
WitOS image
 ↓
QEMU
 ↓
observable system
```

A subsystem is not considered integrated merely because its unit tests pass independently.

It should eventually contribute to the runnable system.

---

# 4. Vertical Slices

Development proceeds vertically.

Bad development sequence:

```text
write scheduler
write filesystem
write network stack
write package manager
write shell
write drivers
eventually connect them
```

Preferred sequence:

```text
boot
 ↓
memory
 ↓
execution
 ↓
isolation
 ↓
managed code
 ↓
device
 ↓
storage
 ↓
CoreCLR
 ↓
network
 ↓
applications
 ↓
graphics
 ↓
distribution
```

Each stage extends a working system.

---

# 5. No Dark Months

A project stage should not disappear into months of work with no observable progress in the bootable image.

Each milestone should produce new visible behavior.

For example:

```text
M0 → Hello
M1 → multitasking
M2 → isolated process
M3 → managed C#
M4 → device discovered
M5 → file read
M6 → normal .NET application
M7 → HTTP
M8 → signed application
M9 → graphical application
M10 → distributed resource
```

This is both an engineering and project-management requirement.

---

# 6. Initial Scope

The initial implementation intentionally excludes broad hardware compatibility.

The first complete hardware platform is:

```text
QEMU virtual machine
```

with a deliberately limited virtual device set.

Likely initial devices:

```text
serial console
virtio-block
virtio-net
virtio-input
virtio-gpu
```

Real PC hardware support comes later.

---

# 7. Boot Strategy

WitOS should not initially implement its own bootloader.

Use an existing mature boot path such as:

```text
Limine
```

or:

```text
UEFI boot adapter
```

provided that the external boot mechanism is hidden behind a WitOS-owned boot contract.

Conceptually:

```text
Firmware / Bootloader
        ↓
Boot Adapter
        ↓
WitBootInfo
        ↓
Kernel
```

---

# 8. WitBootInfo

The kernel should receive a stable internal boot structure.

Conceptually:

```c
struct WitBootInfo
{
    MemoryMap memory;
    CpuInfo bootstrapCpu;
    BootModule[] modules;
    ConsoleInfo console;
};
```

Exact ABI is deferred to the kernel RFC.

The important architectural rule is:

> The kernel must depend on the WitOS boot contract, not directly on Limine, UEFI, or another bootloader-specific API.

---

# 9. Future Boot Replacement

The initial boot adapter may later be replaced by:

```text
native WitOS firmware
Universal Hardware Interface firmware
hypervisor-native boot
```

without redesigning the kernel.

This follows RFC 0007.

---

# 10. Development Tracks

The project should run two related tracks.

## Track A — Native WitOS

```text
QEMU
 ↓
kernel
 ↓
managed system
 ↓
complete OS
```

## Track B — Hosted WitOS APIs

```text
Windows / Linux
      ↓
OutWit.OS.*
```

The two tracks share contracts and conformance tests.

---

# 11. Purpose of the Hosted Track

The hosted track allows high-level resource APIs to be tested before the native OS supports every feature.

For example:

```text
OutWit.OS.Execution
    ├── Windows
    ├── Linux
    └── WitOS
```

This allows application-level design validation early.

---

# 12. Native Track Has Priority for System Identity

Hosted providers are important, but WitOS must become a real independently bootable system early.

The project should not spend years as:

```text
WitOS abstractions running on Linux
```

with no WitOS kernel.

The first bootable artifact is therefore an early milestone.

---

# 13. Milestone Overview

The initial sequence is:

```text
M0 — Boot
M1 — Kernel Core
M2 — Isolated Execution
M3 — Managed System
M4 — UHI & Device Manager
M5 — Persistent Storage
M6 — Standard .NET
M7 — Networking
M8 — Application & Trust Model
M9 — Graphical Presentation
M10 — Distributed Resources
```

Each milestone has a strict Definition of Done.

---

# 14. M0 — Boot

## Goal

Create the first independently bootable WitOS image.

Expected output:

```text
WitOS 0.0.1
CPU: x86_64
Memory: 2048 MB

Kernel initialized.
Hello from WitOS.
```

---

# 15. M0 Required Components

Minimum components:

```text
build system
boot adapter
kernel entry point
serial output
basic panic mechanism
QEMU runner
image builder
CI boot test
```

---

# 16. M0 Non-Goals

M0 does not require:

```text
scheduler
user mode
filesystem
network
managed runtime
GUI
drivers beyond boot console
```

---

# 17. M0 Definition of Done

M0 is complete when:

1. a WitOS image is produced automatically;
2. QEMU can boot it without interactive setup;
3. the kernel writes a deterministic boot message to serial output;
4. QEMU can exit under automated control;
5. CI can distinguish successful boot from failed boot.

---

# 18. M0 Deliverable

Example:

```text
WitOS-x64-debug.img
```

or equivalent bootable artifact.

This is the first real WitOS release artifact.

---

# 19. M1 — Kernel Core

## Goal

Implement the minimum kernel mechanisms necessary for concurrent execution.

Core concepts:

```text
PhysicalMemory
AddressSpace
ExecutionContext
Interrupt
Timer
Event
Capability
Channel
```

Not all need complete final semantics yet.

---

# 20. M1 Memory

Required:

```text
physical page allocator
virtual memory mapping
kernel address space
basic protection flags
page-fault handling
```

---

# 21. M1 Interrupts

Required:

```text
interrupt initialization
timer interrupt
basic exception handling
```

Architecture-specific implementation remains below generic kernel contracts.

---

# 22. M1 Scheduler

The first scheduler may be intentionally simple.

It only needs to demonstrate:

```text
ExecutionContext A
ExecutionContext B
```

being independently scheduled.

Expected visible output:

```text
A: 1
B: 1
A: 2
B: 2
```

---

# 23. M1 SMP

M1 does not require full multi-core scheduling.

The first version may use only the bootstrap processor.

However, implementation should avoid unnecessary assumptions that only one processor can ever exist.

---

# 24. M1 Definition of Done

M1 is complete when:

```text
memory allocation works
virtual memory works
timer interrupts work
at least two execution contexts are scheduled
kernel faults produce useful diagnostics
automated tests run in QEMU
```

---

# 25. M2 — Isolated Execution

## Goal

Run code outside the kernel's privileged address space.

This is the first validation of the WitOS isolation model.

---

# 26. First User/System Component

The initial executable component may be loaded directly as a boot module.

For example:

```text
bootloader
    ├── kernel
    └── init.bin
```

A filesystem is not yet necessary.

---

# 27. M2 Address Spaces

The kernel creates:

```text
Kernel Address Space

Init Address Space
```

with explicit mappings.

The component must not have unrestricted access to kernel memory.

---

# 28. Minimal Kernel ABI

Initial calls may include:

```text
Exit
Yield
Wait
MapMemory
ChannelSend
ChannelReceive
CapabilityClose
```

Exact ABI is deferred.

---

# 29. Capability Handles

The first user/system component should receive resources through capability handles rather than globally privileged IDs where practical.

For example:

```text
console capability
initial channel
memory capability
```

---

# 30. Kernel/User Boundary

The architecture should establish early that:

```text
managed safety
≠
kernel security boundary
```

Address-space isolation remains fundamental.

---

# 31. M2 Definition of Done

M2 is complete when:

1. the kernel creates an isolated address space;
2. a separate executable component runs inside it;
3. the component communicates with the kernel only through defined ABI operations;
4. invalid memory access cannot silently corrupt the kernel;
5. process/component termination is handled cleanly.

---

# 32. M3 — Managed System

## Goal

Run the first managed C# component directly on WitOS.

The initial runtime target should be:

```text
NativeAOT
```

rather than CoreCLR.

---

# 33. Why NativeAOT First

NativeAOT provides:

```text
managed C# implementation
ahead-of-time executable
smaller runtime integration surface
deterministic system-component deployment
```

without requiring the complete CoreCLR platform abstraction layer immediately.

---

# 34. First Managed Program

Target:

```csharp
internal static class Program
{
    static void Main()
    {
        Console.WriteLine("Hello from managed WitOS");
    }
}
```

Expected output:

```text
WitOS Kernel

Starting Init...

Hello from managed WitOS
```

---

# 35. Managed Console Mapping

The first managed `Console` may map onto:

```text
serial terminal capability
```

through a minimal platform adapter.

---

# 36. Managed System Direction

M3 should establish the long-term implementation direction:

```text
Native code:
    kernel
    architecture layer
    unavoidable runtime substrate

Managed C#:
    system services
    device manager
    resource manager
    drivers where practical
    storage services
    shell
```

---

# 37. M3 Definition of Done

M3 is complete when:

1. a C# NativeAOT system component builds for WitOS;
2. it launches in an isolated execution context;
3. it can write to the terminal;
4. it can use basic memory/runtime functionality;
5. failure does not automatically crash the kernel.

---

# 38. Major Architecture Gate A

At the end of M3, the project should explicitly review whether the core hypothesis holds:

> Can a small native kernel support a predominantly managed WitOS system layer without forcing invasive runtime modifications?

If not, architecture should be revised before large subsystems are built.

---

# 39. M4 — UHI & Device Manager

## Goal

Introduce the hardware model defined by RFC 0007.

QEMU hardware should be presented through UHI rather than leaking QEMU-specific concepts throughout the OS.

---

# 40. UHI Backend

First backend:

```text
Uhi.Qemu
```

or equivalent compatibility adapter.

It translates available virtual hardware into UHI descriptors and capabilities.

---

# 41. Device Manager

Create the managed:

```text
System.DeviceManager
```

responsible for:

```text
enumeration
driver matching
driver activation
capability delegation
device lifecycle
```

---

# 42. First Device

The first meaningful device should probably be:

```text
virtio-block
```

because it unlocks persistent storage.

---

# 43. Driver Authority

The driver receives narrow capabilities such as:

```text
device configuration
MMIO or transport access
interrupt
DMA
reset
```

rather than ambient machine-wide hardware authority.

---

# 44. Device Discovery Output

Example:

```text
Device discovered:
    Class: BlockStorageController
    Provider: Virtio
    DeviceId: ...
```

This should be visible through diagnostics.

---

# 45. M4 Definition of Done

M4 is complete when:

1. hardware appears as UHI descriptors;
2. Device Manager discovers the virtual block device;
3. a managed or minimally native driver is selected;
4. the driver receives narrow hardware capabilities;
5. the driver can issue a basic I/O operation.

---

# 46. M5 — Persistent Storage

## Goal

Load and persist ordinary files from a virtual disk.

---

# 47. No Native WitOS Filesystem Yet

M5 should not require inventing a new filesystem.

Initial choices may include:

```text
simple existing filesystem
minimal filesystem implementation
initrd/RAM filesystem plus basic disk filesystem
```

The objective is validating the storage stack, not filesystem innovation.

---

# 48. Storage Path

Conceptually:

```text
virtio block device
      ↓
block driver
      ↓
storage service
      ↓
filesystem/namespace provider
      ↓
file API
```

---

# 49. M5 Minimum Operations

Required:

```text
open
read
write
flush
create
directory enumeration
```

---

# 50. Program Loading from Storage

By the end of M5, a program should be loadable from:

```text
/Programs/Hello/
```

or equivalent namespace.

The bootloader should no longer be required to preload every executable component.

---

# 51. Persistence Test

Example:

```text
boot
write /data/test.txt
shutdown
boot again
read /data/test.txt
```

must succeed.

---

# 52. M5 Definition of Done

M5 is complete when:

1. a virtio-backed storage device is usable;
2. files survive reboot;
3. directories can be enumerated;
4. an executable can be loaded from storage;
5. storage failure produces meaningful diagnostics.

---

# 53. M6 — Standard .NET

## Goal

Run ordinary standard .NET applications that were not written specifically for WitOS.

This is the central compatibility milestone.

---

# 54. CoreCLR

M6 introduces:

```text
CoreCLR
JIT
GC
standard BCL
```

through a WitOS platform port.

The objective is upstream-compatible .NET rather than a WitOS-specific CLR fork.

---

# 55. Compatibility Requirement

A normal application:

```csharp
Console.WriteLine(Environment.Version);

var text =
    await File.ReadAllTextAsync("test.txt");

await Task.WhenAll(
    Enumerable.Range(0, 100)
        .Select(async i =>
        {
            await Task.Delay(10);
            Console.WriteLine(i);
        }));
```

should run without:

```csharp
#if WITOS
```

---

# 56. Standard Semantics

M6 must preserve ordinary semantics of:

```text
Task
Thread
ThreadPool
GC
exceptions
timers
FileStream
reflection
assembly loading
synchronization
```

within the supported implementation set.

---

# 57. Compatibility Test Suite

A dedicated suite should exist before CoreCLR support is complete.

Suggested structure:

```text
tests/
    DotNetCompatibility/
        Console/
        Tasks/
        Threads/
        Timers/
        Exceptions/
        Reflection/
        FileIO/
        AssemblyLoading/
        GC/
        Synchronization/
```

---

# 58. Cross-System Comparison

The same tests should run on:

```text
Windows
Linux
WitOS
```

where appropriate.

Differences should be intentional and documented.

---

# 59. Representative Libraries

Gradually test:

```text
NUnit
xUnit
Math.NET
MessagePack
MemoryPack
Roslyn
```

followed later by larger frameworks.

---

# 60. M6 Definition of Done

M6 is complete when:

1. CoreCLR starts on WitOS;
2. ordinary `netX.0` console applications execute;
3. standard async/task behavior works;
4. file I/O works through BCL APIs;
5. representative NuGet libraries pass tests;
6. the application source contains no WitOS-specific changes.

---

# 61. Major Architecture Gate B

M6 is the point at which WitOS may credibly claim:

> **WitOS runs .NET.**

Before M6, managed execution exists, but full standard .NET compatibility remains incomplete.

---

# 62. M7 — Networking

## Goal

Support normal .NET networking.

---

# 63. Network Device

Introduce:

```text
virtio-net
```

through the same UHI/Device Manager architecture.

---

# 64. Network Stack

Initial network support should eventually provide:

```text
Ethernet-like link
IP
TCP
UDP
DNS
TLS integration
```

Exact implementation layering will be defined separately.

---

# 65. Socket Compatibility

Standard APIs should work:

```csharp
Socket
TcpClient
UdpClient
NetworkStream
HttpClient
```

---

# 66. First Network Test

A key test:

```csharp
using var client = new HttpClient();

var text =
    await client.GetStringAsync(
        "https://example.com");

Console.WriteLine(text);
```

---

# 67. ASP.NET Core Gate

A later M7 criterion should be launching an ordinary:

```text
ASP.NET Core / Kestrel
```

application.

This provides a powerful real-world compatibility test.

---

# 68. M7 Definition of Done

M7 is complete when:

1. virtio-net is functional;
2. TCP and UDP operate;
3. DNS works;
4. `HttpClient` can perform HTTPS requests;
5. an ordinary ASP.NET Core application can run with minimal or no WitOS-specific source changes.

---

# 69. M8 — Application & Trust Model

## Goal

Implement the first useful subset of RFC 0003 and RFC 0010.

---

# 70. Application Directory

An application should run directly from:

```text
/Programs/MyApplication/
```

without mandatory registration.

---

# 71. Optional Manifest

Introduce:

```text
witos.app.json
```

or an equivalent manifest format.

It remains optional for direct execution.

---

# 72. Application Identity

Manifest-enabled applications gain:

```text
ApplicationId
Version
Publisher
EntryPoint
```

and later richer metadata.

---

# 73. First Signing Tools

Initial tooling:

```text
wit identity create
wit sign
wit verify
```

---

# 74. Console Trust Presentation

Before GUI exists, trust state can be visible through CLI.

Example:

```text
> apps

SIGNED     OutWit.Compiler
UNSIGNED   TestUtility
INVALID    ModifiedTool
```

This implements the semantics of RFC 0010 before graphical badges exist.

---

# 75. Shell Independence

The trust-state API should already expose:

```text
Unsigned
SignedValid
SignedInvalid
```

so the future shell only needs to present existing system semantics.

---

# 76. M8 Definition of Done

M8 is complete when:

1. a directory can be copied into arbitrary storage and executed;
2. a manifest is optional;
3. signed applications can be verified;
4. unsigned applications remain runnable;
5. trust state is visibly reported;
6. modifying signed content changes verification state.

---

# 77. M9 — Graphical Presentation

## Goal

Run a graphical .NET application.

This milestone does not yet require a complete desktop shell.

---

# 78. Graphics Device

Introduce:

```text
virtio-gpu
```

or another controlled QEMU graphics device.

---

# 79. Graphics Stack

Minimum path:

```text
virtio-gpu
    ↓
display/graphics service
    ↓
compositor
    ↓
application surface
```

---

# 80. Input

Add enough:

```text
keyboard
pointer
basic focus
```

to operate a graphical application.

---

# 81. No Desktop Required

M9 does not require:

```text
taskbar
start menu
file manager
notifications
settings application
```

A single graphical application may occupy the display.

---

# 82. Avalonia

A key target is a WitOS Avalonia backend.

Conceptually:

```text
ordinary Avalonia app
       ↓
Avalonia.WitOS
       ↓
OutWit.OS.Presentation
       ↓
compositor
```

---

# 83. Graphical Compatibility Gate

The ideal test is:

> An existing simple Avalonia application, originally developed without WitOS-specific UI code, runs on WitOS.

---

# 84. Signature Badge Preparation

Once a launcher or basic shell appears, RFC 0010 trust-state metadata should be presented graphically.

Unsigned applications receive the reference unsigned marker:

```text
red circular corner badge
```

The shell does not recompute trust itself.

---

# 85. M9 Definition of Done

M9 is complete when:

1. graphical output works;
2. compositor displays application surfaces;
3. keyboard/pointer input works;
4. a simple Avalonia application runs;
5. graphical application lifecycle is integrated with WitOS.

---

# 86. M10 — Distributed Resources

## Goal

Demonstrate the architectural feature that distinguishes WitOS from a conventional new desktop OS.

Use at least two WitOS instances or one WitOS instance plus a compatible hosted provider.

---

# 87. First Distributed Demonstration

Example:

```text
VM A
    application UI

VM B
    compute resource
```

The application on A acquires compute from B through the WitOS resource model.

---

# 88. Remote Resource Semantics

The resource must expose:

```text
locality
latency
availability
capability
```

rather than pretending the remote resource is physically local.

---

# 89. Storage + Compute Demonstration

Possible later scenario:

```text
UI      → VM A
Compute → VM B
Storage → VM C
```

while remaining one logical application workflow.

---

# 90. Presentation Handoff

A further demonstration may move presentation between endpoints while execution remains elsewhere.

---

# 91. M10 Definition of Done

M10 is complete when:

1. at least two resource domains communicate;
2. a remote resource can be discovered;
3. authority can be delegated;
4. an application can acquire and use a remote compute/storage resource;
5. locality and failure remain observable;
6. no application-level OS-name branching is required.

---

# 92. What M10 Proves

M10 should demonstrate that WitOS is not merely:

```text
another kernel
+
another desktop
```

but a different resource/execution platform.

---

# 93. Development Repository

A possible initial repository structure:

```text
WitOS/
│
├── docs/
│   ├── rfc/
│   └── implementation/
│
├── src/
│   ├── Kernel/
│   ├── Kernel.Arch.X64/
│   ├── Kernel.Arch.Arm64/
│   │
│   ├── Uhi/
│   ├── Uhi.Qemu/
│   │
│   ├── System.Init/
│   ├── System.DeviceManager/
│   ├── System.ResourceManager/
│   │
│   ├── Drivers.Virtio/
│   │
│   ├── OutWit.OS.Resources/
│   ├── OutWit.OS.Execution/
│   ├── OutWit.OS.Communication/
│   ├── OutWit.OS.Storage/
│   └── OutWit.OS.Presentation/
│
├── tests/
│   ├── Kernel/
│   ├── System/
│   ├── Conformance/
│   └── DotNetCompatibility/
│
└── tools/
    ├── ImageBuilder/
    ├── QemuRunner/
    ├── TestRunner/
    └── WitCli/
```

Exact project boundaries may evolve.

---

# 94. Developer Tooling Language

Development tools such as:

```text
image builder
QEMU launcher
log parser
test orchestrator
package builder
signing utility
```

should be ordinary C#/.NET tools unless another language has a compelling technical advantage.

---

# 95. One-Command Development Loop

The project should provide a command equivalent to:

```text
dotnet run --project tools/QemuRunner
```

that performs:

```text
build
 ↓
create image
 ↓
start QEMU
 ↓
capture serial
 ↓
run tests
 ↓
exit
```

---

# 96. Headless by Default

Automated testing should run QEMU without graphical interaction whenever possible.

Serial output is the canonical early diagnostic/test channel.

---

# 97. Machine-Readable Test Protocol

Early WitOS may emit lines such as:

```text
[TEST-BEGIN] Memory.BasicAllocation
[TEST-PASS] Memory.BasicAllocation

[TEST-BEGIN] Scheduler.Switch
[TEST-PASS] Scheduler.Switch
```

The host runner interprets them.

---

# 98. Automated VM Exit

The test environment should provide a controlled way for WitOS to signal:

```text
success
failure
panic
timeout
```

to QEMU/host CI.

---

# 99. CI Requirement

Every commit to the primary branch should ideally validate:

```text
build tools
build kernel
build image
boot QEMU
run current conformance tests
```

---

# 100. Boot Regression Is Release-Blocking

A change that prevents WitOS from booting or completing baseline tests should block integration.

---

# 101. Deterministic Test Environment

QEMU configuration should be version-controlled.

For example:

```text
CPU model
memory
virtual devices
disk image
network mode
```

should not depend on an individual developer workstation.

---

# 102. Snapshot Testing

VM snapshots may later accelerate test cycles.

They should remain an optimization, not a requirement for correctness.

---

# 103. Diagnostics From Day One

Even M0 should provide:

```text
serial logging
panic message
build/version identifier
```

Later add:

```text
stack trace
memory map dump
capability diagnostics
device graph
```

---

# 104. Version Information

Every boot should identify:

```text
WitOS version
git commit
build configuration
architecture
```

to avoid ambiguous debugging.

---

# 105. Hosted API Conformance

High-level resource APIs should have provider-independent tests.

For example:

```text
ExecutionConformanceTests
```

run against:

```text
WindowsExecutionProvider
LinuxExecutionProvider
WitOSExecutionProvider
```

where possible.

---

# 106. Provider Honesty

Hosted implementations must report weaker guarantees honestly.

For example:

```text
ExclusivePhysicalCore:
    WitOS   → Supported
    Linux   → BestEffort
    Windows → BestEffort
```

depending on implementation.

---

# 107. Do Not Implement Everything Early

The following should explicitly be deferred during early milestones.

---

# 108. Deferred: Native Filesystem

Do not make a new WitOS filesystem a prerequisite for M0–M8.

An existing/simple filesystem is sufficient.

---

# 109. Deferred: Broad Hardware Support

Do not initially support:

```text
arbitrary PC hardware
Wi-Fi chipsets
consumer GPUs
USB ecosystem
audio hardware catalog
```

QEMU and a small controlled hardware set come first.

---

# 110. Deferred: Application Store

The store is irrelevant to the first operating-system milestones.

Signing and directory execution come first.

---

# 111. Deferred: Full Desktop Shell

Do not spend early development time implementing:

```text
taskbar
launcher animations
settings application
file manager
desktop customization
```

before graphical application compatibility exists.

---

# 112. Deferred: Custom Browser

A browser is an application ecosystem problem, not part of early OS construction.

---

# 113. Deferred: Advanced Distributed Storage

M10 needs enough distribution to demonstrate the model.

It does not initially require a production distributed filesystem.

---

# 114. Deferred: Native Firmware

Initial WitOS may boot through existing firmware/boot mechanisms.

Native UHI-oriented firmware is a later stage.

---

# 115. Deferred: Hard Real-Time

The kernel should avoid blocking future real-time work, but hard real-time guarantees are not an early requirement.

---

# 116. Avoid Premature Optimization

Early implementation should prefer:

```text
clear
testable
replaceable
```

over maximum benchmark performance.

Critical paths can be optimized after semantics are stable.

---

# 117. Preserve Architectural Boundaries

Prototype code must not bypass architecture merely for convenience if that bypass would become difficult to remove.

Example bad shortcut:

```text
managed driver
    directly calls arbitrary kernel internals
```

Preferred:

```text
managed driver
    uses defined kernel/UHI capability interface
```

---

# 118. Temporary Shortcuts Must Be Explicit

Some early shortcuts are unavoidable.

They should be marked as:

```text
TEMPORARY IMPLEMENTATION LIMITATION
```

with a defined replacement milestone.

---

# 119. Architecture vs Implementation Flexibility

RFC principles are stronger than implementation details.

For example:

```text
Capability-based authority
```

is architectural.

The exact integer handle format is implementation detail.

Implementation may change without violating the RFC.

---

# 120. Milestone Review

Each milestone should end with three reviews.

## Functional

Did the intended behavior actually work?

## Architectural

Did implementation validate or challenge the RFC assumptions?

## Compatibility

Did the change preserve existing working milestones?

---

# 121. Architecture Feedback Loop

RFCs are not immutable scripture.

Implementation may reveal incorrect assumptions.

The desired loop is:

```text
RFC
 ↓
implementation
 ↓
measurement/testing
 ↓
architectural feedback
 ↓
RFC revision
```

---

# 122. Never Hide Failed Hypotheses

If implementation reveals that an architectural idea does not work, document the failure and revise the architecture.

Do not preserve a bad abstraction merely because it was written earlier.

---

# 123. Performance Measurements

Performance work should begin with measurement.

Early benchmarks may include:

```text
boot time
context-switch time
IPC round-trip
allocation
file read/write
network throughput
CoreCLR startup
```

---

# 124. Correctness Before Benchmark Competition

A benchmark result is not a milestone if compatibility semantics are wrong.

For example:

```text
fast FileStream
```

is irrelevant if it violates expected .NET behavior.

---

# 125. Compatibility Is a Feature

From M6 onward, every WitOS-specific optimization must be evaluated against standard .NET semantics.

When forced to choose:

```text
clever WitOS abstraction
vs
correct standard .NET behavior
```

ordinary .NET compatibility wins unless the feature is explicitly opt-in.

---

# 126. Public Demonstration Strategy

Public demonstrations should follow meaningful capability milestones.

Possible progression:

```text
Demo 1 — boot
Demo 2 — managed C#
Demo 3 — standard .NET console
Demo 4 — HttpClient / ASP.NET
Demo 5 — signed applications
Demo 6 — Avalonia application
Demo 7 — distributed resources
```

---

# 127. Avoid Cosmetic Milestones

Do not prioritize:

```text
boot logo
desktop wallpaper
fancy launcher
animation
```

over architectural functionality.

Visual polish becomes valuable after presentation foundations are real.

---

# 128. First Meaningful Public Technical Claim

At M3:

> WitOS runs managed C# system components.

At M6:

> WitOS runs standard .NET applications.

At M9:

> WitOS runs portable graphical .NET applications.

At M10:

> WitOS provides location-aware distributed resources as an operating-system concept.

These claims should be backed by executable demonstrations and tests.

---

# 129. First Physical Hardware

Physical hardware should be introduced only after the VM architecture is reasonably stable.

Choose one tightly controlled target.

Criteria:

```text
good documentation
simple boot environment
known hardware
available virtualization/debug support
manageable device set
```

---

# 130. ARM64 Timing

ARM64 support should arrive earlier than broad physical PC support.

Running:

```text
QEMU x64
QEMU ARM64
```

early is valuable because it exposes accidental x86 assumptions.

---

# 131. RISC-V

RISC-V remains a future architecture target.

It should not block the initial implementation.

---

# 132. Kernel Language

The kernel language decision should be based on:

```text
runtime independence
toolchain reliability
ABI control
debuggability
memory safety
architecture support
```

rather than a desire to maximize use of C# everywhere.

Managed code becomes dominant above the minimal kernel boundary.

---

# 133. Native Code Budget

A useful project goal is to keep native/unsafe system code narrow.

The metric is not:

```text
zero native code
```

but:

```text
native code only where it provides fundamental mechanisms
```

---

# 134. Managed System Goal

By the end of M5–M7, most new system functionality should preferably be implemented in managed code.

---

# 135. Definition of Success for Initial Program

The first implementation program should be considered successful when the project reaches M6.

At that point WitOS has:

```text
independent boot
kernel
isolation
managed system
hardware abstraction
storage
CoreCLR
standard .NET applications
```

This is the minimum point at which the architecture becomes substantially validated.

---

# 136. Definition of First Usable Platform

M7–M8 create the first practically useful headless WitOS platform:

```text
.NET
files
network
applications
signing
```

This can already serve:

```text
server
compute node
VM workload
edge appliance
```

without GUI.

---

# 137. Definition of First Workstation Platform

M9 creates the first graphical WitOS platform.

A full desktop environment is not required.

---

# 138. Definition of Architectural Differentiation

M10 demonstrates why WitOS exists.

Without M10-like capabilities, WitOS risks becoming merely:

```text
a clean new .NET-oriented operating system
```

which may still be useful, but does not yet demonstrate the full architecture.

---

# 139. Implementation Priorities

When deciding between tasks, prefer work that:

1. unlocks the next runnable milestone;
2. validates a core architectural assumption;
3. improves testability;
4. improves .NET compatibility;
5. removes a temporary architectural shortcut.

Lower priority:

```text
cosmetic features
broad compatibility before architecture validation
premature optimization
features not needed by the next vertical slice
```

---

# 140. Issue Structure

Development issues should ideally reference:

```text
milestone
RFC section
Definition of Done criterion
```

Example:

```text
M2
RFC 0004 capability transfer
Implement capability handle validation for ChannelSend
```

This keeps implementation tied to architecture.

---

# 141. AI-Assisted Development

WitOS is expected to make significant use of coding agents.

This makes precise specifications and conformance tests particularly important.

Suitable task shape:

```text
Implement section X
against interface Y
with invariants Z
and tests A/B/C
```

rather than:

```text
implement the storage system
```

---

# 142. Agent Work Must Remain Reviewable

AI-generated implementation should be:

```text
small enough to review
covered by tests
tied to contracts
free of hidden architectural assumptions
```

The architecture remains human-owned.

---

# 143. Tests as Executable Architecture

Where practical, important RFC invariants should have executable tests.

For example:

```text
ResourceId does not grant authority
unsigned applications remain executable
invalid capability cannot be used
standard Task semantics remain unchanged
```

This reduces architecture drift.

---

# 144. Documentation With Implementation

Each milestone should update:

```text
architecture notes
known limitations
boot instructions
debugging guide
compatibility status
```

Documentation should describe the system that actually exists.

---

# 145. Compatibility Matrix

From M6 onward maintain a public matrix such as:

| Capability | Status |
|---|---|
| Console | Supported |
| FileStream | Supported |
| Task/async | Supported |
| Reflection | Supported |
| Assembly loading | Partial |
| HttpClient | M7 |
| ASP.NET Core | M7 |
| Avalonia | M9 |

Avoid vague statements such as:

```text
.NET mostly works
```

---

# 146. Hardware Matrix

Similarly:

| Platform | Status |
|---|---|
| QEMU x64 | Primary |
| QEMU ARM64 | Early |
| Physical reference machine | Future |
| Generic PC | Future |

---

# 147. Hosted Provider Matrix

Example:

| Provider | Execution | Storage | Communication | Presentation |
|---|---:|---:|---:|---:|
| Windows | Early | Early | Early | Later |
| Linux | Early | Early | Early | Later |
| WitOS | Native | Native | Native | M9 |

---

# 148. Release Style

Milestone builds may be released as experimental artifacts:

```text
WitOS M0
WitOS M1
...
```

The version number may remain pre-1.0 for a long period.

---

# 149. Reproducible VM Demonstrations

Every milestone release should ideally include one command that reproduces the demonstration.

Example:

```text
./run-witos-m6
```

or cross-platform equivalent.

---

# 150. Implementation Invariants

## Invariant 1

WitOS becomes independently bootable as early as possible.

## Invariant 2

The primary development branch should remain bootable.

## Invariant 3

Development proceeds through vertical runnable slices.

## Invariant 4

Each milestone adds observable system behavior.

## Invariant 5

QEMU is the primary initial hardware platform.

## Invariant 6

Broad physical hardware support does not block architecture validation.

## Invariant 7

An external bootloader may be used initially behind a WitOS-owned boot contract.

## Invariant 8

The kernel remains mechanism-focused.

## Invariant 9

User/system isolation is established before large service development.

## Invariant 10

Managed C# system execution is validated early through NativeAOT.

## Invariant 11

CoreCLR compatibility is a distinct major milestone.

## Invariant 12

Standard .NET semantics are preserved.

## Invariant 13

Storage and networking are introduced only as required by vertical milestones.

## Invariant 14

A custom filesystem is not required for the initial system.

## Invariant 15

A full graphical shell is not required before graphical application support.

## Invariant 16

Application signing/trust semantics should exist before graphical shell decoration.

## Invariant 17

Hosted providers are developed in parallel where they help validate public APIs.

## Invariant 18

Hosted execution does not replace the native WitOS implementation track.

## Invariant 19

Automated QEMU boot and testing are required from the earliest milestones.

## Invariant 20

Architecture is revised when implementation disproves assumptions.

## Invariant 21

Conformance tests are part of the architecture.

## Invariant 22

AI-assisted implementation does not replace architectural review.

## Invariant 23

Premature cosmetic work must not block core system milestones.

## Invariant 24

Distributed-resource demonstration is required to validate the full WitOS vision.

---

# 151. Immediate Next Work

The first implementation phase should now focus only on:

```text
M0
M1
M2
M3
```

These milestones define the minimum kernel/runtime foundation.

Before beginning large-scale implementation, the next technical specification should therefore be:

```text
RFC 0011 — Kernel Architecture & ABI
```

but RFC 0011 should initially design only what is necessary for:

```text
boot
memory
interrupts
execution contexts
address spaces
capabilities
channels
minimal user ABI
```

It should not attempt to fully specify every future kernel mechanism.

---

# 152. Initial Implementation Sequence

Practical order:

```text
1. repository skeleton

2. QEMU runner

3. image builder

4. boot adapter

5. serial console

6. kernel entry

7. physical memory allocator

8. virtual memory

9. exception handling

10. timer

11. execution context

12. scheduler

13. user address space

14. minimal ABI

15. capability handle table

16. channel

17. NativeAOT init component
```

At that point M0–M3 are substantially complete.

---

# 153. First Architecture Checkpoint

After the first managed `Hello from WitOS`, stop adding features briefly and review:

```text
kernel complexity
managed/native boundary
ABI quality
debugging experience
build speed
QEMU test cycle
NativeAOT integration
```

If those fundamentals are awkward, fix them before introducing devices and storage.

---

# 154. Second Architecture Checkpoint

After M6, review:

```text
CoreCLR port complexity
BCL compatibility
scheduler semantics
filesystem semantics
GC integration
managed system performance
```

This is the second major opportunity to revise foundational assumptions.

---

# 155. Third Architecture Checkpoint

After M10, evaluate whether the resource/distribution model actually provides practical advantages over conventional operating systems.

The test should be based on working software rather than theoretical elegance.

---

# 156. Summary

WitOS should not be implemented by completing ten RFCs one after another.

It should grow as a continuously runnable system:

```text
Boot
 ↓
Kernel
 ↓
Isolation
 ↓
Managed C#
 ↓
Hardware
 ↓
Storage
 ↓
Standard .NET
 ↓
Network
 ↓
Applications
 ↓
Graphics
 ↓
Distributed Resources
```

The first executable artifact should appear almost immediately.

The first managed artifact should appear early.

Standard .NET compatibility should be reached before substantial graphical work.

The graphical shell should come after graphical application infrastructure, not before it.

Distributed resource execution should eventually demonstrate the architectural reason WitOS exists.

The implementation philosophy is:

> **Always build the smallest next version of the complete system, not the largest isolated piece of an incomplete system.**

And the practical project rule is:

> **At every significant point in development, there should be a WitOS image that can be booted, tested, and shown doing something the previous image could not do.**