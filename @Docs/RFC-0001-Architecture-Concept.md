# WitOS
## RFC 0001 — Architecture Concept
### Draft v0.1

## 1. Vision

WitOS is an operating environment designed around a simple premise:

> **There is one software platform. Devices differ by capabilities, not by operating system.**

An application should be written once and run, without platform-specific adaptations, on any compatible computing device: desktop, laptop, tablet, phone, workstation, server, embedded system, or virtual machine.

Differences in processor architecture, device class, input methods, display configuration, power model, and physical hardware should be handled below the application level.

The system is not intended to reproduce Windows, Linux, macOS, Android, or iOS. Its goal is to reconsider the operating-system abstraction around modern hardware, managed runtimes, heterogeneous devices, distributed computing, and adaptive user interfaces.

---

## 2. Primary Goals

WitOS should provide:

1. **A single application platform**
   - One application binary/package where possible.
   - No Windows/Linux/macOS/Android-specific application variants.
   - No conditional compilation for ordinary applications.
   - Device differences expressed through capabilities.

2. **Full compatibility with upstream .NET**
   - Use the actual open-source .NET runtime.
   - Avoid creating a proprietary or divergent .NET dialect.
   - Track future upstream .NET versions.
   - Portable .NET applications should run unchanged whenever they do not depend on another operating system's APIs.

3. **Hardware independence**
   - CPU architecture and device-specific details exist below a stable hardware boundary.
   - Initial targets: x64 and ARM64.
   - Future architectures such as RISC-V should require a new low-level backend rather than changes throughout the OS.

4. **Device-class independence**
   - Desktop, mobile, tablet, workstation, and server are not separate platforms.
   - They are different collections of resources and capabilities.

5. **Managed-first implementation**
   - The majority of the operating environment should be implemented in memory-safe managed code.
   - Native code should be restricted to areas where direct hardware interaction or runtime implementation requires it.

6. **Distributed resources as a native concept**
   - Local and remote resources should use compatible abstractions.
   - Network distribution should not be artificially added above an intrinsically local operating model.

7. **Optional native hardware integration**
   - New devices may provide a native Universal Hardware Environment in firmware.
   - Existing hardware remains supported through compatibility backends.
   - Native hardware support must improve performance and integration but must never become an artificial lock-in requirement.

---

## 3. Non-Goals

At least initially, WitOS is not intended to:

- emulate Windows or Linux;
- provide complete Win32 compatibility;
- run arbitrary Linux binaries;
- reproduce POSIX as its native programming model;
- replace existing operating systems in the first stages of development;
- support every hardware device from the first release;
- invent a new programming language or managed runtime;
- create a fork of .NET that becomes incompatible with upstream.

Legacy compatibility may be implemented where useful, but it must not define the architecture.

---

## 4. Fundamental Architecture

The system is divided into several conceptual layers.

```text
┌──────────────────────────────────────────────┐
│                 Applications                 │
│            .NET / other runtimes             │
├──────────────────────────────────────────────┤
│            Application Framework             │
│ UI / Lifecycle / Identity / Permissions      │
├──────────────────────────────────────────────┤
│          Universal Resource Model            │
│ Storage / Compute / Display / Input / etc.   │
├──────────────────────────────────────────────┤
│          Universal OS Services               │
│ Networking / Storage / Devices / Security    │
├──────────────────────────────────────────────┤
│                .NET Runtime                  │
│       CoreCLR / NativeAOT / CoreLib          │
├──────────────────────────────────────────────┤
│              Nano/Microkernel                │
│ Memory / Contexts / IPC / IRQ / Timing       │
├──────────────────────────────────────────────┤
│         Universal Hardware Interface         │
├──────────────────────────────────────────────┤
│ Firmware / Compatibility HAL / Hypervisor    │
├──────────────────────────────────────────────┤
│                   Hardware                   │
└──────────────────────────────────────────────┘
```

The exact physical boundaries may differ between implementations, but the semantic boundaries should remain stable.

---

## 5. .NET as the Native Managed Platform

WitOS adopts upstream .NET rather than implementing an independent runtime.

The long-term architecture should resemble:

```text
                     upstream .NET
                          │
          ┌───────────────┼────────────────┐
          │               │                │
       Windows          Linux           WitOS
```

The objective is that an ordinary application targeting:

```xml
<TargetFramework>netX.0</TargetFramework>
```

can run without rebuilding specifically for WitOS, provided that it uses portable .NET APIs.

A platform-specific target should only be required when an application intentionally uses WitOS-specific functionality.

