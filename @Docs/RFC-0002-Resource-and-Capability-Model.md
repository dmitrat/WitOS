# WitOS
## RFC 0002 — Resource & Capability Model
### Draft v0.1

## 1. Status

Draft.

This document defines the initial resource and capability model of WitOS.

It is intentionally independent of:

- a particular kernel implementation;
- a particular CPU architecture;
- a particular transport protocol;
- a particular storage system;
- a particular user-interface framework;
- whether a resource is local or remote.

The concepts defined here are intended to become part of the stable semantic foundation of WitOS.

---

## 2. Motivation

Traditional operating systems expose many fundamentally different object classes:

```text
file
directory
socket
process
thread
device
window
GPU
network interface
pipe
shared memory
service
remote endpoint
```

These distinctions are partly semantic and partly historical.

They also tend to encode assumptions that are increasingly less appropriate for modern systems:

- resources are local unless explicitly made remote;
- a machine is the natural execution boundary;
- devices belong directly to the operating system;
- applications discover platforms rather than capabilities;
- access control is commonly attached to paths, users, processes, or global namespaces;
- local and distributed operations use substantially different programming models.

WitOS attempts to replace this fragmented model with two fundamental concepts:

> **Resource** — something the system can expose, use, compose, locate, or manage.

> **Capability** — an unforgeable authority granting a specific form of access to a resource.

---

## 3. Core Principle

The native WitOS programming model should not primarily ask:

> What operating system am I running on?

or:

> What type of machine is this?

Instead it should ask:

> What resources are available to me, and what capabilities have I been granted over them?

For example, an application should not need:

```csharp
if (OperatingSystem.IsAndroid())
{
    OpenCameraUsingAndroidApi();
}
```

It should instead request:

```csharp
ICamera? camera =
    await context.Resources.TryAcquireAsync<ICamera>();
```

If a suitable camera exists and policy grants access, the application receives an appropriate capability.

The underlying implementation may be:

```text
phone camera
USB webcam
network camera
virtual camera
remote camera
test implementation
```

The application need not distinguish these unless their differing properties matter to its behavior.

---

## 4. Resource

A **resource** is an identifiable system entity that exposes one or more capabilities.

A conceptual base interface:

```csharp
public interface IResource
{
    ResourceId Id { get; }

    ResourceDescriptor Descriptor { get; }
}
```

A resource is not necessarily a physical object.

Examples include:

```text
physical SSD
file-like persistent object
GPU
CPU execution pool
camera
display
window surface
audio endpoint
network interface
database
remote service
compute cluster
virtual filesystem
encrypted storage view
application state store
```

A resource may itself be composed from other resources.

For example:

```text
EncryptedStorage
        │
        └── LocalStorage

ReplicatedStorage
        ├── LaptopStorage
        ├── PhoneStorage
        └── CloudStorage
```

The consumer interacts with the composed resource rather than necessarily knowing its internal topology.

---

## 5. Resource Identity

Resources require stable identities.

Conceptually:

```csharp
public readonly struct ResourceId
{
    ...
}
```

A ResourceId should identify a logical resource, not merely its current location.

Bad model:

```text
Resource identity =
machine A + disk 2 + path /foo/bar
```

Preferred model:

```text
Resource identity =
globally or domain-unique logical identifier
```

The resource may subsequently move or acquire replicas without changing its logical identity.

The exact representation of ResourceId is intentionally unspecified by this RFC.

Possible implementations may include:

- random 128-bit or 256-bit identifiers;
- cryptographic identities;
- hierarchical identities;
- domain-scoped identifiers.

---

## 6. Resource Descriptor

A resource should expose descriptive information separately from authority.

Example:

```csharp
public sealed record ResourceDescriptor(
    ResourceKind Kind,
    IReadOnlySet<ResourceFeature> Features,
    ResourceProperties Properties);
```

A descriptor answers:

> What is this resource capable of?

It must not itself imply:

> What am I allowed to do with it?

This distinction between **description** and **authority** is fundamental.

---

## 7. Capability

A capability is an unforgeable token or reference representing authority over a resource.

Conceptually:

```csharp
public interface ICapability<out TResource>
    where TResource : IResource
{
    ResourceId ResourceId { get; }

    TResource Resource { get; }
}
```

