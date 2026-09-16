# WitOS
## RFC 0007 — Universal Hardware Interface
### Draft v0.1

## 1. Status

Draft.

This document defines the Universal Hardware Interface of WitOS.

It builds upon:

```text
RFC 0001 — Architecture Concept
RFC 0002 — Resource & Capability Model
RFC 0003 — Application & Lifecycle Model
RFC 0004 — Security, Identity & Capability Delegation
RFC 0005 — Execution, Scheduling & Compute Model
RFC 0006 — IPC & Local/Remote Communication Model
```

The central principle is:

> **Hardware-specific knowledge should terminate at a narrow, stable boundary below the operating-system resource model.**

A second principle is:

> **A new device should describe and expose what it can do rather than require the rest of the operating system to know what device model it is.**

---

## 2. Motivation

Traditional operating systems accumulate enormous amounts of hardware-specific knowledge.

This includes:

```text
CPU families
chipsets
interrupt controllers
PCI quirks
ACPI quirks
firmware bugs
storage controllers
USB controllers
network devices
GPU initialization
power management
device-specific initialization sequences
```

Over decades this results in operating systems where support for hardware becomes inseparable from the operating system itself.

WitOS seeks a different boundary.

The long-term goal is:

```text
Hardware-specific implementation
            ↓
Universal Hardware Interface
            ↓
WitOS
```

The operating system above this boundary should not need to know whether it is running on:

```text
Intel PC
AMD workstation
ARM SoC
RISC-V board
virtual machine
cloud VM
future custom hardware
```

except when architecture-specific behavior is explicitly relevant.

---

## 3. Hardware Personality

Every machine has a **hardware personality**.

It describes:

```text
processors
memory
interrupts
timers
devices
buses
DMA capabilities
power domains
security hardware
boot facilities
```

WitOS should consume this personality through one stable semantic interface.

The source of that information may differ.

---

## 4. Multiple Hardware Backends

The same Universal Hardware Interface may be implemented through:

```text
Native WitOS firmware
Compatibility HAL
Hypervisor backend
Hosted development backend
```

Conceptually:

```text
                     WitOS
                       │
          Universal Hardware Interface
                       │
       ┌───────────────┼────────────────┐
       │               │                │
 Native Firmware   Legacy HAL       Virtual HAL
       │               │                │
   New Device       PC / SoC          VM
```

The operating system above the interface should not depend on which backend is used.

---

## 5. Native Hardware Environment

A device designed for WitOS may implement the Universal Hardware Interface directly.

This environment may reside in:

```text
firmware
SoC firmware
SPI flash
secure monitor
dedicated controller
ROM + flash
virtual firmware
```

Physical placement is implementation detail.

The semantic interface is what matters.

---

## 6. Compatibility Hardware Environment

Existing hardware must remain usable.

A compatibility implementation may translate:

```text
UEFI
ACPI
PCI/PCIe
Device Tree
SMBIOS
vendor firmware
legacy device interfaces
```

into the same Universal Hardware Interface.

Therefore:

```text
old hardware
    ↓
compatibility layer
    ↓
same WitOS hardware model
```

---

## 7. Virtual Hardware Environment

Virtual machines and hypervisors may expose the interface directly.

For example:

```text
QEMU
Hyper-V
KVM
VMware
cloud VM
```

may be presented to WitOS through a virtual hardware backend.

Virtio is a natural early implementation technology but must not define the architecture.

---

## 8. Hosted Hardware Backend

During early development, WitOS may execute above another operating system.

Example:

```text
WitOS hosted runtime
      ↓
Universal Hardware Interface
      ↓
Windows/Linux backend
      ↓
host operating system
      ↓
hardware
```

This allows hardware-independent services to be implemented before the native kernel and driver stack are complete.

---

## 9. Stable Boundary

The Universal Hardware Interface should remain significantly more stable than individual device drivers.

Above it:

```text
kernel
resource managers
drivers
services
applications
```

should not contain vendor-specific initialization logic unless explicitly implementing a device protocol.

---

## 10. What Belongs Below the Boundary

The hardware layer may provide:

```text
CPU discovery
memory map
interrupt routing
timer sources
device enumeration
MMIO regions
I/O port regions where applicable
DMA/IOMMU primitives
power/reset primitives
boot identity
firmware services
security roots
```

---

## 11. What Does Not Belong Below the Boundary

The hardware environment should not normally implement:

```text
filesystem
TCP/IP
HTTP
windowing
camera API
printer API
application model
user accounts
package management
high-level storage semantics
```

These belong above the hardware boundary.

---

## 12. Mechanism, Not Policy

The hardware interface exposes mechanisms.

It should not decide high-level policy.

Example:

```text
Hardware layer:
    CPU may enter power state X

WitOS policy:
    choose whether and when to use X
```

---

## 13. Hardware Descriptor

The hardware environment exposes a root hardware descriptor.

Conceptually:

```csharp
public interface IHardwareEnvironment
{
    HardwareIdentity Identity { get; }

    IReadOnlyList<IProcessorDescriptor> Processors { get; }

    IReadOnlyList<IMemoryRegionDescriptor> Memory { get; }

    IReadOnlyList<IDeviceDescriptor> Devices { get; }
}
```

The exact .NET representation is illustrative.

The native ABI may be lower level.

---

## 14. Descriptor vs Capability

Hardware descriptors describe existence and properties.

Capabilities grant authority.

Knowing that Device X exists does not imply that a component may map Device X registers.

This follows RFC 0002.

---

## 15. Hardware Identity

The environment may expose machine identity.

Possible forms:

```text
firmware-generated identity
hardware-backed identity
virtual identity
administrator-defined identity
none
```

Strong identity is optional.

---

## 16. Hardware Identity Is Not Application Identity

A machine identity must never be confused with user identity, application identity, or resource identity.

