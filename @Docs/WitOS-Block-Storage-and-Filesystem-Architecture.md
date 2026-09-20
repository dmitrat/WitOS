# WitOS
## Block Storage & Filesystem Architecture
### Draft v0.1

## 1. Status

Draft.

This document defines the implementation architecture of block storage, volume composition, filesystems, and their relationship to the WitOS kernel and standard .NET file APIs.

It concretizes the storage principles defined by:

```text
RFC 0002 — Resource & Capability Model
RFC 0004 — Security, Identity & Capability Delegation
RFC 0006 — IPC & Local/Remote Communication Model
RFC 0007 — Universal Hardware Interface
RFC 0008 — Storage & Persistent State Model
```

It does not redefine the higher-level persistent-state semantics of RFC 0008.

---

# 2. Central Principle

The WitOS kernel does not implement filesystems.

It also does not need to understand:

```text
files
directories
paths
partitions
RAID
volumes
ext4
FAT
encryption
```

The kernel provides the mechanisms required to construct storage services:

```text
memory
address spaces
DMA
interrupts
channels
capabilities
scheduling
device memory
```

Storage functionality is implemented above the kernel.

---

# 3. Architecture Overview

The intended storage stack is:

```text
Applications
     │
     │ System.IO / OutWit.OS.Storage
     ▼
┌───────────────────────────────┐
│ Filesystem / Namespace Layer  │
│                               │
│ ext4                          │
│ FAT / exFAT                   │
│ other providers               │
└──────────────┬────────────────┘
               │
          IBlockStorage
               │
┌──────────────▼────────────────┐
│ Block Composition Layer       │
│                               │
│ partitions                    │
│ RAID                          │
│ encryption                    │
│ mirroring                     │
│ caching                       │
│ thin volumes                  │
│ future snapshots/COW          │
└──────────────┬────────────────┘
               │
          IBlockStorage
               │
┌──────────────▼────────────────┐
│ Device Drivers                │
│                               │
│ Virtio                        │
│ NVMe                          │
│ USB Mass Storage              │
│ SD                            │
│ remote providers              │
└──────────────┬────────────────┘
               │
        MMIO / IRQ / DMA
        capabilities
               │
┌──────────────▼────────────────┐
│ WitOS Kernel                  │
└──────────────┬────────────────┘
               │
              UHI
               │
            Hardware
```

---

# 4. The Kernel Has No Disk Abstraction

The kernel does not require a kernel object called:

```text
Disk
```

A storage controller driver receives low-level capabilities such as:

```text
device configuration
MMIO
interrupt
DMA
reset
power
```

and exposes a higher-level storage resource.

For example:

```text
Virtio block device
      ↓
VirtioBlockDriver
      ↓
IBlockStorage
```

The `IBlockStorage` abstraction belongs above the minimal kernel ABI.

---

# 5. Managed Storage by Default

Once the managed WitOS environment is available, hardware-independent storage functionality should be implemented in managed code by default.

This includes:

```text
filesystem implementations
partition managers
RAID
encryption
caching
volume management
storage diagnostics
```

Native libraries above the kernel may be used when technically justified, but they are exceptional or compatibility implementations rather than the preferred architecture.

---

# 6. Hardware Independence

A managed filesystem may be compiled separately for:

```text
x64
ARM64
RISC-V
```

without being hardware-dependent.

Hardware independence means that its source code depends only on stable storage contracts and not on:

```text
CPU instruction set details
interrupt controllers
PCI configuration
specific storage controllers
DMA registers
```

---

# 7. Fundamental Block Abstraction

The central low-level storage abstraction is:

```text
IBlockStorage
```

A preliminary shape is:

```csharp
public interface IBlockStorage : IAsyncDisposable
{
    BlockStorageInfo Info { get; }

    ValueTask ReadAsync(
        long offset,
        Memory<byte> buffer,
        CancellationToken cancellationToken = default);

    ValueTask WriteAsync(
        long offset,
        ReadOnlyMemory<byte> buffer,
        WriteOptions options = default,
        CancellationToken cancellationToken = default);

    ValueTask FlushAsync(
        CancellationToken cancellationToken = default);
}
```

The final API may use logical block numbers rather than byte offsets.

That is an API design question rather than an architectural distinction.

---

# 8. No Stateful Seek at the Block Layer

The block API should not expose:

```text
Seek
Read
Seek
Write
```

as shared mutable device position.

Each I/O operation should identify its own location.

For example:

```text
Read(offset, buffer)
Write(offset, buffer)
```

This permits concurrent I/O and avoids shared cursor synchronization.

Stateful `Seek` belongs to higher-level abstractions such as:

```text
Stream
FileStream
```

---

# 9. Block Storage Characteristics