In practice the interface may not expose the raw resource directly, but the semantic rule remains:

> Possession of the capability is the authority.

For example:

```csharp
ICameraCapture camera;
```

may itself effectively be a capability.

The application does not separately ask:

```csharp
if (SecurityManager.CanUseCamera(currentProcess))
```

on every operation.

If it legitimately holds an `ICameraCapture`, then the operation is authorized within the constraints of that capability.

---

## 8. Capabilities Are Not Global Permissions

A capability should be more specific than traditional global permissions.

Instead of:

```text
Application X:
    CAMERA = allowed
```

WitOS should prefer:

```text
Application X:
    Capture capability for Camera #17
    valid while application is active
```

or:

```text
Read capability for Storage Resource A
Write capability for Storage Resource B
```

This makes authority:

- explicit;
- narrow;
- delegable;
- revocable;
- inspectable.

---

## 9. Capability Granularity

Different operations over the same logical resource should be representable by different capabilities.

For storage:

```text
IStorageRead
IStorageWrite
IStorageEnumerate
IStorageDelete
IStorageAdmin
```

For camera:

```text
ICameraPreview
ICameraCapture
ICameraConfiguration
```

For compute:

```text
IComputeSubmit
IComputeMonitor
IComputeAdmin
```

Possession of one does not imply possession of the others.

---

## 10. Capability Attenuation

Capabilities should be reducible.

Given:

```text
read + write + delete
```

a component should be able to derive:

```text
read only
```

and pass the weaker capability to another component.

Conceptually:

```csharp
var readOnly =
    storage.Restrict(StorageAccess.Read);
```

A holder may reduce its authority but may not create greater authority than it already possesses.

---

## 11. Capability Delegation

Capabilities may be transferable where policy permits.

For example:

```text
Application
    │
    └── gives read capability
            ↓
         Plugin
```

The plugin can now read the resource without being granted access to all application storage.

Delegation may be:

- permanent;
- temporary;
- one-shot;
- scoped to a session;
- limited by operation;
- limited by amount;
- limited by time.

---

## 12. Capability Revocation

Capabilities must support revocation semantics.

Potential reasons include:

- user permission revoked;
- application suspended;
- device disconnected;
- resource moved;
- security policy changed;
- temporary capability expired;
- remote provider disappeared.

Revocation should be observable.

---

## 13. Capability Lifetime

Every capability has a lifetime.

Possible lifetimes include:

```text
operation
scope
session
application lifetime
user session
persistent grant
device lifetime
```

The lifetime should be explicit where relevant.

---

## 14. Discovery Is Separate from Acquisition

Resource discovery and resource access are different operations.

An application may discover:

```text
2 displays
1 camera
3 audio outputs
```

without receiving authority to use all of them.

Conceptually:

```csharp
var cameras =
    await Resources.DiscoverAsync<ICameraDescriptor>();
```

Then:

```csharp
var capture =
    await Resources.AcquireAsync<ICameraCapture>(
        cameras[0].Id);
```

Acquisition may involve:

- policy;
- user approval;
- security checks;
- resource scheduling;
- remote negotiation;
- exclusive-access arbitration.

---

## 15. Resource Queries

Applications should generally request resources by properties rather than by platform-specific identifiers.

Example:

```csharp
var camera = await Resources.AcquireAsync<ICamera>(
    new CameraRequirements
    {
        MinimumWidth = 1920,
        MinimumHeight = 1080,
        FrontFacingPreferred = true
    });
```

Compute example:

```csharp
var compute = await Resources.AcquireAsync<IComputeResource>(
    new ComputeRequirements
    {
        MinimumMemory = 8.GB(),
        FloatingPoint = FloatCapability.FP32,
        AcceleratorPreferred = true
    });
```

The resource manager chooses a suitable implementation.

---

## 16. Requirements and Preferences

Resource requests should distinguish hard requirements from preferences.

Example:

```text
Requires:
    RAM >= 4 GB
    graphics surface support

Prefers:
    touch
    HDR
    hardware video decoder
```

A resource that fails a requirement is invalid.

A resource that fails a preference may still be selected.

---

## 17. Locality

Locality is a resource property, not a separate programming universe.