It is merely one security/resource property.

---

## 17. CPU Architecture

The Universal Hardware Interface supports multiple CPU architectures.

Initial intended targets:

```text
x86-64
ARM64
```

Possible future targets:

```text
RISC-V
other architectures
```

Architecture-specific support should be concentrated below stable abstractions.

---

## 18. Architecture Backend

Each CPU architecture requires a small architecture backend.

Responsibilities may include:

```text
early bootstrap
page table operations
context switching
interrupt entry/exit
atomic primitives
CPU-local state
cache maintenance
TLB operations
architecture timers
```

These should not leak into ordinary system services.

---

## 19. Processor Discovery

The hardware environment must expose logical execution topology.

This includes:

```text
physical processor packages
NUMA nodes
physical cores
hardware threads
core classes
cache domains
frequency domains
```

where available.

---

## 20. CPU Topology

The topology should support RFC 0005.

Example:

```text
Socket 0
 ├── NUMA 0
 │    ├── Core 0
 │    │    ├── Thread 0
 │    │    └── Thread 1
 │    └── Core 1
 └── NUMA 1
```

The scheduler must not reconstruct this topology using vendor-specific heuristics if firmware can provide it directly.

---

## 21. Heterogeneous Cores

The interface must support heterogeneous CPU cores.

Possible descriptors include:

```text
performance class
efficiency class
frequency range
ISA extensions
vector width
power characteristics
```

No assumption should be made that all cores are identical.

---

## 22. ISA Features

Processor descriptors may expose:

```text
SIMD capabilities
vector extensions
crypto extensions
virtualization support
atomic features
memory ordering characteristics
```

These are discoverable hardware capabilities.

---

## 23. ISA Features and .NET

CoreCLR/JIT may consume processor features through the platform adaptation layer.

WitOS-specific application code should not need to duplicate runtime hardware detection unless using advanced APIs.

---

## 24. Processor Availability

Processors may transition between:

```text
Online
Offline
Starting
Stopping
Unavailable
```

This supports CPU hotplug, power management, virtual CPU resizing, and hardware failure.

---

## 25. CPU Startup

The hardware environment should provide mechanisms for bringing secondary processors online.

The kernel owns scheduling.

Firmware owns architecture/device-specific startup where required.

---

## 26. Physical Memory Map

The hardware interface exposes physical memory regions.

Each region may describe:

```text
base address
size
type
NUMA node
cacheability
firmware ownership
device ownership
security properties
```

---

## 27. Memory Region Types

Possible memory types include:

```text
Usable RAM
Reserved
Firmware
MMIO
Persistent Memory
Secure Memory
Device Memory
Unusable
```

The exact classification is extensible.

---

## 28. Memory Identity

Physical memory addresses are not stable resource identities.

They are implementation-level locations.

Higher-level memory resources should use logical handles/capabilities.

---

## 29. NUMA Memory

Memory descriptors should expose NUMA locality.

This supports RFC 0005 compute and memory placement.

---

## 30. Memory Hotplug

The interface may support dynamic memory addition/removal.

Applications should not depend on fixed physical memory topology.

---

## 31. Memory Protection

The architecture backend provides:

```text
page mapping
unmapping
permissions
address-space switching
TLB invalidation
```

The kernel builds higher-level isolation above these mechanisms.

---

## 32. Page Sizes

Hardware may support multiple page sizes.

The interface should expose supported sizes.

The kernel chooses which to use.

---

## 33. Executable Memory

The hardware interface exposes page permission mechanisms needed for read, write, and execute.

This supports W^X policy and .NET JIT requirements.

---

## 34. Cache Attributes

Mappings may require attributes such as:

```text
write-back
write-through
uncached
device memory
write-combining
```

Architecture-specific encoding remains below the interface.

---

## 35. MMIO

Devices may expose memory-mapped I/O regions.

A driver can receive an MMIO capability.

Conceptually:

```text
Device Descriptor
     ↓
authorized driver
     ↓
MMIO capability
```

---

## 36. Port I/O

Architectures supporting port-mapped I/O may expose equivalent resources.

This remains a low-level hardware mechanism.

Higher layers should not depend on it.

---

## 37. Interrupts

The hardware environment must expose interrupt resources.

A device descriptor may reference:

```text
interrupt lines
MSI/MSI-X vectors
virtual interrupts
doorbells
```

The driver receives a capability rather than arbitrary global interrupt access.

---

## 38. Interrupt Routing

Interrupt routing may depend on CPU topology, interrupt controller, NUMA locality, power state, and virtualization.

The kernel and hardware backend coordinate routing.

---

## 39. Interrupt Controller

Architecture/platform-specific interrupt controllers remain below the stable interface.

Examples might include:

```text
APIC
GIC
PLIC
virtual interrupt controller
```

Higher layers should see generic interrupt capabilities.

---

## 40. Interrupt Affinity

Interrupt affinity may participate in scheduling.

For example:

```text
network queue
    IRQ
    ↓
same CPU/NUMA domain as consumer
```

This may improve locality.

---

## 41. Interrupt Safety

Ordinary drivers should not execute arbitrary large workloads directly in privileged interrupt context.

Interrupt handling should minimize privileged work and defer processing where practical.

---

## 42. Timers

The hardware interface exposes timing sources.

Possible capabilities include:

```text
monotonic timer
high-resolution timer
per-CPU timer
deadline timer
real-time clock
```

---

## 43. Monotonic Time

WitOS requires a monotonic timing source for scheduler, timeouts, deadlines, profiling, and leases.

Wall-clock time is separate.

---

## 44. Wall Clock

Real-world calendar time may come from RTC, network synchronization, trusted time provider, or user setting.

It must not be confused with monotonic time.

---

## 45. High-Resolution Timers

Latency-sensitive workloads may require high-resolution timers.