A storage resource should expose enough physical and semantic information for efficient and correct operation.

Conceptually:

```csharp
public sealed record BlockStorageInfo
{
    public long Length { get; init; }

    public int LogicalBlockSize { get; init; }
    public int PhysicalBlockSize { get; init; }

    public int RequiredAlignment { get; init; }
    public int OptimalTransferSize { get; init; }
    public int MaxTransferSize { get; init; }

    public int MaxOutstandingRequests { get; init; }

    public bool IsReadOnly { get; init; }

    public bool SupportsFlush { get; init; }
    public bool SupportsForceUnitAccess { get; init; }
    public bool SupportsDiscard { get; init; }
}
```

Exact fields remain subject to implementation experience.

---

# 10. FAT-Specific Geometry Must Not Leak Upward

The generic WitOS block API must not inherit restrictions that exist only because a particular filesystem expects them.

For example:

```text
512–4096-byte sectors
power-of-two sector size
```

may be valid FAT/exFAT constraints.

They are not necessarily universal `IBlockStorage` constraints.

Filesystem providers validate the requirements they need.

---

# 11. Asynchronous I/O

Block I/O is asynchronous.

This applies even when the actual underlying device is extremely fast.

Reasons include:

```text
DMA completion
interrupt-driven devices
remote storage
queued storage controllers
RAID operations
encryption pipelines
```

The programming model should not assume synchronous completion.

---

# 12. Concurrent I/O

Unlike the existing `OutWit.Common.Fat.IBlockDevice` contract, the system-level `IBlockStorage` should permit multiple outstanding operations.

This is required to make effective use of:

```text
NVMe queues
virtio multiqueue
RAID
remote storage
high-throughput devices
```

Providers may expose limits through resource characteristics.

---

# 13. Sequential Providers

A provider that can process only one operation at a time remains valid.

Examples include:

```text
slow embedded media
Bluetooth-connected storage
legacy devices
```

Such a provider may internally serialize requests.

The limitation does not redefine the general contract.

---

# 14. Existing OutWit.Common.Fat

`OutWit.Common.Fat` already provides a managed filesystem implementation over a pluggable asynchronous block abstraction.

It supports:

```text
FAT12
FAT16
FAT32
exFAT
```

and can operate over:

```text
memory
file
stream
partition
cached block device
custom device provider
```

This architecture is directly aligned with the WitOS storage model.

---

# 15. Reuse Rather Than Fork

`OutWit.Common.Fat` should remain an independent portable OutWit library.

It should not be copied into the WitOS source tree and turned into WitOS-specific code.

Instead WitOS should provide an adapter/provider such as:

```text
OutWit.OS.Storage
       ↓
FAT filesystem adapter
       ↓
OutWit.Common.Fat
```

This preserves use of the same library on:

```text
Windows
Linux
macOS
WitOS
```

---

# 16. FAT Provider Role

FAT/exFAT are expected to remain useful in WitOS for:

```text
removable media
interoperability
small devices
boot-related storage
embedded systems
legacy storage
```

They are not intended to define the primary high-scale writable filesystem of WitOS.

---

# 17. ext4 as the Initial Primary Filesystem Candidate

For the first general-purpose writable WitOS filesystem, ext4 is the preferred candidate.

The choice is based on properties such as:

```text
large volume support
large files
extents
journaling
crash recovery
sparse files
hard links
symbolic links
efficient directories
mature on-disk format
wide external tooling support
```

The choice of ext4 does not make WitOS dependent on Linux architecture.

WitOS uses the ext4 on-disk format, not Linux VFS semantics.

---

# 18. ext4 Should Be a Managed Provider

The preferred long-term architecture is:

```text
IBlockStorage
      ↓
Managed Ext4 Provider
      ↓
filesystem namespace
```

The implementation may be based on:

```text
existing specifications
existing implementations
AI-assisted porting
compatibility testing against established ext4 tools
```

but should ultimately fit the managed WitOS service model.

---

# 19. Do Not Port Linux VFS

The project should not attempt to import the Linux storage architecture wholesale.

In particular, WitOS does not need to adopt:

```text
Linux VFS
Linux page cache
Linux block layer
Linux inode object model
Linux kernel driver architecture
```

merely to use the ext4 disk format.

---

# 20. Filesystem Provider Contract

The filesystem layer should expose semantic operations such as:

```text
open file
create file
enumerate directory
rename
delete
metadata
flush
open stream
resolve namespace reference
```

It should not expose ext4-specific details to ordinary applications.

---

# 21. Filesystem Is a Service

A filesystem implementation should normally execute as an isolated system service rather than as kernel code.

Conceptually:

```text
ext4 filesystem service
        │
        │ block capability
        ▼
IBlockStorage
```