Possible locality values may include:

```text
InProcess
LocalMachine
LocalDevice
LocalNetwork
Remote
Replicated
Unknown
```

An application may choose not to care.

For latency-sensitive work, locality can become a constraint.

The key principle is:

> Locality should be explicit when physically relevant, not implicitly embedded into every API.

---

## 18. Local and Remote Resources

A local and remote resource should expose equivalent semantic interfaces where practical.

Example:

```csharp
IObjectStore store;
```

may represent:

```text
local SSD-backed object store
remote NAS
cloud storage
replicated storage cluster
```

The API should not artificially pretend that all implementations have identical performance or failure behavior.

---

## 19. Failure Model

Distributed resources mean that failure must be part of the native semantics.

A resource operation may fail because:

```text
resource disappeared
network partition
device removed
provider crashed
capability revoked
timeout
resource migrated
version conflict
```

Such failures must not be treated as exceptional architecture-specific edge cases.

---

## 20. Resource State

Resources may have lifecycle states.

Example:

```text
Available
Busy
Suspended
Offline
Migrating
Degraded
Failed
Disposed
```

Applications should normally observe changes asynchronously rather than poll continuously.

---

## 21. Resource Composition

Resources should be composable.

For example:

```text
PhysicalDisk
     ↓
EncryptedStorage
     ↓
ReplicatedStorage
     ↓
VersionedObjectStore
     ↓
FilesystemView
```

Each layer exposes another resource interface.

Applications may use the abstraction appropriate to their needs.

---

## 22. Resource Views

A resource may expose multiple views.

Example:

```text
Storage Object
   ├── Byte Stream View
   ├── Versioned Object View
   ├── File View
   └── Memory-Mapped View
```

Views allow compatibility APIs to coexist with richer native semantics.

---

## 23. Resource Graph

Resources naturally form a graph rather than a strict tree.

Example:

```text
Application
   │
   ├── Window
   │     └── Display
   │
   ├── Document
   │     ├── Local Replica
   │     └── Remote Replica
   │
   └── Compute Job
         ├── GPU
         └── Dataset
```

Dependencies should therefore be represented explicitly where useful.

---

## 24. Names and Identity

Human-readable names and resource identity are separate concepts.

Example:

```text
Name:
    "report.pdf"

ResourceId:
    8f39...
```

Renaming or moving a resource should not inherently change resource identity.

---

## 25. Namespaces

WitOS may expose namespaces for usability and compatibility.

Examples:

```text
Documents/
Projects/
Applications/
Devices/
```

However, a namespace is a **view for discovery and organization**, not necessarily the fundamental identity system.

Traditional filesystem paths may be implemented as namespace paths over resource identities.

---

## 26. Application Context

Every application executes within an `ApplicationContext`.

Conceptually:

```csharp
public interface IApplicationContext
{
    ApplicationId ApplicationId { get; }

    IResourceResolver Resources { get; }

    ICapabilityContext Capabilities { get; }

    IApplicationLifecycle Lifecycle { get; }
}
```

The context defines the application's current view of the environment.

It is not equivalent to a Unix process.

---

## 27. Application Identity

An application has a stable logical identity distinct from PID, address space, current machine, or current process.

This distinction enables future support for:

- application handoff;
- migration;
- suspend/resume;
- distributed execution;
- replicated application state.

---

## 28. Resource Ownership

WitOS should avoid assuming that every resource is permanently owned by a process.

Possible relationships include:

```text
owned
borrowed
shared
leased
delegated
system-provided
externally-provided
```

Ownership and lifetime need to be modeled independently.

---

## 29. Leases

Some resources may use leases.

A lease grants temporary access and may require renewal.

Example:

```text
GPU compute slot
exclusive camera configuration
remote worker
hardware codec
```

A lease may expire if the application disappears.

---

## 30. Exclusive and Shared Resources

Resources may define sharing semantics.

Possible policies:

```text
Shared
Exclusive
SharedRead
SingleWriter
Multiplexed
Scheduled
```

Resource arbitration belongs to resource-management policy rather than the minimal kernel.

---

## 31. Resource Scheduling

Some resources are schedulable.

Examples:

```text
CPU
GPU
NPU
hardware encoder
network bandwidth
remote compute cluster
```