For example:

```csharp
System.Text.Json
System.Linq
System.Threading
System.Net.Http
```

should behave as standard .NET.

WitOS functionality may be exposed separately:

```csharp
var camera =
    await Resources.RequestAsync<ICamera>();
```

The standard .NET API remains valid.

---

## 6. Upstream Compatibility Principle

The project should minimize modifications to upstream .NET.

Desired state:

```text
Modified upstream runtime code:    approximately zero
WitOS-specific runtime backend:    small
WitOS-specific CoreLib changes:    approximately zero
WitOS-specific BCL behavior:       minimal
```

WitOS should continuously test itself against current upstream .NET.

A future CI pipeline should approximately perform:

```text
dotnet/runtime main
        ↓
build WitOS runtime
        ↓
x64 tests
ARM64 tests
        ↓
CoreCLR
GC
JIT
BCL
Threading
Reflection
NativeAOT
ASP.NET Core
```

Compatibility with future .NET releases is a primary architectural requirement.

---

## 7. Runtime Strategy

Two .NET execution modes may coexist.

### Core system

Critical OS services can use NativeAOT:

```text
WitOS core
    ↓
NativeAOT
    ↓
native machine code
```

Advantages:

- fast startup;
- predictable deployment;
- no boot-time JIT requirement;
- reduced runtime complexity during early boot.

### Applications

Applications can use ordinary CoreCLR:

```text
Application.dll
       ↓
    CoreCLR
       ↓
      JIT
```

Advantages:

- normal .NET binary compatibility;
- reflection;
- dynamic loading;
- runtime code generation where supported;
- existing NuGet ecosystem.

NativeAOT and CoreCLR are therefore complementary rather than competing execution models.

---

## 8. Kernel Philosophy

The native kernel should provide mechanisms, not high-level operating-system policy.

The kernel should understand concepts such as:

```text
PhysicalMemory
AddressSpace
ExecutionContext
Interrupt
Timer
Event
Channel
Capability
DeviceMemory
DMA domain
```

It should not need to understand concepts such as:

```text
File
Window
User
TCP connection
Process name
Directory
Camera
Printer
HTTP
```

These belong to managed services above the kernel.

A conceptual kernel API might contain primitives equivalent to:

```text
CreateAddressSpace
MapMemory
CreateExecutionContext
SwitchContext
Wait
Signal
CreateChannel
MapDeviceMemory
BindInterrupt
CreateDmaDomain
```

The kernel should remain small enough that its correctness and security properties can be reasoned about independently.

---

## 9. Hardware Abstraction

WitOS defines a stable Universal Hardware Interface.

There are multiple legitimate implementations:

```text
                        WitOS
                          │
              Universal Hardware API
                          │
        ┌─────────────────┼─────────────────┐
        │                 │                 │
 Native firmware    Compatibility HAL    Virtual HAL
        │                 │                 │
 new hardware        legacy hardware     VM/hypervisor
```

The operating system above this interface should not depend on which implementation is being used.

---

## 10. Native Hardware Environment

Future devices may implement the Universal Hardware Interface directly in firmware.

This firmware represents the hardware personality of the device.

Its responsibilities may include:

- early CPU initialization;
- RAM initialization;
- CPU topology;
- interrupt controller setup;
- basic memory map;
- MMU bootstrap;
- device discovery information;
- power domains;
- DMA/IOMMU setup;
- secure boot;
- hardware identity;
- initial capability construction.

It should expose mechanisms and hardware description rather than implement high-level operating-system services.

A native device might boot approximately as:

```text
Power
  ↓
Hardware firmware
  ↓
RAM / CPU / basic devices
  ↓
Universal Hardware Environment
  ↓
NanoKernel
  ↓
NativeAOT OS core
  ↓
Managed services
  ↓
UI / applications
```

The firmware implementation may reside in:

- SPI flash;
- SoC internal flash;
- ROM + flash;
- secure firmware;
- a dedicated controller;
- another implementation chosen by the hardware vendor.

A dedicated physical chip is optional.

The interface matters; physical placement does not.

---

## 11. Legacy Hardware Support

Native WitOS firmware must never be mandatory.

Existing machines should construct the same hardware environment through adapters:

```text
UEFI
ACPI
Device Tree
PCI/PCIe
legacy drivers
        ↓
Compatibility Hardware Environment
        ↓
Universal Hardware Interface
```

During early development, Linux itself may be used as a hardware compatibility backend:

```text
WitOS
  ↓
WitOS Hardware Interface
  ↓
Linux compatibility backend
  ↓
Linux device drivers
  ↓
Hardware
```

This allows development of the operating model without first reproducing the entire modern hardware driver ecosystem.

Later, native drivers can replace compatibility backends where this creates sufficient benefit.

---

## 12. Resource Model

A fundamental WitOS abstraction is the **resource**.

Traditional operating systems expose unrelated concepts such as:

```text
file
socket
device
process
GPU
screen
network interface
remote service
```

WitOS should seek a more consistent model.

Conceptually:

```csharp
public interface IResource
{
    ResourceId Id { get; }
    CapabilitySet Capabilities { get; }
}
```

Derived resource classes might include:

```text
IStorageResource
IComputeResource
IDisplayResource
IInputResource
INetworkResource
IAudioResource
ISensorResource
ICameraResource
```

Resources may be:

```text
Local
Remote
Replicated
Virtual
```

Location should only matter when required by semantics, latency, bandwidth, privacy, or explicit application constraints.

---

## 13. Capability Model

Applications should request what they need rather than infer capabilities from an operating-system name.

Bad model:

```csharp
if (OperatingSystem.IsAndroid())
{
    ...
}
```

Preferred model:

```csharp
if (Resources.TryGet<ICamera>(out var camera))
{
    ...
}
```

or:

```csharp
var gpu = await Resources.AcquireAsync<IComputeResource>(requirements);
```

A device does not need to be classified as a phone, PC, or server.

It simply exposes capabilities.

---

## 14. Security Model

Capabilities should also form the basis of security.

An application should not implicitly receive all authority belonging to the user.

Instead:

```text
no capability
    =
no access
```

Example permissions:

```text
Storage.Documents.Read
Camera.Front
Network.Internet
Location.Approximate
Microphone
GPU.Compute
```

The same model applies to desktop, phone, server, and embedded devices.

Hardware protection through MMU/address spaces remains available and should be used where appropriate.

Managed safety complements hardware isolation; it does not replace it universally.

---

## 15. Application Model

An application should be a logical entity independent of a particular device.

Its package may contain:

```text
managed assemblies
resources
UI definitions
manifest
capability requirements
optional architecture-specific native assets
```

The same application package should ideally run on x64 and ARM64 because managed code remains architecture-independent.

Architecture-specific assets exist only where native dependencies make them unavoidable.

---

## 16. Adaptive UI

UI adaptation should be based on capabilities and available presentation space rather than OS identity.

The application may observe:

```text
display dimensions
DPI
orientation
touch support
pointer precision
keyboard availability
multiple displays
windowing availability
```

and adapt accordingly.

Avalonia is a strong candidate for an initial UI framework because its architecture already separates application UI from platform-specific backends.

---

## 17. Unified Input Model

Input should similarly be capability-based.

Instead of applications having fundamentally unrelated paths for:

```text
mouse
touch
pen
trackpad
```

the OS may expose generalized pointer/input capabilities such as:

```text
position
buttons
pressure
tilt
hover
precision
multitouch
gesture support
```

Different physical devices implement different subsets.

Applications respond to available capabilities.

---

## 18. Application Lifecycle

The traditional desktop assumption that an application exists continuously from explicit start to explicit exit should not be fundamental.

Universal applications should support a lifecycle appropriate to:

- mobile suspension;
- desktop sleep;
- memory pressure;
- process migration;
- device handoff;
- VM migration;
- failure recovery.

Possible lifecycle:

```text
Created
   ↓
Active
   ↓
Suspended
   ↓
Persisted
   ↓
Restored
```

Applications should have explicit mechanisms for persistent logical state that are distinct from transient execution state.

---

## 19. Storage Model

Traditional local filesystem semantics should remain available for .NET compatibility.

For example:

```csharp
File.OpenRead("document.txt");
```

must continue to work.

Internally, however, WitOS may model persistent objects more richly:

```text
identity
content
version
replicas
permissions
availability
location
```

A storage object might physically exist:

```text
in RAM
on local flash
on NVMe
on another device
on NAS
in cloud storage
in several locations simultaneously
```

The compatibility filesystem becomes a view of this richer storage model rather than the fundamental abstraction of the system.

---

## 20. Distributed Computing

Distribution should be native but not invisible where physical constraints matter.

A computation might request:

```csharp
IComputeResource resource =
    await Resources.AcquireAsync<IComputeResource>(requirements);
```

The selected resource may be:

```text
local CPU
local GPU
another workstation
LAN compute node
server
cloud accelerator
```

Applications can specify constraints concerning:

```text
latency
bandwidth
locality
privacy
memory
accelerator capabilities
cost
power
```

The goal is not to pretend that local and remote execution are physically identical.

The goal is to place both within the same resource and capability model.

---

## 21. Networking

Networking should be both:

1. compatible with ordinary .NET networking APIs;
2. usable by the richer distributed resource environment.

Standard applications continue to use:

```text
System.Net
System.Net.Sockets
HttpClient
ASP.NET Core
```

WitOS applications may additionally use resource-oriented communication where appropriate.

---

## 22. Drivers

Drivers should preferably execute outside the most privileged kernel layer.

A managed driver might receive:

```text
MMIO capability
interrupt capability
DMA capability
device identity
power-control capability
```

and implement the hardware protocol in managed code.

Conceptually:

```text
Hardware
   ↓
minimal privileged mechanism
   ↓
managed driver
   ↓
resource interface
```

A failed device driver should ideally terminate and restart rather than crash the whole machine.

Some drivers or driver components may necessarily remain native.

---

## 23. Boot Model

WitOS should minimize sequential boot dependencies.

Instead of:

```text
A → B → C → D → E → desktop
```

boot should resemble a dependency graph:

```text
              ┌─ storage
              ├─ network
CPU → RAM ────┼─ display → UI
              ├─ audio
              ├─ Bluetooth
              └─ optional devices
```

Only resources needed for the initial interactive environment block startup.

Other services initialize asynchronously.

---

## 24. Updates

Core system updates should favor immutable or atomic deployment.

An A/B model is desirable:

```text
System A — active
System B — update target
```

Process:

```text
write B
verify B
switch boot target
restart
```

If startup fails:

```text
rollback to A
```

The same principle may be used for firmware.

---

## 25. Hardware Certification

Future hardware may advertise progressively deeper integration with WitOS.

Illustrative levels:

```text
Level 0
Legacy hardware through compatibility environment

Level 1
Native boot and hardware description

Level 2
Full Universal Hardware Interface
including power, DMA/IOMMU and lifecycle

Level 3
Hardware specifically designed around
WitOS execution and security model
```

Certification should describe technical capability, not artificially restrict installation on uncertified machines.

---

## 26. Compatibility Strategy

Compatibility exists at several levels.

### .NET binary compatibility

Portable managed assemblies should run unchanged.

### .NET API compatibility

Standard .NET APIs should maintain their expected semantics.

### Native dependency compatibility

Native libraries may require WitOS builds and a platform RID such as:

```text
witos-x64
witos-arm64
witos-riscv64
```

### Foreign OS compatibility

Windows- or Linux-specific APIs are outside the core compatibility guarantee.

For example:

```text
WPF
WinForms
COM
Win32
Linux ioctl
specific native libraries
```

may require compatibility environments or ports.

---

## 27. Hosted-First Development Strategy

WitOS should initially be developed above existing operating systems.

Phase 1:

```text
WitOS API
   ↓
Windows backend
Linux backend
possibly macOS backend
```

This allows the application model to be developed and tested independently of bare-metal hardware work.

The most important early question is:

> Is the WitOS programming and resource model actually better?

It should be answered before large investment in drivers or kernel development.

---

## 28. Native Prototype

After the hosted model stabilizes, implement a minimal native environment under virtualization.

Preferred initial target:

```text
QEMU
x64 initially
virtio devices
```

Minimum hardware:

```text
CPU
memory
timer
interrupts
console
virtio-block
virtio-net
virtio-input
virtio-gpu or simple framebuffer
```

Milestone:

> The same managed application binary runs on Windows, Linux, and native WitOS under QEMU without application-specific changes.

---

## 29. ARM64 Prototype

The second native architecture should be ARM64.

This demonstrates that CPU independence is real rather than theoretical.

Milestone:

```text
same application DLL
same OS services
same APIs

x64 QEMU
ARM64 QEMU/device
```

Only the hardware/runtime backend differs.

---

## 30. Proposed Development Phases

### Phase 0 — Architecture

Define:

- resource model;
- capability model;
- application lifecycle;
- security boundaries;
- hardware interface;
- .NET compatibility rules;
- IPC;
- local/remote semantics.

Deliverable:

```text
Architecture RFC set
```

### Phase 1 — Hosted Runtime

Implement:

```text
OutWit.OS
OutWit.OS.Resources
OutWit.OS.Security
OutWit.OS.Lifecycle
OutWit.OS.Platform.Windows
OutWit.OS.Platform.Linux
```