Its failure should not inherently imply kernel failure.

---

# 22. Restartability

Where filesystem semantics permit, the service should be restartable.

A failure may result in:

```text
volume temporarily unavailable
      ↓
service restart
      ↓
journal replay / recovery
      ↓
volume remount
```

rather than:

```text
kernel panic
```

Not every failure can be transparently hidden from applications.

The architecture should nevertheless isolate the fault domain.

---

# 23. Capability Attenuation

A filesystem receives only authority over the block resource it manages.

Example:

```text
Physical disk
      ↓
Partition capability
      ↓
Filesystem service
```

The filesystem cannot access unrelated regions of the device unless explicitly granted that authority.

---

# 24. Partition Layer

Partitions are block-storage transformations.

For example:

```text
Physical Disk
      ↓
GPT Provider
      ├── EFI partition
      ├── System partition
      └── Data partition
```

Each partition may itself be exposed as an independent:

```text
IBlockStorage
```

with restricted range and capabilities.

---

# 25. Partition Identity

A partition resource should have its own logical resource identity.

Higher layers should not normally need to reason in terms of:

```text
disk 2
partition 3
```

They may instead receive a capability to the partition resource directly.

---

# 26. Storage Composition

A fundamental storage rule is:

> **A block-level transformation may consume one or more block resources and expose another block resource.**

This allows arbitrary composition.

---

# 27. Example Composition

```text
NVMe 0 ──┐
         ├── RAID1
NVMe 1 ──┘
            │
            ▼
       Encryption
            │
            ▼
        Partition
            │
            ▼
           ext4
```

The ext4 implementation receives only one `IBlockStorage`.

It does not know that multiple physical devices exist below it.

---

# 28. RAID Is Not a Filesystem Feature

RAID belongs below the filesystem.

Possible providers include:

```text
RAID0
RAID1
RAID5
RAID6
future erasure coding
```

Each provider exposes another block resource.

---

# 29. RAID as Managed Service

RAID logic should normally be implemented in managed code.

For example:

```csharp
public sealed class Raid1Storage : IBlockStorage
{
    private readonly IBlockStorage[] _members;
}
```

The RAID provider receives capabilities only for its member devices.

---

# 30. RAID Resource Identity

A RAID array is a logical resource independent of its current physical members.

For example:

```text
Disk A ─┐
        ├── RAID1 Resource
Disk B ─┘
```

If Disk B fails and is replaced:

```text
Disk A ─┐
        ├── same RAID1 Resource
Disk C ─┘
```

the logical volume identity may remain unchanged.

---

# 31. RAID Health

RAID resources should expose state such as:

```text
Healthy
Degraded
Rebuilding
Failed
```

and characteristics such as:

```text
redundancy level
usable capacity
member health
rebuild progress
```

These are resource characteristics, not filesystem metadata.

---

# 32. Hot Replacement

Replacing failed storage should not require remounting or redesigning the filesystem when the RAID provider can preserve the logical block resource.

The filesystem continues to see:

```text
same logical block resource
```

while the underlying composition changes.

---

# 33. Encryption

Block encryption is another composable transformation.

```text
IBlockStorage
      ↓
EncryptedBlockStorage
      ↓
IBlockStorage
```

The encryption provider may require:

```text
block storage capability
encryption key capability
```

and no broader machine authority.

---

# 34. Encryption Keys

Encryption keys should not be ambient globals.

They should be supplied through narrow secret/key capabilities.

This allows different volumes to have different trust and ownership policies.

---

# 35. Caching

Caching is also a storage policy/provider.

```text
IBlockStorage
      ↓
Cache Provider
      ↓
IBlockStorage
```

A cache may be appropriate for:

```text
high-latency storage
remote block devices
slow flash
```

and inappropriate or redundant for some high-performance local devices.

---

# 36. Existing BlockDeviceCached

The `OutWit.Common.Fat` experience demonstrates the value of explicit caching over high-latency block storage.

WitOS should generalize this idea using resource characteristics rather than special-casing individual device types.

---

# 37. Resource Characteristics

Storage resources may describe:

```text
latency
bandwidth
capacity
optimal transfer size
queue depth
durability
availability
locality
energy cost
monetary cost
trust domain
```

These characteristics can influence:

```text
caching
read-ahead
write combining
placement
scheduling
```

---

# 38. Remote Block Storage

A remote block-storage provider may expose:

```text
IBlockStorage
```

just as a local device does.

The filesystem contract remains unchanged.

However the resource must expose the physical realities of remote access:

```text
latency
failure
availability
bandwidth
```

---

# 39. Remote Does Not Mean Transparent Physics

WitOS does not promise that:

```text
remote block storage == local NVMe
```

It promises that both may participate in the same resource model.

Policies and applications may choose between them based on requirements.