Applications request capability to use the resource.

They should not necessarily control the physical scheduler.

---

## 32. Compute Resources

Compute should be modeled explicitly.

Possible implementations:

```text
local CPU
local GPU
local NPU
remote CPU
remote GPU
cluster
cloud service
FPGA
specialized accelerator
```

The goal is not to force all compute models into one lowest-common-denominator interface.

Instead, resources may expose multiple capability interfaces.

---

## 33. Capability Interfaces

Capabilities should generally be represented by semantic interfaces.

Example:

```csharp
public interface IReadableStorage
{
    ValueTask<int> ReadAsync(
        StorageOffset offset,
        Memory<byte> destination,
        CancellationToken cancellationToken = default);
}
```

An application obtains the interface only if it has the corresponding authority.

This combines API and authority into one object.

---

## 34. Capability Transport

If capabilities can reference remote resources, they must be transferable across machine boundaries.

The transport representation must not simply serialize an internal pointer.

A remote capability might represent:

```text
resource identity
grant identity
permissions
expiration
issuer
cryptographic proof
routing information
```

At the programming-model level, the same capability interface should remain usable.

---

## 35. Capability Forgery

Capabilities must not be forgeable by constructing arbitrary identifiers.

For example:

```csharp
new ResourceId("camera-1")
```

must not grant camera access.

A `ResourceId` identifies.

A capability authorizes.

---

## 36. Capability Persistence

Some capabilities may be persistable.

For example:

```text
user permanently grants application
read access to document X
```

After restart, the application may regain that capability.

Persistent capabilities require a secure rehydration mechanism involving the authority that issued them.

---

## 37. User Consent

Some capability acquisitions may require user consent.

Example:

```csharp
var microphone =
    await Resources.RequestAsync<IMicrophoneCapture>();
```

The application itself should not control the authoritative security presentation.

---

## 38. User Identity and Capability Authority

User identity and capability authority are related but not identical.

A user account may be one source of policy, but resource access should still ultimately be represented as capabilities.

---

## 39. System Services

System services should themselves participate in the capability model.

Instead of:

```text
global privileged daemon accessible by everyone
```

prefer:

```text
service endpoint capability
```

Applications receive access only to the services appropriate to them.

---

## 40. IPC

Local inter-process communication should use the same conceptual model as other resource interactions.

An IPC endpoint is itself a resource/capability.

Passing a capability through IPC delegates authority.

---

## 41. Service Discovery

Discovery should return descriptions or handles suitable for requesting authority.

Discovery does not automatically imply use permission.

---

## 42. Resource Providers

A resource is exposed by a provider.

Providers may include:

```text
kernel
managed driver
system service
application
remote machine
cloud service
virtualization layer
firmware
```

Consumers should not generally care which category provides the resource.

---

## 43. Resource Provider Failure

Providers may fail independently.

Example:

```text
Wi-Fi driver crashes
```

The Wi-Fi resource transitions to unavailable; the provider may restart.

Provider failure should not normally imply OS failure.

---

## 44. Hardware as Resources

Physical devices enter the managed system as resources.

The low-level hardware layer may expose primitives such as:

```text
MMIO region
interrupt
DMA domain
device identity
power-control capability
```

A managed driver consumes those capabilities and exposes a higher-level resource.

---

## 45. Resource Transformation

One resource can transform another.

Examples:

```text
Block device
    ↓ filesystem provider
Filesystem resource

Network interface
    ↓ TCP/IP provider
Network service
```

Transformation components should themselves run with only the capabilities required for their role.

---

## 46. Compatibility APIs

Existing .NET APIs must map naturally onto the resource model.

Example:

```csharp
File.OpenRead(path)
```

may conceptually perform:

```text
path namespace resolution
        ↓
storage resource identity
        ↓
read capability
        ↓
stream view
        ↓
FileStream
```

Existing .NET code does not need to know that this occurs.

---

## 47. No Mandatory Distributed Transparency

WitOS must not pretend that a remote object is physically identical to an in-process object.

That approach hides:

- latency;
- partial failure;
- serialization cost;
- bandwidth;
- connectivity;
- consistency.

Instead, the interfaces may be semantically uniform while resource metadata and asynchronous operations expose physical constraints.