The interface should report actual precision and cost.

---

## 46. Clock Stability

Virtualized and mobile hardware may have varying clock characteristics.

The timing subsystem must handle frequency changes without breaking monotonic semantics.

---

## 47. Device Enumeration

The hardware interface provides device discovery.

A device descriptor may contain:

```text
device identity
class
resources
dependencies
power domain
interrupts
MMIO
DMA requirements
firmware metadata
```

---

## 48. Device Class

Device class helps select a driver.

Examples:

```text
storage controller
network controller
display controller
USB controller
audio controller
sensor hub
```

This remains hardware-level classification.

Application-level device semantics appear only after driver/provider transformation.

---

## 49. Device Identity

A physical device may have:

```text
stable hardware identity
firmware identity
bus identity
temporary enumeration identity
```

The interface should distinguish stable identity from temporary addressing.

---

## 50. Device Dependencies

Some devices depend on others.

Example:

```text
camera sensor
   ↓
I2C controller
   ↓
power domain
```

The hardware graph should represent dependencies where possible.

---

## 51. Hardware Graph

Hardware is not necessarily a tree.

The environment should be capable of representing a graph.

Example:

```text
GPU
 ├── PCIe function
 ├── IOMMU domain
 ├── power domain
 ├── display outputs
 └── shared memory region
```

---

## 52. Device Resource Bundle

A driver may receive a bundle such as:

```text
MMIO regions
interrupt capability
DMA capability
reset capability
power capability
configuration metadata
```

This is preferable to giving the driver unrestricted hardware access.

---

## 53. Driver Binding

A driver declares the types of hardware descriptors it can handle.

The system selects a suitable driver.

Conceptually:

```text
Device Descriptor
     ↓
Driver Resolver
     ↓
Driver Package
```

---

## 54. Driver Matching

Matching may use:

```text
device class
vendor/device identifiers
compatible strings
interface versions
firmware-defined contract identifiers
```

Native WitOS hardware should prefer semantic compatibility identifiers over giant vendor-specific ID tables where possible.

---

## 55. Native Device Contracts

Future hardware may expose standardized semantic device contracts.

Example:

```text
BlockStorageController v2
NetworkQueueDevice v1
DisplayOutput v3
```

This can substantially reduce vendor-specific driver complexity.

---

## 56. Firmware Does Not Need to Implement Every Driver

Native WitOS firmware should not contain all high-level device protocols.

For example:

```text
Firmware exposes:
    MMIO
    IRQ
    DMA
    reset
    device descriptor

Managed driver implements:
    NVMe
    USB
    Wi-Fi protocol
```

This keeps firmware smaller and updateable OS drivers flexible.

---

## 57. Device Firmware

Some hardware includes its own firmware.

WitOS may need mechanisms for firmware loading, verification, update, and rollback.

These are device-management functions above the basic hardware interface.

---

## 58. Firmware Trust

Device firmware must not automatically be considered trustworthy.

Security policy may use signature validation, measurement, vendor trust, and organization policy where appropriate.

---

## 59. DMA

DMA is a first-class hardware concept.

A device with DMA can potentially access system memory without CPU mediation.

Therefore DMA authority must be explicit.

---

## 60. DMA Domain

A driver may create or receive a DMA domain.

Conceptually:

```text
DMA Domain
   ├── Buffer A
   ├── Buffer B
   └── Buffer C
```

The device can access only mapped buffers where hardware permits enforcement.

---

## 61. IOMMU

If an IOMMU is available, WitOS should use it to isolate devices.

The hardware interface exposes generic IOMMU capabilities.

Vendor-specific programming remains below the interface.

---

## 62. No-IOMMU Hardware

Some hardware lacks an IOMMU.

WitOS may still support it but must report weaker isolation guarantees.

---

## 63. DMA Buffers

DMA-compatible memory may have constraints:

```text
alignment
physical contiguity
address width
cache coherence
device visibility
```

These properties should be expressed through memory-resource requirements.

---

## 64. Cache Coherency

The hardware interface should expose relevant cache coherency properties.

Drivers should not assume CPU/device memory is always coherent.

---

## 65. Power Management

Power is a fundamental hardware capability.

The environment may expose:

```text
device power states
CPU power states
frequency domains
sleep states
power domains
battery information
thermal constraints
```

Policy remains above the hardware layer.

---

## 66. Device Power State

A driver/service may request state transitions such as:

```text
Active
Idle
LowPower
Off
```

The actual available states are device-specific.

---

## 67. CPU Power Policy

RFC 0005 may request performance preference, efficiency preference, or latency-sensitive operation.

The hardware layer translates policy into supported CPU mechanisms.

---

## 68. Frequency Control

The hardware environment may expose frequency range, performance levels, and boost availability.

The scheduler/power manager chooses actual policy.

---

## 69. Thermal Management

Hardware may expose:

```text
temperature sensors
thermal limits
throttling states
cooling capabilities
```

Thermal safety overrides application performance preferences.

---

## 70. Thermal Events

The system may receive hardware events:

```text
temperature warning
critical threshold
throttling required
device shutdown required
```

These events must be handled even if ordinary services are overloaded.

---

## 71. Battery

Battery-backed systems may expose charge level, charging state, health, power source, and estimated capacity.

Battery semantics belong to a higher managed resource after hardware discovery.

---

## 72. Suspend

The hardware interface must support suspend/resume mechanisms where hardware allows them.

Possible states may include:

```text
light idle
deep sleep
suspend-to-memory
hibernate support
platform-specific low-power state
```

WitOS application lifecycle remains independent of the exact hardware sleep mode.

---

## 73. Resume

On resume, devices may need reinitialization, firmware reload, resource rebinding, DMA restoration, or interrupt reconfiguration.

Drivers must not assume physical state survived unchanged.