---

# 40. Filesystem Above RAID

The recommended relationship is:

```text
filesystem
    ↓
logical volume
    ↓
RAID
    ↓
physical devices
```

rather than embedding RAID semantics into the first filesystem implementation.

---

# 41. Why Not Make ZFS/Btrfs the Fundamental Model

Integrated storage systems such as ZFS or Btrfs combine:

```text
filesystem
volume management
checksumming
RAID-like functions
snapshots
compression
```

This is useful in conventional operating systems.

WitOS instead prefers composable resources where these concerns can evolve independently.

Such filesystems may still be supported later as providers.

They simply do not define the foundational storage architecture.

---

# 42. Snapshots

Snapshots may eventually exist at different levels.

Examples:

```text
filesystem snapshot
block-level snapshot
application-state snapshot
distributed storage version
```

WitOS should not assume these are equivalent.

---

# 43. Thin Provisioning

Thin provisioning may be represented as another block provider:

```text
Physical/Remote Pool
       ↓
Thin Volume Provider
       ↓
IBlockStorage
```

It is not required for early milestones.

---

# 44. Discard / TRIM

Storage providers may optionally support:

```text
Discard
```

or equivalent semantics.

A filesystem may use it when freeing ranges.

Example:

```text
delete file
    ↓
filesystem frees extents
    ↓
Discard
    ↓
SSD TRIM / virtio discard
```

Unsupported discard must not prevent normal filesystem operation.

---

# 45. Flush Semantics

`FlushAsync()` must have strong, documented durability semantics.

It must not merely mean:

```text
request accepted by driver
```

It should mean that all prior relevant writes have reached the durability level promised by the resource.

---

# 46. Why Flush Matters

A journaling filesystem may require sequences such as:

```text
write journal
flush

write metadata
flush
```

If the first flush is not durable, recovery guarantees become invalid.

---

# 47. Force Unit Access

Some storage devices can make a specific write durable without a separate global flush.

WitOS may expose this through:

```text
WriteOptions.Durable
```

or an equivalent FUA capability.

The exact API remains open.

---

# 48. Ordering

The storage stack must preserve sufficient ordering semantics for crash-consistent filesystems.

A block provider must not arbitrarily reorder operations across an explicit durability barrier.

---

# 49. Crash Consistency

Correct operation after sudden:

```text
power loss
VM termination
device reset
service crash
```

is a primary filesystem correctness requirement.

Testing must include fault injection.

---

# 50. Power-Failure Testing

Tests should deliberately terminate storage operations at many points:

```text
after journal write
before flush
after flush
during metadata update
during rename
during truncate
```

and verify recovery after remount.

---

# 51. Filesystem Correctness Before Performance

A fast filesystem that violates:

```text
rename semantics
flush semantics
journal guarantees
file visibility
```

is incorrect.

Performance optimization follows semantic correctness.

---

# 52. Parallel Filesystem Operations

The main WitOS filesystem must support concurrent callers.

Target usage includes:

```text
many processes
many threads
many open files
parallel metadata operations
parallel data I/O
```

A serialized initial prototype may exist temporarily.

It should not define final semantics.

---

# 53. File Handles

Filesystem operations should be represented through narrow file/directory capabilities where appropriate.

An application need not receive authority over the entire volume merely to access one file.

---

# 54. Namespace Authority

Examples:

```text
read one file
read/write one file
enumerate directory
create children
rename within directory
delete
```

may be separately expressible capabilities.

This builds upon RFC 0008.

---

# 55. System.IO

Standard .NET file APIs remain supported.

Conceptually:

```text
System.IO.File
System.IO.Directory
FileStream
       ↓
WitOS System.IO PAL
       ↓
Filesystem / Storage service
```

WitOS-specific resource APIs are additive.

---

# 56. Path Is Not Authority

A path resolves a namespace location.

It does not itself grant access.

The caller must still possess appropriate authority.

This distinction remains intact even when exposing conventional `System.IO`.

---

# 57. Path Compatibility

Ordinary .NET applications should be able to use expected file semantics without knowing the underlying filesystem type.

For example:

```csharp
File.Open(...)
File.ReadAllText(...)
Directory.EnumerateFiles(...)
```

must not care whether the underlying provider is ext4 or FAT.

---

# 58. Filesystem Detection

Storage management may detect supported on-disk formats and select a matching provider.

For example:

```text
volume
  ↓
probe
  ↓
ext4 provider
```

or:

```text
volume
  ↓
probe
  ↓
FAT/exFAT provider
```

Detection is not authority.

Mounting still requires appropriate block capabilities.

---

# 59. Formatting

Formatting should be performed by filesystem providers or dedicated formatting tools/services.

For example:

```text
Format as ext4
Format as exFAT
```