---

## 48. Resource Characteristics

Common characteristics may include:

```text
Locality
Latency
Bandwidth
Capacity
Durability
Availability
Consistency
EnergyCost
MonetaryCost
TrustLevel
PrivacyDomain
Mobility
Persistence
```

Not every resource needs every property.

The characteristics system should be extensible.

---

## 49. Resource Selection

The system may choose among equivalent providers based on requirements and policy.

Applications may specify constraints such as:

```text
must be local
must be persistent
must survive reboot
must remain inside trust domain X
```

but should avoid selecting an implementation unnecessarily.

---

## 50. Resource Policy

Selection policy may consider:

```text
performance
battery level
network cost
privacy
user preference
resource availability
current load
energy consumption
monetary cost
```

Applications should declare constraints.

The system should decide policy where possible.

---

## 51. Device Handoff

Because application identity and resource identity are independent of one process or device, a logical application may change execution environment.

Some capabilities may follow automatically; others may need reacquisition.

---

## 52. Capability Equivalence vs Resource Equivalence

During migration, the same logical capability type need not imply the same physical provider.

If the specific resource matters, the application may retain its ResourceId requirement.

---

## 53. Resource Mobility

Some resources themselves may move.

A compute job may migrate.

A virtual display session may move to another physical display.

The resource model should not require identity changes merely because implementation location changes.

---

## 54. Resource Replication

Replicated resources may have several physical manifestations.

The application may simply hold one logical storage object.

Replication policy belongs to the storage service.

---

## 55. Consistency

Distributed resources may expose consistency characteristics.

Possible values include:

```text
Strong
Session
Eventual
Immutable
Versioned
ApplicationDefined
```

The resource model should not hard-code one distributed consistency model globally.

---

## 56. Versioning

Persistent resources should be able to expose versions.

Writes may optionally use optimistic concurrency.

This makes multi-device use easier to model than assuming one local mutable file.

---

## 57. Streams and Objects

WitOS should support both:

```text
stream semantics
```

and:

```text
object/version semantics
```

A byte stream remains essential for compatibility and many real workloads.

---

## 58. Resource API Evolution

Resource interfaces must be extensible without forcing platform fragmentation.

Capability discovery should prefer feature detection over OS version checks.

---

## 59. Capability Versioning

Capability interfaces themselves require compatibility rules.

Version compatibility should preferably follow normal .NET assembly/API compatibility principles rather than invent another independent type system.

---

## 60. Typed Capabilities

The .NET type system should be used aggressively.

Prefer:

```csharp
ICameraCapture camera;
```

over generic flag-based permission objects.

---

## 61. Unsafe Resources

Some low-level resources expose unsafe operations.

Example:

```text
raw MMIO
physical memory
DMA mapping
interrupt binding
```

These capabilities should be strongly restricted.

---

## 62. Privilege Is a Graph, Not a Ring

WitOS should avoid assuming that privilege is a single linear hierarchy.

Each component receives only the capabilities required for its function.

Hardware privilege levels remain necessary for enforcement, but software authority should be finer-grained.

---

## 63. Capability Root

At boot, some trusted root must initially possess authority over fundamental resources.

The boot process attenuates and delegates these capabilities to system components.

---

## 64. Bootstrapping

The initial resource graph may be built approximately as:

```text
firmware/hardware environment
        ↓
raw hardware capabilities
        ↓
kernel
        ↓
device manager
        ↓
drivers
        ↓
system resources
        ↓
application framework
        ↓
applications
```

---

## 65. Auditability

Capability transfers should be auditable where security requires it.

Audit information must not require every ordinary operation to become prohibitively expensive.

---

## 66. Privacy

Resource metadata itself may be sensitive.

Therefore resource discovery may itself require permission or return privacy-reduced descriptors.

---

## 67. Resource Visibility

Applications need not see every resource known to the system.

Their `ResourceResolver` may present a filtered view.

---

## 68. Sandboxing

An application sandbox can therefore be described as:

```text
execution isolation
+
visible resource set
+
granted capability set
```

rather than merely:

```text
restricted process
```

---

## 69. Plugins

Plugins should not implicitly inherit all application authority.

The parent application can explicitly delegate only what the plugin requires.

---