---

## 74. Reset

The hardware layer provides reset mechanisms.

Possible scopes:

```text
device reset
bus reset
CPU reset
system reboot
cold reset
```

Authority is capability-controlled.

---

## 75. Watchdog

Hardware watchdog resources may be exposed to trusted system services.

Applications should not normally control machine watchdogs directly.

---

## 76. Boot Process

The hardware environment participates in boot.

Conceptually:

```text
Power
  ↓
firmware
  ↓
hardware initialization
  ↓
Universal Hardware Environment
  ↓
kernel
  ↓
managed core services
```

---

## 77. Boot Contract

The kernel should receive a stable boot contract rather than parsing arbitrary firmware structures throughout the OS.

This boot contract may contain:

```text
memory map
CPU topology
device graph
boot storage
console
security state
firmware interface
```

---

## 78. Early Console

The hardware environment may expose a minimal early console or logging target.

Possible implementations:

```text
serial
firmware text console
framebuffer
hypervisor console
debug port
```

---

## 79. Early Storage

Boot may require access to initial storage.

A minimal boot-storage capability may be supplied before the general storage stack is ready.

---

## 80. Boot Filesystem Is Not Hardware API

The firmware may provide access to boot data.

However, filesystem semantics remain outside the Universal Hardware Interface itself.

---

## 81. Dependency-Graph Boot

Hardware initialization should be modeled as dependencies rather than one giant sequential routine.

Example:

```text
CPU
 ↓
memory
 ├── storage
 ├── network
 ├── display
 └── optional devices
```

Only required dependencies should delay boot progress.

---

## 82. Parallel Initialization

Independent devices may initialize concurrently.

This is particularly useful for modern hardware with many controllers and slow firmware operations.

---

## 83. Deferred Initialization

Nonessential devices may initialize after the user/session environment becomes available.

Examples:

```text
Bluetooth
secondary storage
secondary GPU
camera
optional USB devices
```

---

## 84. Boot Performance

WitOS should distinguish:

```text
time to kernel
time to managed core
time to interactive presentation
time to full device readiness
```

A device need not wait for every peripheral before becoming usable.

---

## 85. Firmware Fast Path

Native WitOS hardware may perform more initialization before kernel handoff.

This may reduce OS startup work.

---

## 86. Firmware vs Kernel Responsibility

A useful rule:

Firmware should perform operations that are hardware-specific, required before safe kernel execution, or difficult to express generically.

Kernel/system code should perform operations that are policy-sensitive, frequently updated, high-level, or portable.

---

## 87. Firmware Update

Firmware should support safe updates where writable.

Preferred behavior:

```text
write alternate image
verify
activate
rollback on failure
```

where hardware permits.

---

## 88. Firmware ABI Versioning

The Universal Hardware Interface requires version negotiation.

The kernel should know major version, minor version, and feature set before relying on optional facilities.

---

## 89. Backward Compatibility

New firmware should preserve existing interface semantics where practical.

New capabilities should usually be additive.

---

## 90. Feature Discovery

Prefer:

```text
Feature X supported?
```

over:

```text
Firmware version >= 4.2?
```

Version numbers are insufficient substitutes for capability discovery.

---

## 91. Virtualization

WitOS may itself run under a hypervisor.

The Universal Hardware Interface must work naturally with virtual hardware.

---

## 92. Paravirtualization

A hypervisor may expose optimized paravirtualized devices.

Examples:

```text
virtio block
virtio network
virtio GPU
shared-memory transport
```

These can map cleanly to resource/device descriptors.

---

## 93. Hypervisor Detection

The OS may know it is virtualized if useful.

Applications should not normally need to care.

---

## 94. Nested Virtualization

Nothing in the hardware interface should inherently prevent nested virtualization.

Exact support depends on CPU and hypervisor capabilities.

---

## 95. Virtual CPUs

Virtual CPUs may expose topology that differs from physical hardware.

WitOS should treat the exposed topology as authoritative for scheduling inside that VM.

---

## 96. Virtual Memory Hotplug

Cloud environments may dynamically add CPUs or memory.

The interface should support topology updates.

---

## 97. Device Hotplug

Devices may appear or disappear dynamically.

Examples:

```text
USB
Thunderbolt
PCIe hotplug
virtual devices
external GPUs
dock devices
```

The hardware graph must support dynamic changes.

---

## 98. Hotplug Events

The environment should signal:

```text
device added
device removed
device changed
```

The device manager then activates or deactivates drivers.

---

## 99. Surprise Removal

Some devices disappear without graceful shutdown.

Drivers must tolerate this.

Resource revocation propagates upward.

---

## 100. Docking

A dock may add display, network, storage, USB controllers, power, and audio.

This should appear as a resource-graph change, not a platform change.

---

## 101. Hardware Capability Changes

Capabilities may change at runtime.

Examples:

```text
CPU goes offline
GPU resets
external display connected
battery source changes
network adapter disappears
```

The rest of WitOS must adapt dynamically.

---

## 102. Driver Model

A driver transforms low-level hardware resources into semantic resources.

Example:

```text
PCIe NVMe Device
       ↓
NVMe Driver
       ↓
Block Storage Resource
```

The resource above the driver is what higher system layers normally consume.

---

## 103. Managed Drivers

Drivers should be managed where practical.

Advantages include:

```text
memory safety
runtime diagnostics
simpler restart
shared .NET tooling
cross-platform code reuse
```

This is a preference, not an absolute rule.

---

## 104. Native Drivers

Native code remains appropriate when required by architecture bootstrap, runtime constraints, performance, vendor libraries, hardware initialization, or timing requirements.

Native code should remain narrow.

---

## 105. Driver Isolation

Drivers should not automatically execute in the kernel's most privileged address space.

Possible models:

```text
managed service
isolated process
trusted driver host
restricted native host
```