is an operation over a writable block capability.

---

# 60. Filesystem Service Isolation

Different volumes may use separate service instances.

For example:

```text
System ext4 service

UserData ext4 service

USB exFAT service
```

A failure processing malformed removable media need not compromise unrelated mounted filesystems.

---

# 61. Removable Media

Removable storage should normally be mounted through dedicated providers with appropriately scoped capabilities.

Unexpected removal is an ordinary resource failure.

Applications must be able to observe it.

---

# 62. Read-Only Filesystems

A block resource or filesystem may be exposed as read-only.

This can be used for:

```text
system images
recovery environments
installation media
forensic access
```

---

# 63. System Image

The WitOS system image is expected eventually to be:

```text
immutable or effectively immutable
versioned
signed
atomically replaceable
```

It therefore does not necessarily require the same writable filesystem semantics as user data.

---

# 64. Possible Mature Layout

A future WitOS machine might use:

```text
Boot / firmware partition
    → FAT-compatible

System image
    → signed read-only/versioned image

User/data volume
    → ext4

Removable devices
    → FAT/exFAT

Embedded raw flash
    → specialized flash filesystem
```

No single filesystem must solve every use case.

---

# 65. Bootstrap

A managed filesystem creates a bootstrap question:

> How is the filesystem service loaded before a filesystem is mounted?

The initial solution is straightforward.

Critical components are part of the boot/system image.

For example:

```text
Boot image
    ├── Kernel
    ├── Init
    ├── DeviceManager
    ├── VirtioBlockDriver
    └── Ext4/FAT filesystem service
```

These may be NativeAOT system components.

---

# 66. After Root Mount

After persistent storage becomes available, additional services and applications may be loaded normally from storage.

The bootstrap set should remain small.

---

# 67. M5 Initial Implementation

For the first persistent-storage milestone, the simplest path is:

```text
QEMU
 ↓
virtio-block
 ↓
managed block driver
 ↓
IBlockStorage
 ↓
FAT/exFAT adapter
 ↓
existing OutWit.Common.Fat
```

This allows WitOS to gain persistent files without first implementing ext4.

---

# 68. M5 Purpose

M5 should prove:

```text
device discovery
block I/O
managed storage service
persistent files
filesystem-independent block abstraction
program loading from disk
```

It does not need to prove the final filesystem choice.

---

# 69. ext4 Development Can Proceed in Parallel

A managed ext4 provider can be implemented and tested outside WitOS.

For example:

```text
ext4 disk image
       ↓
FileBlockStorage
       ↓
Ext4Provider
```

running under normal .NET on Windows/Linux.

This is an important development strategy.

---

# 70. Hosted Filesystem Testing

Filesystem logic should be testable without QEMU or a WitOS kernel.

A provider should be able to run against:

```text
memory-backed storage
file-backed disk image
fault-injection storage
remote test provider
```

This allows rapid fuzzing and compatibility testing.

---

# 71. Backend Substitution Test

A critical conformance test is that the same filesystem implementation works unchanged over:

```text
FileBlockStorage
MemoryBlockStorage
VirtioBlockStorage
future NVMeBlockStorage
```

If filesystem code branches on the physical device type, the abstraction is leaking.

---

# 72. Fuzzing

Filesystem parsers process untrusted structured binary data.

They should be extensively fuzzed against:

```text
malformed superblocks
broken directories
corrupt allocation structures
cyclic metadata
invalid extent trees
damaged journals
```

Managed code reduces some memory-safety risks but not logical corruption bugs.

---

# 73. Cross-Implementation Testing

For ext4, disk images should be exchanged with established tools.

Tests may include:

```text
format externally
mount in WitOS provider
modify
check externally

format in WitOS
mount externally

simulate crash
recover in both implementations
```

Compatibility is defined by actual format semantics.

---

# 74. Page Cache

The initial implementation may keep filesystem/block caching entirely in managed storage services.

This is sufficient for early milestones.

---

# 75. VM Integration

Later support for:

```text
MemoryMappedFile
demand paging
shared executable pages
mapped data files
```

requires cooperation between the VM subsystem and filesystem services.

This should not require moving filesystems into the kernel.

---

# 76. External Pager Model

A possible architecture is:

```text
Kernel VM
   │
   │ page fault
   ▼
Pager capability
   │
   ▼
Filesystem service
   │
   ▼
IBlockStorage
```

The kernel understands:

```text
memory object
page
pager
mapping
```

not:

```text
inode
path
extent
ext4
```

---

# 77. Executable Paging

Executable files may initially be fully loaded before execution.

Later they may become demand-paged using the same pager mechanism.

This is an optimization and scalability feature, not an early milestone requirement.

---

# 78. Zero-Copy I/O