Demonstrate one application on both hosts.

### Phase 2 — Application Environment

Add:

- Avalonia integration;
- storage abstraction;
- networking;
- permissions;
- application packaging;
- lifecycle persistence.

### Phase 3 — Distributed Resources

Prototype:

- resource discovery;
- remote resources;
- remote compute;
- migration/handoff;
- resource locality constraints.

### Phase 4 — Native Kernel Prototype

Implement:

- boot;
- memory;
- address spaces;
- execution contexts;
- interrupts;
- timing;
- IPC;
- capability primitives.

Run under QEMU.

### Phase 5 — .NET Native Platform Port

Port upstream .NET sufficiently to execute existing portable .NET applications.

Run upstream .NET test suites.

### Phase 6 — Native Device Stack

Implement virtio-based:

- storage;
- network;
- display;
- input.

### Phase 7 — ARM64

Create ARM64 low-level backend.

Verify architecture independence.

### Phase 8 — Physical Hardware

Select one tightly controlled hardware target.

Avoid broad PC hardware support initially.

### Phase 9 — Universal Hardware Firmware Prototype

Build a firmware-level implementation of the Universal Hardware Interface.

Compare:

```text
legacy boot path
vs
native WitOS hardware path
```

### Phase 10 — Broader Ecosystem

Evaluate:

- NuGet compatibility;
- ASP.NET Core;
- Roslyn;
- NUnit/xUnit;
- databases;
- Avalonia applications;
- selected native libraries.

---

## 31. Early Success Metrics

### .NET

- percentage of upstream runtime/BCL tests passed;
- compatibility with current .NET;
- compatibility with next .NET version;
- size of WitOS-specific runtime patch set.

### Applications

- existing .NET programs running without source modifications;
- NuGet package compatibility;
- absence of WitOS-specific conditional compilation.

### Architecture

- same application binary on x64 and ARM64;
- same application binary hosted and native;
- no device-class-specific application build.

### Kernel

- size of privileged native code;
- driver failures isolated from kernel;
- clean capability enforcement.

### Hardware

- number of platform-specific components required for a new architecture;
- time required to bring up a new hardware target.

---

## 32. Core Architectural Rules

### Rule 1

**Do not fork .NET conceptually.**

WitOS is a .NET platform, not a competing .NET implementation.

### Rule 2

**Applications depend on capabilities, not OS/device names.**

Avoid:

```csharp
if (IsPhone)
if (IsWindows)
```

Prefer:

```csharp
if (HasTouch)
if (HasCamera)
```

### Rule 3

**Mechanism belongs below; policy belongs above.**

Keep the privileged kernel minimal.

### Rule 4

**Native hardware integration is optional.**

Legacy hardware remains a valid target.

### Rule 5

**Compatibility must not dictate the native architecture.**

Existing APIs may be presented as compatibility views over richer internal models.

### Rule 6

**Locality is a property, not a fundamental type distinction.**

Local and remote resources should participate in a common model where sensible.

### Rule 7

**Managed code is preferred but not dogmatic.**

Use native code where technically justified.

### Rule 8

**Do not invent infrastructure that upstream ecosystems already solve well.**

Reuse:

```text
.NET
C#
Roslyn
NuGet
MSBuild
Avalonia where appropriate
```

### Rule 9

**Bare metal is a backend, not the definition of the project.**

The operating model must provide value even before the native kernel exists.

### Rule 10

**Universality must be demonstrated, not asserted.**

The decisive tests are existing applications running unchanged across genuinely different environments.

---

## 33. Central Hypothesis

The project is ultimately an experiment around one hypothesis:

> A modern operating system can move the stable application boundary above hardware architecture, device class, and local-machine assumptions, while using a thin hardware-dependent layer below a standard managed runtime.

If this hypothesis is correct, the resulting platform could make:

```text
desktop
mobile
tablet
server
workstation
edge device
cloud node
```

different manifestations of the same computing environment rather than separate software platforms.

The desired result is not merely cross-platform software.

It is a system in which **platform differences largely cease to exist at the application level**.

---

## 34. Short Form

The entire project can be summarized as:

```text
                 Application
                      │
              Standard .NET
                      │
          WitOS semantics
                      │
       Resources + Capabilities
                      │
             Minimal kernel
                      │
       Universal Hardware Interface
                      │
                  Hardware
```

with one defining principle:

> **Write once for one platform. Run wherever the required capabilities exist.**