The exact boundary depends on hardware requirements.

---

## 106. Driver Crash

A driver crash should ideally result in:

```text
resource unavailable
driver restart
resource restoration
```

rather than system crash whenever hardware and architecture allow recovery.

---

## 107. Driver Restart

A restartable driver must be able to reacquire MMIO, interrupts, DMA domain, and device reset from the device manager.

---

## 108. Driver State

Transient driver state should not be treated as authoritative persistent device state.

After restart, the driver should reconstruct what it can.

---

## 109. Driver Security

Drivers receive only the hardware capabilities they require.

Example:

```text
NIC driver:
    NIC MMIO
    NIC IRQs
    NIC DMA domain

not:
    all physical memory
    all devices
```

---

## 110. Driver Packages

Driver packages may contain:

```text
managed assemblies
native components
device match metadata
firmware blobs
resource contracts
```

Exact packaging belongs to RFC 0010.

---

## 111. Generic Drivers

Standard hardware contracts make generic drivers possible.

Examples:

```text
USB HID
USB storage
virtio
standard block interface
standard network queue interface
```

WitOS should prefer standardized contracts where available.

---

## 112. Vendor Drivers

Vendor-specific drivers remain possible.

They should implement the same semantic resource interfaces above the hardware layer.

---

## 113. Device Classes Are Not Application APIs

A hardware descriptor may say:

```text
PCI storage controller
```

while applications see:

```text
IStorageResource
```

The hardware taxonomy must not leak unnecessarily into applications.

---

## 114. Display Hardware

The hardware layer may expose display controller, scanout capabilities, framebuffer, modes, connectors, and GPU device.

Higher layers implement compositor and presentation semantics.

---

## 115. Early Framebuffer

A simple framebuffer may be provided for early boot and recovery.

This does not define the normal graphical architecture.

---

## 116. GPU Driver

A GPU driver may expose multiple resources:

```text
graphics queue
compute queue
video decode
video encode
display scanout
memory resources
```

The presentation system consumes these through higher-level APIs.

---

## 117. Input Hardware

Input hardware may expose raw events, device identity, timestamp, and capabilities.

A higher input service transforms these into semantic pointer/touch/keyboard/pen resources.

---

## 118. Raw Input Security

Applications should not automatically receive raw global input merely because input hardware exists.

Hardware access remains separated from application input authority.

---

## 119. Storage Hardware

Storage controllers should expose block-like resources to storage services.

Filesystems remain above this layer.

---

## 120. Network Hardware

Network drivers expose packet/queue resources.

TCP/IP remains above the driver.

---

## 121. Audio Hardware

Audio drivers expose streams, clocking, buffers, formats, and latency characteristics.

Higher-level audio routing and application permissions remain above.

---

## 122. Sensors

Sensor drivers expose typed sensor resources.

Examples:

```text
accelerometer
gyroscope
temperature
GPS-like location source
light sensor
```

Application access is mediated through capability policy.

---

## 123. Secure Hardware

The hardware environment may expose security resources such as:

```text
TPM
secure element
hardware key store
trusted execution environment
random-number generator
```

These become semantic security resources above the hardware layer.

---

## 124. Randomness

The hardware layer may provide entropy sources.

A security service should combine and expose cryptographically appropriate random generation.

Applications should normally use standard .NET cryptographic RNG APIs.

---

## 125. Secure Boot

Native hardware may verify the kernel/core system before execution.

The result may be exposed as a trust characteristic.

---

## 126. Measured Boot

Hardware may support measurements of firmware, kernel, and system image.

These may be consumed by optional attestation services.

---

## 127. Secure Boot Is Not Mandatory

Legacy hardware without secure boot remains valid.

WitOS reports weaker guarantees honestly.

---

## 128. Hardware Root of Trust

A hardware root of trust may provide device identity, key protection, measurement root, and firmware validation.

This is optional capability, not a prerequisite for the OS.

---

## 129. Trusted Execution Environments

TEE resources may be exposed where supported.

Applications do not directly assume a vendor-specific TEE API.

---

## 130. Debug Hardware

Development systems may expose debug resources.

These might include serial debugger, JTAG proxy, hypervisor debug channel, or trace buffer.

Production policy may disable them.

---

## 131. Hardware Trace

Advanced profiling may use CPU performance counters, branch tracing, cache counters, and device telemetry.

Access is capability-controlled.

---

## 132. Performance Counters

Performance counters should not automatically be globally readable because they may leak cross-application information.

Profilers require explicit authority.

---

## 133. Fault Reporting

Hardware may report:

```text
ECC error
machine check
PCI error
device fault
thermal failure
memory fault
```

The hardware layer must normalize these into system fault events.

---

## 134. Recoverable Faults

Some faults may be recoverable.

Examples include GPU reset, device restart, memory page retirement, or CPU offline.

WitOS should prefer local recovery where possible.

---

## 135. Fatal Faults

Some failures make continued safe execution impossible.

The kernel may need to stop execution, persist crash information, reboot, or enter recovery mode.

---

## 136. Hardware Error Isolation

Where hardware supports it, faults should be isolated to the smallest possible domain.

Examples:

```text
one device
one CPU
one memory region
one VM
```

---

## 137. Firmware Errors

Firmware may itself fail or provide invalid data.

WitOS should validate hardware descriptors rather than blindly trusting them.

---

## 138. Hardware Quirks

Legacy hardware may require quirks.

These should remain inside:

```text
compatibility backend
driver
firmware-specific adapter
```

not spread throughout general system services.

---

## 139. Quirk Database

A compatibility backend may maintain a quirk database.

Native WitOS hardware should aim to eliminate this requirement by implementing the standard interface correctly.

---

## 140. Hardware Certification

WitOS may define hardware certification levels.

Illustrative levels:

```text
Level 0
Compatibility hardware

Level 1
Native boot + device graph

Level 2
Full power/DMA/IOMMU interface

Level 3
Hardware designed for WitOS execution/security
```

These levels describe capabilities.

They must not become artificial installation restrictions.

---

## 141. Level 0 — Compatibility

Device runs WitOS using legacy firmware and compatibility HAL.

Expected:

```text
full functional OS
more vendor-specific drivers
slower boot possible
weaker isolation on some hardware
```

---

## 142. Level 1 — Native Boot

Hardware directly supplies:

```text
stable boot contract
memory map
CPU topology
device descriptors
basic firmware interface
```

This reduces compatibility complexity.

---

## 143. Level 2 — Native Hardware Interface

Hardware supports:

```text
power control
DMA/IOMMU
device reset
hotplug
security roots
standard resource descriptors
```

through native UHI semantics.

---

## 144. Level 3 — WitOS-Oriented Hardware

Hardware may be designed specifically around:

```text
capability isolation
fast managed boot
resource partitioning
device-contained low-level support
WitOS-native firmware
```

This is optional.

---

## 145. Certification Is Descriptive

Certification should answer:

> Which guarantees does this device provide?

not:

> Is WitOS allowed to run here?

---

## 146. Universal Hardware Interface ABI

The native ABI should be:

```text
small
versioned
language-neutral
architecture-aware where unavoidable
stable
```

It must not depend on C# object layout.

---

## 147. ABI Representation

Possible implementation forms include:

```text
shared boot structure
function table
message-based firmware protocol
memory-mapped service table
hybrid model
```

The exact representation is deferred.

---

## 148. Firmware Calls

Runtime firmware calls should be limited.

Frequent hot-path operations should generally move into kernel/driver code after initialization.

Firmware should not become a permanent high-latency dependency for ordinary I/O.

---

## 149. Boot-Time vs Runtime Interface

The interface may have two parts:

```text
Boot Interface
    one-time discovery and initialization

Runtime Interface
    power/reset/security/hotplug operations
```

Keeping the runtime interface small improves robustness.

---

## 150. Firmware Reentrancy

If firmware services remain callable after boot, their concurrency and reentrancy semantics must be explicit.

The kernel must not assume firmware is safe for arbitrary parallel invocation.

---

## 151. Firmware Memory Ownership

Memory used by firmware must be clearly described as reserved, reclaimable, runtime-owned, or shared.

Ambiguous ownership is unacceptable.

---

## 152. Secure Firmware Boundary

Firmware must not receive unnecessary visibility into application memory after boot.

Where hardware allows, isolation should restrict firmware/runtime service access.

---

## 153. Hardware Resources and Capabilities

Low-level resources map naturally into RFC 0002.

Examples:

```text
PhysicalMemoryCapability
MmioCapability
InterruptCapability
DmaCapability
ResetCapability
PowerCapability
ProcessorCapability
```

These are privileged system capabilities.

---

## 154. Capability Delegation to Drivers

The device manager owns broad device authority and delegates only device-specific capabilities to the selected driver.

This follows monotonic delegation from RFC 0004.

---

## 155. Kernel Ownership

The kernel retains authority required for memory isolation, scheduling, interrupt mediation, capability enforcement, and address spaces.

Drivers do not need unrestricted kernel authority.

---

## 156. Device Manager

A managed or hybrid Device Manager is responsible for:

```text
device discovery
driver matching
driver activation
resource delegation
driver restart
hotplug
device lifecycle
```

It operates above the minimal kernel.

---

## 157. Device Manager Is Not the Firmware

Firmware describes hardware.

The Device Manager decides how WitOS uses it.

---

## 158. Hardware Resource Resolver

The system may expose internal hardware resources through a resolver.

Applications normally do not receive access to it.

Drivers and privileged services do.

---

## 159. Application Hardware Access

Ordinary applications should request semantic resources such as:

```text
ICamera
IGpuCompute
IAudioOutput
```

not MMIO/IRQ hardware.

---

## 160. Low-Level Application Access

Specialized applications may request low-level device capabilities if policy permits.

Examples:

```text
hardware diagnostics
device development
virtual-machine monitor
industrial control
```

Such authority is privileged.

---

## 161. Containers and Sandboxes

A sandbox may receive virtualized hardware-like resources.

For example:

```text
virtual GPU queue
virtual network interface
virtual block resource
```

These use the same semantic resource model.

---

## 162. Virtual Device Provider

WitOS services may themselves expose virtual devices.

Example:

```text
virtual audio device
virtual camera
virtual disk
virtual network adapter
```

The application need not distinguish physical from virtual unless characteristics matter.

---

## 163. Remote Hardware

A physical device may even be remote.

Examples include remote camera or accelerator.

It should normally appear as a higher semantic resource rather than raw hardware.

---

## 164. Hardware Locality

Low-level hardware descriptors may expose:

```text
NUMA locality
PCIe topology
shared buses
bandwidth constraints
```

This helps scheduling and driver placement.

---

## 165. PCIe Topology

A compatibility backend may expose PCIe hierarchy.

Advanced resource scheduling may use it to identify GPU near CPU node, NVMe near accelerator, shared PCIe switch, or bandwidth contention.

Applications should not normally parse PCI topology directly.

---

## 166. Resource Affinity

A compute allocation may request affinity with a device.

Example:

```text
GPU
    near
CPU partition
    near
memory domain
```

RFC 0005 consumes hardware locality information to satisfy such requests.

---

## 167. Interrupt and Compute Affinity

Drivers may request interrupt placement near worker execution groups.

This is advisory unless guaranteed.

---

## 168. Device Queue Affinity

Multi-queue devices may expose independent queues.

Example:

```text
NIC queue 0 → CPU group A
NIC queue 1 → CPU group B
```