A userspace filesystem architecture must avoid excessive memory copying.

The intended model is capability-based shared buffers/pages.

Conceptually:

```text
Application
    │
    │ shared buffer capability
    ▼
Filesystem service
    │
    │ same/shared pages
    ▼
Block driver
    │
    │ DMA mapping
    ▼
Storage controller
```

IPC transfers descriptors and authority.

Bulk data need not be copied between every layer.

---

# 79. Managed Buffers and DMA

Ordinary movable GC memory must not be used blindly for long-lived DMA.

The system should provide explicit mechanisms for:

```text
pinned memory
shared pages
DMA-capable buffers
buffer pools
```

These are memory/storage integration concerns rather than filesystem-specific concepts.

---

# 80. Large Storage

The block abstraction must support capacities substantially beyond current consumer disk sizes without changing API semantics.

Offsets/counts must use sufficiently wide integer types.

Filesystem implementations should not inherit obsolete volume-size assumptions.

---

# 81. Storage Pools

Future resource providers may aggregate many devices into larger pools.

A pool may expose:

```text
one or more logical volumes
```

without requiring applications or filesystems to know the physical layout.

---

# 82. Heterogeneous Storage

A storage pool may eventually contain:

```text
NVMe
SATA SSD
HDD
remote storage
persistent memory
```

with placement driven by resource policy.

This is a future capability and should not complicate M5.

---

# 83. Storage Tiers

Characteristics may allow policy such as:

```text
hot data → NVMe
warm data → SSD
cold data → HDD / remote storage
```

without changing application APIs.

---

# 84. Health

Physical storage resources may expose:

```text
health
temperature
wear
error counters
remaining endurance
```

where available.

Derived resources may expose aggregate health.

---

# 85. Failure Propagation

A provider must translate underlying failures into meaningful storage errors without hiding them.

Examples:

```text
device disappeared
RAID degraded
remote storage unavailable
read checksum failure
volume read-only
```

Higher layers can decide whether recovery is possible.

---

# 86. Checksums

Checksumming may exist at multiple layers:

```text
device
RAID
block transformation
filesystem
application
```

The architecture should not assume that one checksum layer replaces all others.

---

# 87. Filesystem Metadata vs WitOS Metadata

WitOS concepts such as:

```text
ResourceId
persistent grants
application identity
trust domain
replication policy
```

must not be assumed to live directly inside ext4 inode metadata.

These are separate architectural layers.

---

# 88. Extended Attributes

Filesystem-native metadata such as xattrs may be used where useful.

They must not become the sole persistence mechanism for essential WitOS authority or identity semantics.

---

# 89. RAID and Filesystem Independence

The same ext4 provider should work over:

```text
single NVMe
RAID1
RAID5
encrypted RAID
remote virtual disk
```

without filesystem changes.

---

# 90. Filesystem and Device Independence

The same physical block provider should support:

```text
ext4
FAT/exFAT
future filesystem
raw database storage
```

without driver changes.

---

# 91. Raw Block Consumers

Filesystems are not the only valid consumers of block storage.

Possible consumers include:

```text
database engine
VM image service
backup system
specialized scientific storage
volume manager
```

The block layer therefore must not be filesystem-specific.

---

# 92. WitDatabase

WitDatabase remains a higher-level storage/database technology.

It may use:

```text
files
block resources
other providers
```

depending on future integration.

The WitOS storage architecture must not depend on WitDatabase.

---

# 93. Security Boundary

A filesystem service should execute with no more authority than necessary.

Typical authority:

```text
one volume block capability
logging
memory
IPC endpoint
```

It does not require:

```text
all physical disks
network
camera
display
arbitrary process authority
```

---

# 94. Malformed Filesystem Isolation

Mounting an untrusted removable disk should not place filesystem parser code inside the kernel's trusted memory space.

A parser failure or memory exhaustion should remain within its service/resource limits where practical.

---

# 95. Quotas and Resource Limits

Filesystem services may receive limits for:

```text
memory
CPU
open handles
I/O bandwidth
```

to prevent malformed or hostile media from exhausting the system.

---

# 96. Access Control

Filesystem-internal ACLs, where supported, may contribute to persistent access policy.

They do not replace capability authority.

Conceptually:

```text
persistent policy
      ↓
capability issuance
      ↓
live access
```

---

# 97. Mounting Is Capability Construction

Mounting a filesystem conceptually transforms:

```text
Capability<IBlockStorage>
```

into:

```text
Capability<INamespace>
```

or equivalent filesystem capabilities.

This is a resource transformation.

---

# 98. Unmount

Unmounting requires:

```text
stop new operations
complete/cancel active operations
flush filesystem metadata
flush block layers
release capabilities
```

The exact lifecycle will be specified later.