## 70. Application-to-Application Cooperation

Applications may deliberately exchange capabilities.

User actions such as:

```text
Open With...
Share...
```

can naturally be implemented as capability transfers.

---

## 71. Clipboard and Drag-and-Drop

Dragging a document into another application may transfer:

```text
read capability
```

or:

```text
temporary object capability
```

rather than a filesystem path.

---

## 72. URLs and External References

External identifiers such as URLs are not capabilities by themselves.

Identity/address is not authority.

---

## 73. Serialization

Resource references embedded in serialized data must not automatically serialize authority.

Serializing a live capability requires explicit security semantics.

---

## 74. Resource Equality

Two capabilities may refer to the same ResourceId while granting different authority.

Therefore resource equality and capability equality are different concepts.

---

## 75. Cancellation

All potentially blocking resource operations should support cancellation.

This is especially important because the same API may represent local or remote providers.

---

## 76. Async by Default for External Resources

Operations that may involve hardware, storage, network, user approval, remote compute, or device acquisition should generally have asynchronous APIs.

---

## 77. Synchronous Compatibility

Standard .NET synchronous APIs must remain usable where required.

Native WitOS APIs should prefer asynchronous forms when interacting with uncertain-latency resources.

---

## 78. Performance Escape Hatches

The abstraction must not make high-performance workloads impossible.

Low-level APIs may expose:

```text
shared memory
memory mapping
zero-copy buffers
pinned memory
DMA buffers
GPU-visible memory
batch operations
```

through appropriately privileged capabilities.

---

## 79. Zero-Copy

Where two components share a trust or protection domain, capability-controlled shared buffers may avoid copying.

The same logical resource pipeline should support both safe copying paths and zero-copy optimized paths.

---

## 80. Remote Optimization

Remote resource APIs should support batching and streaming to avoid accidental chatty RPC patterns.

A uniform semantic API must not imply naive per-property remote calls.

---

## 81. Resource Caching

Caching is an implementation strategy that may produce new resource views.

Applications needing strict freshness can request the appropriate semantics.

---

## 82. Resource Trust Domains

Resources should expose trust/security domain information when relevant.

Applications handling sensitive data may require a minimum trust domain before delegating information.

---

## 83. Data Locality Constraints

A compute request may express that data must not leave a device, jurisdiction, or trust domain.

This allows resource scheduling to respect privacy requirements.

---

## 84. Cost as a Resource Characteristic

Some resources may have explicit or implicit cost.

Examples:

```text
mobile network usage
cloud GPU
paid API
battery consumption
```

Applications may specify maximum cost or leave policy to the system/user.

---

## 85. Energy Awareness

Energy is particularly important on mobile and embedded devices.

A compute resource may advertise performance, energy efficiency, and current power state.

---

## 86. Resource Priorities

Applications may express intent:

```text
Interactive
Foreground
Background
Maintenance
RealTimeCandidate
BestEffort
```

These are requests, not unconditional authority.

---

## 87. Real-Time Requirements

Hard real-time resources may require stronger guarantees than ordinary managed workloads.

The model must not falsely guarantee real-time behavior merely because the API exists.

---

## 88. Physical Resource Constraints

Some resources cannot be virtualized transparently.

The resource system must expose limitations rather than pretending unlimited logical instances exist.

---

## 89. Virtual Resources

Software may expose resources that have no direct physical equivalent.

These participate in the same discovery and capability model as physical resources.

---

## 90. Resource Factories

Applications or services may create resources.

Creation itself requires appropriate capability.

A resource factory is therefore another capability.

---

## 91. Persistence vs Existence

Persistent resources may outlive all current processes.

Transient resources may disappear when their provider disappears.

This property should be explicit.

---

## 92. Stable Application State

Application state intended to survive suspension, migration, or restart should itself be stored in persistent resources.

The runtime should not assume that preserving arbitrary process memory is the only persistence mechanism.

---

## 93. Snapshotting

The system may optionally support snapshots of execution state.

However, snapshotting is an optimization and migration mechanism, not the only application persistence model.

---

## 94. Compatibility with Process Semantics

Standard .NET concepts such as:

```text
Process
Thread
Environment
```

may still exist for compatibility.

They should be mapped onto WitOS execution mechanisms.