This can improve scaling on large machines.

---

## 169. Storage Queue Affinity

Storage controllers may similarly expose queue topology.

The I/O scheduler may align queues with CPU/NUMA allocation.

---

## 170. GPU Locality

On multi-GPU systems, the hardware graph may expose GPU memory, NUMA relation, PCIe bandwidth, peer-to-peer links, and display attachment.

The compute resolver can use this information.

---

## 171. Unified Memory Systems

Some SoCs share physical memory between CPU and GPU.

The hardware interface should describe this accurately rather than forcing discrete-GPU assumptions.

---

## 172. Non-Coherent Accelerators

Other accelerators may require explicit transfers or cache maintenance.

The interface must express these properties.

---

## 173. Device Memory

Device-local memory may itself be a resource.

Examples:

```text
GPU VRAM
NPU memory
FPGA memory
persistent device memory
```

Applications usually access it through high-level compute APIs.

---

## 174. Hardware Concurrency

The hardware interface must be safe under multi-core concurrent use.

Global firmware locks should be avoided in hot paths.

---

## 175. Per-CPU Data

Architecture backends may expose efficient per-CPU storage primitives to the kernel.

These remain implementation detail.

---

## 176. Boot CPU

One processor begins execution as the bootstrap processor.

After initialization, it should not retain unnecessary permanent special status.

---

## 177. CPU Failure

If a processor becomes unreliable, WitOS may offline it where hardware permits.

Execution allocations then adapt.

---

## 178. Memory Failure

Recoverable memory errors may lead to page retirement.

Applications should not see physical memory failure unless it affects their resource guarantees.

---

## 179. Device Health

Devices may expose health telemetry.

Examples:

```text
SSD wear
temperature
battery health
GPU error count
network link quality
```

This becomes a higher-level resource property.

---

## 180. Hardware Monitoring

System monitoring may consume health telemetry with appropriate capability.

Ordinary applications should not gain unnecessary global hardware visibility.

---

## 181. Privacy

Hardware descriptors may expose identifying information such as serial numbers, MAC addresses, device IDs, or board identifiers.

Access may be restricted for privacy.

---

## 182. Stable IDs vs Privacy

WitOS should avoid exposing stable hardware identifiers to arbitrary applications.

Applications should use semantic resource identities instead.

---

## 183. Hardware Fingerprinting

Low-level topology information itself may enable fingerprinting.

Detailed hardware descriptors should therefore be capability-controlled outside trusted system components.

---

## 184. Time Sources and Security

Security protocols may require trusted wall-clock assumptions.

The hardware interface only exposes available time sources.

Trust policy is defined above.

---

## 185. Secure Counters

Some hardware may provide monotonic counters.

These may support anti-rollback, secure storage, or replay protection.

They become security resources.

---

## 186. Persistent Hardware Storage

Firmware may expose small persistent storage for boot configuration, device identity, security state, and recovery metadata.

It must not become the general application storage model.

---

## 187. Boot Selection

Firmware may support selection between:

```text
System A
System B
Recovery
```

This supports atomic WitOS updates.

---

## 188. Recovery Boot

A minimal recovery environment should be bootable even if the normal system image fails.

This may use the same Universal Hardware Interface.

---

## 189. Hardware Recovery

Recovery may allow firmware rollback, system rollback, storage repair, driver disable, shell disable, and diagnostics.

Exact recovery UI belongs elsewhere.

---

## 190. Development Targets

The first UHI implementation should probably target:

```text
QEMU x64
```

with devices such as:

```text
virtio-block
virtio-net
virtio-input
virtio-gpu
serial/debug console
```

---

## 191. Second Architecture

ARM64 should follow early.

This validates that UHI semantics are not accidentally x86-specific.

---

## 192. Physical Hardware Target

The first physical target should be tightly controlled.

A known board or machine is preferable to arbitrary PC hardware.

---

## 193. Legacy PC Support

Broad PC support should come through a compatibility backend.

Attempting to reproduce Linux-scale hardware support before proving the architecture would be counterproductive.

---

## 194. Linux Compatibility Backend

A development backend may even use Linux as a device-hosting environment.

Conceptually:

```text
WitOS services
     ↓
UHI
     ↓
Linux host adapter
     ↓
Linux drivers
```

This enables experimentation with the WitOS resource model on real hardware early.

---

## 195. Compatibility Does Not Define Native Semantics

The native hardware interface must not merely copy ACPI, Linux device model, or Windows WDM.

Compatibility layers translate those systems into UHI.

---

## 196. Minimal Native Kernel Dependency

The native kernel should depend only on a compact subset of UHI.

For example:

```text
CPU
memory
interrupts
timers
boot console
```

Higher device functionality can appear later through drivers.

---

## 197. Boot Without Full Device Support

WitOS should be able to boot to a minimal environment even when many devices lack drivers.

Unsupported devices simply remain unavailable resources.

---

## 198. Driver Availability

A missing driver should mean:

```text
resource unavailable
```

not:

```text
OS cannot boot
```

unless the missing device is required for boot itself.

---

## 199. Hardware Requirements

WitOS should publish minimum hardware requirements in terms of capabilities.

Example:

```text
required:
    supported CPU architecture
    MMU
    minimum memory
    timer
    interrupt mechanism
    boot storage or network boot

optional:
    IOMMU
    GPU
    TPM
    battery
```

---

## 200. MMU Requirement

A native general-purpose WitOS implementation will normally require an MMU.

Extremely small embedded profiles could be considered separately.

---

## 201. Embedded Variant

The architecture should not forbid systems with limited hardware.

An embedded profile may have single address space, no GPU, small RAM, and static devices while preserving as much of the resource/capability model as practical.

---

## 202. Capability Reporting

The OS should report actual hardware guarantees.

Example:

```text
IOMMU:
    unavailable

Secure Boot:
    unavailable

CPU topology:
    available

DMA isolation:
    weak
```

No feature should be assumed merely because of device category.

---

## 203. Hardware Degradation

If a feature becomes unavailable at runtime, dependent resources may transition to degraded or unavailable states.

---

## 204. Hardware Profiles

Hardware vendors may publish tested profiles describing supported UHI capabilities.

These aid certification and diagnostics.

---

## 205. Vendor Extensions

The interface may allow vendor extensions.

Extensions must not be required for ordinary semantic resources if a standard capability exists.

---

## 206. Extension Namespaces

Vendor extensions should be separately identified to prevent collisions.

Applications should not depend on them unless intentionally hardware-specific.

---

## 207. Standardization

If WitOS becomes widely used, UHI should ideally become a published open specification.

Hardware vendors could implement it without needing the full WitOS source tree.

---

## 208. Reference Implementation

The project should provide reference implementations for:

```text
QEMU/virtio
legacy x64 PC
ARM64 virtual platform
```

These become conformance examples.

---

## 209. Conformance Tests

A UHI implementation should pass conformance tests.

Categories may include:

```text
boot contract
memory map
CPU topology
timer correctness
interrupt delivery
device enumeration
DMA isolation
power transitions
hotplug
reset
```

---

## 210. Hardware Simulator

A simulated hardware backend could deliberately expose unusual topologies:

```text
heterogeneous cores
multiple NUMA nodes
device hotplug
fault injection
missing IOMMU
high latency devices
```

This helps detect architectural assumptions early.

---

## 211. Fault Injection

Testing should support:

```text
device removal
interrupt loss
DMA fault
thermal event
CPU offline
memory error
firmware timeout
```

Hardware failure must be testable rather than hypothetical.

---

## 212. Traceability

Low-level hardware events should be traceable for diagnostics.

Example:

```text
device discovered
driver bound
IRQ allocated
DMA domain created
device reset
device removed
```

---

## 213. Diagnostics API

Trusted diagnostic tools may inspect the hardware graph.

Ordinary applications receive filtered semantic resources.

---

## 214. Human-Readable Hardware Tree

A system tool may present:

```text
Machine
 ├── CPUs
 ├── Memory
 ├── PCIe
 ├── Devices
 ├── Power
 └── Security
```

This is a diagnostic view, not the fundamental API.

---

## 215. Logging

Early boot logging should remain available until the normal logging system starts.

Logs may transition from firmware console to kernel buffer to managed logging service.

---

## 216. Crash Diagnostics

Fatal crashes may persist minimal data through reserved memory, firmware storage, crash partition, or remote debugger depending on available hardware.

---

## 217. Boot Measurements

Boot-time performance measurements should be possible without requiring a graphical environment.

---

## 218. Compatibility Invariants

1. Hardware-specific knowledge should terminate below the Universal Hardware Interface whenever possible.
2. The same UHI semantics may be implemented by firmware, compatibility HAL, hypervisor, or hosted backend.
3. Native WitOS hardware is optional; legacy hardware remains valid.
4. Descriptors describe hardware; capabilities grant authority.
5. Drivers receive only the device resources they require.
6. The kernel must not depend on high-level device semantics.
7. Applications normally consume semantic resources, not MMIO/IRQ hardware.
8. CPU topology, NUMA, heterogeneous cores, and device locality are first-class hardware properties.
9. DMA authority must be explicit and isolated where hardware allows.
10. Firmware should provide mechanisms and hardware description, not become a second operating system.
11. Missing optional hardware must result in unavailable capabilities, not platform fragmentation.
12. UHI must be language-neutral and must not depend on C# object layout.
13. Hotplug and runtime hardware changes are normal events.
14. Hardware security features improve guarantees but are not mandatory.
15. Compatibility backends must not dictate the native UHI architecture.

---

## 219. Deferred Questions

### Native ABI

```text
binary descriptor format
function table vs messages
boot handoff structure
runtime firmware calls
```

### Kernel Interface

```text
exact MMU primitives
interrupt API
timer API
context switching
```

### Driver Runtime

```text
driver process model
managed vs NativeAOT
driver restart
driver package structure
```

### Device Matching

```text
semantic IDs
legacy vendor IDs
version negotiation
```

### DMA

```text
buffer API
IOMMU domain lifetime
device sharing
```

### Power

```text
platform sleep states
device power transitions
CPU frequency policy
```

### Secure Hardware

```text
TPM integration
secure elements
measured boot
firmware signing
```

### Hardware Certification

```text
formal levels
test suite
vendor tooling
```

---

## 220. Relationship to Future RFCs

This RFC interacts strongly with:

```text
RFC 0008 — Storage & Persistent State
    block resources, persistent memory, boot storage

RFC 0009 — Presentation, Shell & Input
    display hardware, GPU, input devices

RFC 0010 — Application Packaging
    driver packages and native assets
```

Future RFCs may also define Driver Architecture, Power Management, Boot & Update Architecture, Firmware ABI, and Hardware Certification in greater detail.

---

## 221. Summary

The Universal Hardware Interface establishes the lowest stable semantic boundary of WitOS.

Below it may exist:

```text
firmware
vendor hardware
legacy PC interfaces
hypervisors
host operating systems
architecture-specific code
```

Above it exists one coherent WitOS hardware model.

The interface exposes:

```text
processors
memory
interrupts
timers
devices
MMIO
DMA
IOMMU
power
reset
security roots
hardware topology
```

without forcing higher layers to understand every vendor and machine.

Native WitOS hardware can implement the interface directly.

Existing hardware can implement the same interface through compatibility backends.

Virtual machines can implement it through paravirtualized devices.

The defining principle is:

> **The device should bring its hardware personality to WitOS; WitOS should not need to contain the personality of every device ever manufactured.**