---

# 99. Device Removal

For removable devices, forced removal may prevent clean unmount.

The filesystem must handle subsequent failure/recovery according to its format guarantees.

---

# 100. RAID Metadata

RAID configuration/identity metadata should be independent of filesystem metadata.

An array must be reconstructable before its filesystem is mounted.

---

# 101. Composition Metadata

Storage transformations such as:

```text
RAID
encryption
thin provisioning
```

may need persistent metadata.

Their formats should be openly specified where possible.

---

# 102. Storage Graph

The system should be able to represent the current storage graph.

Example:

```text
NVMe0 ──┐
        ├─ RAID1 ─ Encryption ─ DataVolume ─ ext4
NVMe1 ──┘

USB0 ──────────────────────────────── exFAT
```

Administrative tools can inspect this graph.

Ordinary applications do not need to.

---

# 103. No Mandatory Global Root Filesystem

RFC 0008 remains authoritative:

WitOS does not require the entire storage universe to exist under one fundamental global filesystem tree.

A conventional namespace may still be presented for compatibility and usability.

---

# 104. Volume Naming

Users may identify logical storage through:

```text
human-readable names
ResourceId
mount namespace
application-assigned references
```

Device enumeration order should not be a stable identity mechanism.

---

# 105. Device Names Are Presentation

Names such as:

```text
Disk 0
Disk 1
```

are presentation conveniences.

They are not stable resource identity.

---

# 106. First Implementation Packages

Possible logical project structure:

```text
OutWit.OS.Storage
    contracts

OutWit.OS.Storage.Devices.Virtio
OutWit.OS.Storage.Devices.Nvme

OutWit.OS.Storage.Volumes.Gpt
OutWit.OS.Storage.Volumes.Raid
OutWit.OS.Storage.Volumes.Encryption

OutWit.OS.Storage.FileSystems.Fat
OutWit.OS.Storage.FileSystems.Ext4
```

Exact package boundaries remain open.

---

# 107. Implementation Sequence

Recommended order:

```text
1. IBlockStorage contract

2. MemoryBlockStorage

3. FileBlockStorage hosted provider

4. adapter to OutWit.Common.Fat

5. FAT/exFAT conformance tests

6. VirtioBlockStorage on WitOS

7. persistent M5 filesystem

8. GPT/partition provider

9. managed ext4 implementation

10. ext4 becomes main writable filesystem

11. RAID1

12. encryption

13. advanced RAID / snapshots / thin volumes
```

---

# 108. Why RAID1 First

The first RAID implementation should probably be RAID1.

It validates:

```text
multiple input resources
one output block resource
health state
degraded mode
member replacement
rebuild
```

without parity complexity.

---

# 109. RAID5/6 Later

Parity RAID adds:

```text
stripe calculation
read-modify-write
write-hole handling
recovery complexity
performance tuning
```

and should follow simpler compositions.

---

# 110. Hosted RAID Testing

Like filesystems, RAID providers should be testable against file-backed storage under ordinary .NET.

Example:

```text
disk-a.img ─┐
            ├─ Raid1Storage
disk-b.img ─┘
```

No WitOS kernel is required to test rebuild semantics.

---

# 111. Fault Injection Provider

A reusable test block provider should support failures such as:

```text
fail write N
drop device
return corrupted block
delay requests
reorder allowed writes
simulate power loss
```

This will be valuable across:

```text
filesystem
RAID
cache
encryption
recovery
```

---

# 112. Conformance Suites

Separate test suites should exist for:

```text
IBlockStorage providers
volume transformations
filesystem providers
System.IO compatibility
```

This prevents implementation-specific tests from becoming the only correctness measure.

---

# 113. IBlockStorage Conformance

Providers should be tested for:

```text
range validation
concurrent I/O
read-after-write
flush semantics
read-only behavior
cancellation
device failure
alignment rules
large offsets
```

---

# 114. Filesystem Conformance

Filesystem providers should be tested for:

```text
create
read
write
append
truncate
rename
delete
directory enumeration
sparse files where supported
links where supported
timestamps
crash recovery
concurrent access
```

---

# 115. System.IO Conformance

Standard .NET filesystem tests should run on WitOS to verify expected compatibility.

The filesystem provider implementation must not leak into application behavior except where the underlying format fundamentally differs and .NET permits such differences.

---

# 116. Performance Tests

Relevant measurements include:

```text
sequential throughput
random IOPS
metadata operations
concurrent I/O scaling
cache hit behavior
journal overhead
RAID rebuild impact
encryption overhead
```

Performance should be measured at multiple stack layers.

---

# 117. M5 Performance Goal

M5 does not need competitive storage benchmark results.

It needs:

```text
correct persistent storage
clean architecture
acceptable development performance
```