---

## 95. Resource Model and Kernel Boundary

The full resource model does not belong in the kernel.

The kernel should implement only primitives required to enforce resources and capabilities safely.

---

## 96. Kernel Capabilities vs Semantic Capabilities

Two levels may exist:

```text
Kernel capability
    ↓
low-level authority enforced by kernel

Semantic capability
    ↓
high-level authority implemented by managed services
```

This layered capability model reduces the amount of policy required inside the kernel.

---

## 97. Capability Enforcement

Enforcement may use several mechanisms:

```text
MMU protection
process/address-space isolation
managed type safety
opaque object references
cryptographic tokens
kernel object tables
service-level authorization
```

The architecture does not require one mechanism for every capability.

---

## 98. Native Code

Native components cannot be assumed to obey managed type safety.

They therefore require stronger isolation or explicit trust.

---

## 99. Resource Model and .NET

WitOS should feel natural to .NET developers.

Desired API qualities:

- strongly typed;
- async-friendly;
- `IDisposable` / `IAsyncDisposable` where appropriate;
- `CancellationToken` support;
- generic constraints;
- `Span<T>` / `Memory<T>` for efficient data paths;
- standard exception conventions;
- ordinary dependency injection compatibility;
- no separate proprietary object system.

---

## 100. Example: Camera Application

```csharp
await using ICameraCapture camera =
    await context.Resources.AcquireAsync<ICameraCapture>(
        new CameraRequirements
        {
            PreferredFacing = CameraFacing.Front
        });

await using CameraFrame frame =
    await camera.CaptureAsync(cancellationToken);
```

Possible underlying implementation:

```text
Application
     ↓
ICameraCapture capability
     ↓
Camera Service
     ↓
Managed Camera Driver
     ↓
DMA / IRQ / MMIO capabilities
     ↓
Hardware
```

---

## 101. Example: Storage

A native WitOS application may use a logical storage object backed by versioned and replicated storage, while a legacy .NET application may see the same data through `FileStream`.

---

## 102. Example: Compute

An application may request a compute resource with memory, latency, and accelerator requirements.

Candidates may include local GPU, local NPU, LAN workstation, or remote server.

---

## 103. Example: Application Plugin

A parent application delegates only document-read and GPU-compute capabilities to a plugin, withholding camera, network, and write authority.

---

## 104. Example: Device Handoff

Persistent logical capabilities are restored; physical capabilities such as display, GPU, and touch are reacquired on the destination device.

---

## 105. Architectural Invariants

1. A ResourceId does not grant authority.
2. Authority cannot be increased through capability attenuation or delegation.
3. Applications should not require device-class or OS-name checks for normal behavior.
4. Locality must be observable where relevant but must not create a completely separate programming model.
5. Resource discovery does not imply resource access.
6. Capabilities should be explicit, narrow, and revocable where practical.
7. The process is not the fundamental application identity.
8. The resource model should remain usable for both local-only and distributed systems.
9. Standard .NET compatibility APIs remain supported.
10. The semantic resource model belongs primarily above the privileged kernel.

---

## 106. Questions Deferred to Future RFCs

This RFC intentionally does not fully define:

- exact ResourceId format;
- kernel vs managed capability representation;
- distributed capability protocol;
- storage replication and consistency;
- application package format;
- user consent model;
- IPC transport;
- provider selection algorithms;
- scheduling policy.

---

## 107. Suggested Next RFCs

```text
RFC 0003 — Application & Lifecycle Model
RFC 0004 — Security, Identity & Capability Delegation
RFC 0005 — Execution, Scheduling & Compute Model
RFC 0006 — IPC and Local/Remote Communication Model
RFC 0007 — Universal Hardware Interface
RFC 0008 — Storage & Persistent Object Model
RFC 0009 — UI, Display & Input Model
```

---

## 108. Summary

WitOS models the computing environment as a graph of resources governed by capabilities.

A resource describes:

> what exists.

A capability describes:

> what the holder is authorized to do with it.

Resources may be physical, virtual, local, remote, replicated, persistent, or transient without requiring separate fundamental programming models.

The defining principle is:

> **Resources describe the world. Capabilities define authority over it. Location and implementation are properties, not platform boundaries.**