Optimization follows later.

---

# 118. Native Escape Hatch

If a specific implementation cannot initially be delivered in managed code, a native provider may be used behind the same contract.

This does not move that functionality into the kernel.

For example:

```text
IBlockStorage
      ↓
temporary native filesystem service
```

may be acceptable during transition.

The contract remains implementation-neutral.

---

# 119. Long-Term Preference

The preferred long-term stack is:

```text
minimal native kernel
      ↓
managed device/service layer
      ↓
managed volume composition
      ↓
managed filesystem providers
      ↓
standard .NET
```

---

# 120. Non-Goals

This document does not define:

```text
final ext4 implementation details
final RAID metadata format
full distributed filesystem
final snapshot system
storage UI
backup protocol
cloud storage API
```

These may require separate specifications.

---

# 121. Architecture Invariants

## Invariant 1

The kernel does not implement filesystems.

## Invariant 2

The kernel does not need to understand disks, partitions, RAID arrays, volumes, files, directories, or paths.

## Invariant 3

Device drivers construct block-storage resources from low-level hardware capabilities.

## Invariant 4

`IBlockStorage` is the common boundary between block-producing and block-consuming services.

## Invariant 5

Block operations are asynchronous.

## Invariant 6

The system-level block contract permits concurrent outstanding I/O.

## Invariant 7

Stateful seek is not part of the shared block-storage abstraction.

## Invariant 8

Durability and flush semantics are explicit.

## Invariant 9

Filesystem-specific physical restrictions must not leak into the generic block-storage API.

## Invariant 10

Block transformations may consume block resources and expose new block resources.

## Invariant 11

Partitions are block resources.

## Invariant 12

RAID is a block-resource provider, not a filesystem feature.

## Invariant 13

Encryption is composable below the filesystem.

## Invariant 14

Caching is policy/provider, not a mandatory universal layer.

## Invariant 15

Filesystems execute outside the kernel.

## Invariant 16

Hardware-independent storage services should be managed by default.

## Invariant 17

Filesystem providers should be independently testable under ordinary .NET.

## Invariant 18

FAT/exFAT support should reuse `OutWit.Common.Fat` rather than fork it.

## Invariant 19

ext4 is the preferred initial candidate for the primary general-purpose writable filesystem.

## Invariant 20

Using ext4 does not imply adopting Linux VFS or Linux kernel architecture.

## Invariant 21

The filesystem does not need to know whether its block resource is physical, RAID, encrypted, virtual, or remote.

## Invariant 22

The storage graph may change underneath a stable logical resource when provider semantics allow it.

## Invariant 23

Standard `System.IO` remains supported.

## Invariant 24

Paths are namespace references, not authority.

## Invariant 25

Crash consistency is a correctness requirement, not an optional optimization.

## Invariant 26

Zero-copy/shared-page mechanisms should prevent userspace filesystem architecture from requiring repeated bulk-memory copies.

## Invariant 27

VM/page-cache integration must not require filesystems to move into the kernel.

## Invariant 28

No single filesystem is required to serve all WitOS storage roles.

---

# 122. Initial M5 Target

The immediate implementation target is:

```text
QEMU
 ↓
virtio-block
 ↓
managed Virtio block driver
 ↓
IBlockStorage
 ↓
OutWit.Common.Fat adapter
 ↓
exFAT/FAT filesystem
 ↓
persistent /Programs and /Data
```

This gives WitOS a real persistent filesystem early.

---

# 123. Primary Filesystem Evolution

After M5:

```text
OutWit.Common.Fat
    → bootstrap / removable / interoperability

Managed ext4 provider
    → primary general-purpose writable volume
```

The transition must not alter the block architecture or application-facing storage contracts.

---

# 124. Future Storage Evolution

Later layers may add:

```text
GPT
RAID1
RAID5/6
encryption
storage pools
snapshots
thin provisioning
tiering
remote storage
distributed replication
```

without requiring redesign of:

```text
ext4
System.IO
application storage APIs
kernel ABI
```

---

# 125. Final Statement

The WitOS storage model deliberately separates:

```text
hardware access
block storage
volume composition
filesystem
namespace
application API
```

into independently replaceable layers.

The kernel provides mechanisms.

Drivers turn hardware into block resources.

Volume providers combine and transform those resources.

Filesystems turn block resources into namespaces and file capabilities.

Standard .NET exposes familiar file APIs to applications.

The defining principle is:

> **A filesystem is a managed consumer of block-storage capabilities, not a kernel primitive.**

And the broader storage principle is:

> **Storage is a composable resource graph: physical devices may be partitioned, mirrored, striped, encrypted, cached, virtualized or distributed without forcing the filesystem or application model to know how the underlying resource is constructed.**
