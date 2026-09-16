# WitOS
## RFC 0008 — Storage & Persistent State Model
### Draft v0.1

## 1. Status

Draft.

This document defines the storage, persistence, namespace, file-compatibility, application-state, versioning, replication, durability, and data-locality model of WitOS.

It builds upon:

```text
RFC 0001 — Architecture Concept
RFC 0002 — Resource & Capability Model
RFC 0003 — Application & Lifecycle Model
RFC 0004 — Security, Identity & Capability Delegation
RFC 0005 — Execution, Scheduling & Compute Model
RFC 0006 — IPC & Local/Remote Communication Model
RFC 0007 — Universal Hardware Interface
```

The central principles are:

> **Standard .NET file and stream APIs remain fully supported.**

and:

> **A filesystem path is a useful view of persistent data, not the fundamental identity of that data.**

---

## 2. Motivation

Traditional operating systems commonly define persistence primarily through:

```text
disk
partition
filesystem
directory
file
path
```

This model remains extremely useful.

However, modern applications increasingly require data that may be:

```text
replicated
versioned
remote
cached
encrypted
content-addressed
shared across devices
migrated
synchronized
transactional
```

A path such as:

```text
C:\Users\Alice\Documents\Report.docx
```

or:

```text
/home/alice/report.docx
```

encodes namespace, location, and directory structure, but not necessarily the persistent logical identity of the document.

WitOS therefore keeps normal filesystem semantics while introducing a richer persistent-resource model beneath and alongside them.

---

## 3. Compatibility Is Mandatory

Existing .NET applications must continue to use:

```csharp
File.Open(...)
File.ReadAllBytes(...)
Directory.EnumerateFiles(...)
new FileStream(...)
Path.Combine(...)
FileInfo
DirectoryInfo
MemoryMappedFile
```

with expected .NET semantics.

WitOS does not redefine `System.IO`.

---

## 4. Native Storage APIs Are Additive

Advanced storage functionality may be exposed through optional packages such as:

```text
OutWit.OS.Storage
OutWit.OS.Storage.Abstractions
```

These APIs provide functionality that ordinary filesystem APIs cannot naturally express.

Examples include:

```text
logical resource identity
versions
replicas
durability levels
storage placement
capability-based access
persistent grants
transaction groups
```

---

## 5. Storage Is a Resource

Persistent storage participates in the RFC 0002 resource model.

Conceptually:

```text
IStorageResource
    ├── IByteStorage
    ├── IObjectStorage
    ├── IVersionedStorage
    ├── IBlockStorage
    ├── INamespace
    └── ITransactionalStorage
```

These interfaces are illustrative.

The exact API is deferred.

---

## 6. Physical Storage vs Logical Storage

WitOS distinguishes physical storage from logical persistent resources.

Physical storage examples:

```text
NVMe SSD
SATA disk
eMMC
SD card
persistent memory
remote block device
```

Logical resources include:

```text
document
database
application state
filesystem
replicated object
snapshot
package
```

One logical resource may use several physical storage devices.

---

## 7. Storage Identity

A persistent logical object should have identity independent of physical location.

Conceptually `ResourceId` may continue to identify the object after rename, move, replication, migration, device handoff, or storage-tier change.

---

## 8. Paths Are Not Identity

A path is a namespace reference.

For example:

```text
/Documents/Report.docx
```

may currently resolve to:

```text
ResourceId = A81F...
```

Renaming it to:

```text
/Documents/Final Report.docx
```

should not inherently require changing logical resource identity.

---

## 9. Path Knowledge Does Not Grant Authority

Knowing:

```text
/Documents/Report.docx
```

does not automatically grant access.

Authority comes from capabilities.

This follows RFC 0004.

---

## 10. Storage Descriptor

A storage resource may expose characteristics such as:

```text
capacity
available space
durability
latency
bandwidth
locality
replication state
encryption
consistency
mobility
persistence
```

These describe the resource.

They do not grant authority.

---

## 11. Native Storage Hierarchy Is Not Mandatory

WitOS should not require every persistent resource to exist inside one global directory tree.

Some resources may exist only through:

```text
ResourceId
capability
application state reference
database identity
remote object identity
```

A filesystem namespace can expose them when useful.

---

## 12. Namespace

A **Namespace** maps human-readable names to resources.

Conceptually:

```text
Name
   ↓
Namespace
   ↓
ResourceId
```

A namespace may appear filesystem-like.

---

## 13. Namespace Types

Possible namespaces include:

```text
application-private
user documents
system
temporary
removable media
remote storage
shared workspace
virtual/generated
```

Different namespaces may have different policies.

---

## 14. Namespace Mounting

WitOS may expose one namespace inside another.

Conceptually:

```text
/Documents
/Applications
/System
/Devices
/Network
```

However, these are views rather than fundamental global ownership structures.

---

## 15. Filesystem Compatibility View

Standard .NET applications may receive a conventional filesystem view.

Example:

```text
/
├── app
├── data
├── documents
└── temp
```

The exact path syntax is a platform decision.

The filesystem compatibility layer resolves paths to authorized storage resources.

---

## 16. Application Filesystem View

Each application may receive a filtered namespace.

It need not see every file known to the system.

Example:

```text
Application sees:

/app
/data
/documents/report.docx
/temp
```

while the system may contain many other resources.

---

## 17. Existing File APIs

When an application calls:

```csharp
File.OpenRead(path);
```

the conceptual flow may be:

```text
path
 ↓
namespace resolution
 ↓
logical storage resource
 ↓
read capability
 ↓
stream view
 ↓
FileStream
```

This remains transparent to ordinary applications.

---

## 18. File

A **file** is an important compatibility and native abstraction.

WitOS does not seek to eliminate files.

Instead, a file is one possible view of a persistent storage resource.

---

## 19. Byte Stream View

Many resources naturally expose byte-stream semantics.

Conceptually `IByteStorage` may provide read, write, length, resize, and flush operations.

A `FileStream` can map onto this.

---

## 20. Object View

Other applications may prefer persistent object semantics.

Example:

```text
Document
Version
Metadata
Content
```

without manually converting everything into paths.

---

## 21. File and Object Views May Coexist

One logical resource might expose:

```text
file view
object view
version view
stream view
memory-mapped view
```

Different clients use the representation they require.

---

## 22. Block Storage

Low-level storage drivers expose block-like resources.

Conceptually:

```text
NVMe device
     ↓
driver
     ↓
IBlockStorage
```

The filesystem or object-storage provider consumes the block resource.

---

## 23. Block Storage Is Privileged

Ordinary applications should not automatically receive raw block access.

Raw block capabilities may expose filesystem contents, deleted data, other applications' data, or system metadata.

Therefore access is highly privileged.

---

## 24. Block Geometry

A block-storage resource may expose:

```text
logical block size
physical block size
alignment
maximum transfer
discard support
flush support
atomic-write characteristics
```

Higher layers can optimize accordingly.

---

## 25. Storage Topology

Physical storage may have topology such as:

```text
CPU NUMA node
  ↓
PCIe root
  ↓
NVMe controller
  ↓
namespace
```

This information can influence compute/data placement.

---

## 26. Storage Locality

Storage locality participates in resource selection.

Possible properties:

```text
same device
same NUMA domain
local machine
LAN
remote region
cloud
```

An application may require or prefer particular locality.

---

## 27. Data and Compute Co-Location

Large workloads may prefer moving compute to data rather than data to compute.

Example:

```text
Dataset: 4 TB on workstation B

Solver:
    may run on workstation B
```

instead of transferring 4 TB elsewhere.

This connects storage with RFC 0005.

---

## 28. Persistent Resource Characteristics

A persistent resource may describe:

```text
Durability
Consistency
Replication
Availability
Latency
Bandwidth
Capacity
Encryption
TrustDomain
Cost
```

Applications should specify only characteristics that matter.

---

## 29. Durability

Durability describes what failure conditions data is expected to survive.

Possible semantic levels might include:

```text
Volatile
ProcessDurable
DeviceDurable
ReplicatedDurable
ProviderDefined
```

Exact names remain open.

---

## 30. Durability Must Be Explicit

A successful write may mean different things:

```text
copied into process buffer
copied into OS cache
accepted by storage controller
persisted to nonvolatile media
replicated remotely
```

WitOS should not confuse these guarantees.

---

## 31. Standard File Flush Semantics

Standard .NET `Flush` and filesystem durability semantics must remain compatible with the underlying platform contract.

Advanced WitOS APIs may request stronger explicit durability levels.

---

## 32. Persistence Barrier

A native API may conceptually support:

```text
PersistAsync(requiredDurability)
```

to request a particular durability guarantee.

The exact API is deferred.

---

## 33. Hardware Flush

Physical devices may provide cache flush, force unit access, write barrier, or persistent-memory fence.

The storage stack maps semantic durability requirements onto supported hardware mechanisms.

---

## 34. Unsupported Durability

If a requested durability level cannot be guaranteed, the system must report this explicitly.

It must not silently pretend stronger persistence.

---

## 35. Temporary Storage

Temporary storage is a first-class resource.

Possible semantics:

```text
application-lifetime
session-lifetime
reboot-lifetime
cache-only
best-effort
```

Temporary data should not accidentally receive permanent-storage guarantees.

---

## 36. Cache Storage

Cache storage is disposable persistent or semi-persistent data.

The system may reclaim it under pressure.

Applications must distinguish cache from authoritative state.

---

## 37. Application Data

Applications may receive private persistent storage automatically.

Conceptually:

```text
ApplicationId
    ↓
private state namespace
```

Only the application and explicitly authorized components receive capabilities.

---

## 38. Application Data vs User Documents

Private application state is different from user-owned documents.

Removing an application may remove private state according to policy.

It should not automatically remove user documents.

---

## 39. Application State

RFC 0003 requires persistent logical application state.

WitOS should provide a standard application-state resource.

Example:

```text
open documents
workspace
UI state
recent operation state
application model
```

---

## 40. Application State Is Not Process Memory

Applications should not rely on serializing arbitrary heap contents as the fundamental persistence mechanism.

Logical state should be explicit.

Execution snapshots may optimize resume but remain secondary.

---

## 41. State Store

A future API may provide something equivalent to:

```csharp
IApplicationStateStore
```

with read, write, transaction, version, and snapshot capabilities.

---

## 42. State Schema

Applications own the schema of their logical state.

The OS should not require one universal serialization format.

---

## 43. State Versioning

Application state should support schema migration between application versions.

Example:

```text
State v5
   ↓
Application update
   ↓
migration
   ↓
State v6
```

---

## 44. Atomic State Update

Logical application state often needs atomic changes.

Example:

```text
open document list
+
current workspace
+
active project
```

should not necessarily become partially updated after a crash.

---

## 45. Transactions

Storage providers may expose transactional capability.

Conceptually:

```csharp
await using var transaction =
    await storage.BeginTransactionAsync();
```

Operations inside may commit or roll back atomically according to provider guarantees.

---

## 46. Transactions Are Resource-Specific

WitOS does not require every storage resource to implement full ACID transactions.

Capabilities report what is supported.

---

## 47. Transaction Scope

A transaction may involve:

```text
one object
one namespace
one storage provider
multiple resources
```

Cross-provider transactions are significantly more complex and should not be assumed.

---

## 48. Atomic Rename

Filesystems commonly rely on atomic rename for durable update patterns.

WitOS compatibility filesystems should preserve such expected semantics where promised.

---

## 49. Atomic Replace

A storage API may expose atomic replacement of one resource version by another.

This is useful for configuration, documents, application state, and system updates.

---

## 50. Write-Ahead Logging

Storage providers may use:

```text
WAL
copy-on-write
journaling
shadow paging
```

internally.

These are implementation strategies, not application-level requirements.

---

## 51. Snapshots

Persistent resources may support snapshots.

A snapshot captures a stable logical state at a point in time.

Possible uses:

```text
backup
rollback
application suspend
system update
testing
```

---

## 52. Snapshot Identity

A snapshot may receive its own resource identity while retaining relationship to its source.

---

## 53. Copy-on-Write Snapshots

Providers may implement snapshots efficiently through copy-on-write.

Applications should not depend on implementation details.

---

## 54. Snapshot Consistency

A snapshot contract must define whether it captures a single object, filesystem, application state group, or provider-wide state, and what consistency guarantee applies.

---

## 55. Versioning

A persistent resource may expose multiple logical versions.

Example:

```text
Document
 ├── Version 17
 ├── Version 18
 └── Version 19
```

The current version is not the object's identity.

---

## 56. Version Identity

Versions may have stable identifiers.

This enables optimistic concurrency, history, replication, rollback, and conflict detection.

---

## 57. Immutable Versions

A useful model is:

```text
version = immutable
resource = sequence/reference to versions
```

Providers are free to implement this differently.

---

## 58. Mutable File Compatibility

Standard files remain mutable.

The storage system may internally implement mutable files using immutable versions without exposing that complexity.

---

## 59. Optimistic Concurrency

Applications may request update only if the resource remains at an expected version.

Conceptually:

```text
write if current version == V42
```

Otherwise:

```text
conflict
```

---

## 60. Concurrency Conflict

A conflict is not necessarily an I/O failure.

It may mean another actor updated the resource.

Applications can reload, merge, fork, or ask the user.

---

## 61. Locks

Traditional file locks remain supported.

Native storage may additionally expose shared lease, exclusive lease, version precondition, or transaction depending on resource semantics.

---

## 62. Locks Are Not Identity

A lock controls access or coordination.

It does not define resource ownership.

---

## 63. Lease-Based Access

Distributed resources may use leases instead of permanent locks.

A lease may expire if the client disappears.

---

## 64. Lock Failure

Locks can fail due to peer crash, network partition, lease expiration, or provider restart.

Distributed locking must explicitly define its failure semantics.

---

## 65. Change Notifications

Storage resources may expose change streams.

Examples:

```text
created
modified
deleted
renamed
version changed
replica changed
```

---

## 66. FileSystemWatcher Compatibility

Existing `FileSystemWatcher` behavior should be mapped to the compatibility namespace where feasible.

Native storage change APIs may offer stronger logical-resource semantics.

---

## 67. Metadata

Persistent resources may have metadata.

Examples:

```text
display name
content type
timestamps
owner
tags
version
hash
origin
```

Not every provider needs every field.

---

## 68. Metadata Is Extensible

The system should allow typed metadata extensions without requiring one giant universal metadata structure.

---

## 69. Timestamps

Traditional timestamps may include created, modified, and accessed.

Applications should not assume every storage provider can maintain all timestamps with identical semantics.

---

## 70. Content Type

A resource may expose semantic content type independently of filename extension.

Compatibility namespaces may still use extensions normally.

---

## 71. User Metadata

Applications may attach application-defined metadata where the provider supports it.

---

## 72. Provenance

A resource may optionally record provenance such as created by application, imported from device, received from remote user, or derived from resource X.

This can support audit and collaboration.

---

## 73. Provenance Is Not Authority

Knowing who created a resource does not itself grant access.

Security remains capability-based.

---

## 74. Ownership

Persistent ownership may mean user-owned, application-private, organization-owned, system-owned, provider-owned, or shared.

Ownership participates in policy.

Active authority is still represented through capabilities.

---

## 75. ACL Compatibility

Providers may store persistent ACL-like policies.

These policies can determine capability issuance.

Applications need not interpret ACLs directly.

---

## 76. Storage Capabilities

Possible semantic capabilities include:

```text
Read
Write
Append
Enumerate
CreateChild
Delete
Rename
ManageMetadata
CreateSnapshot
ReadHistory
Administer
```

These should be separable.

---

## 77. Read Does Not Imply Enumerate

An application may receive access to one document without access to its containing namespace.

This is an important capability distinction.

---

## 78. Write Does Not Imply Delete

An editor may modify a document without being allowed to delete it.

---

## 79. Namespace Enumeration

Directory/namespace enumeration is itself authority.

A resource picker can grant access to a selected item without revealing siblings.

---

## 80. Persistent Grants

A user may permanently allow an application access to a document or namespace.

The live storage capability is reissued according to RFC 0004 rather than simply serialized.

---

## 81. File Picker

A trusted file/resource picker can broker storage capability.

Example:

```text
Application requests document
        ↓
trusted picker
        ↓
user selects Report.docx
        ↓
application receives IReadableStorage
```

The application need not receive broad directory authority.

---

## 82. Save Picker

A save operation may grant create-new-resource or replace-selected-resource authority without exposing the entire destination namespace.

---

## 83. Open With

A document manager may delegate a storage capability to another application.

This integrates naturally with RFC 0003 and RFC 0004.

---

## 84. Drag and Drop

Dragging a document may transfer an attenuated storage capability.

---

## 85. Clipboard References

Clipboard data may include references to persistent resources.

Copying a reference does not automatically grant arbitrary authority unless explicit capability transfer is part of the operation.

---

## 86. Encryption at Rest

Storage may be encrypted transparently.

Conceptually:

```text
Physical Storage
      ↓
Encrypted Provider
      ↓
Logical Storage
```

Applications need not handle raw encryption keys.

---

## 87. Encryption Capability

An encryption layer may use an `IEncryptionKey` or similar security capability.

The key material may remain inside a secure provider.

---

## 88. Per-Resource Encryption

Some storage may support unique encryption domains per user, application, resource, or organization.

This is provider policy.

---

## 89. Full-Device Encryption

Device-level encryption remains possible.

It is independent of higher-level resource-specific encryption.

Both may coexist.

---

## 90. Encryption Is Not Backup

Encrypted data may still be lost due to hardware failure, deletion, corruption, or key loss.

Replication and backup are separate concerns.

---

## 91. Replication

A logical storage resource may have multiple replicas.

Example:

```text
Document X
 ├── Laptop
 ├── Phone
 └── Home Server
```

The application may still see one logical resource.

---

## 92. Replica Identity

Replicas are physical manifestations of one logical resource.

They need not have separate application-visible identities unless explicitly requested.

---

## 93. Replica State

Possible states include:

```text
Current
Synchronizing
Stale
Offline
Conflict
Unavailable
```

---

## 94. Replication Policy

Replication may be:

```text
none
local redundancy
multi-device
LAN
cloud
organization-managed
```

Applications may specify constraints but should not normally implement replication themselves.

---

## 95. Replication Is Optional

A local-only resource remains completely valid.

WitOS must not require remote replication.

---

## 96. No Mandatory Cloud Storage

Persistence must work fully without Internet, cloud account, or central storage provider.

Cloud is one possible provider.

---

## 97. Offline Operation

Replicated storage should support explicitly defined offline behavior.

Possible semantics:

```text
read cached version
allow local writes
read-only
unavailable
```

---

## 98. Offline Writes

If multiple replicas accept writes while disconnected, reconciliation may be required.

WitOS should not pretend this problem does not exist.

---

## 99. Conflict Detection

Conflicts may be detected using version IDs, vector/version metadata, or provider-specific mechanisms.

The exact distributed algorithm is provider-specific.

---

## 100. Conflict Resolution

Possible policies include:

```text
application merge
automatic merge
last-writer policy
fork resource
user choice
provider-specific CRDT
```

No universal conflict policy is imposed.

---

## 101. Strong Consistency

Some resources may require strong consistency.

Examples include database metadata, financial transaction state, or security policy.

Providers must expose whether they can satisfy it.

---

## 102. Eventual Consistency

Other resources may accept eventual convergence.

This is a characteristic, not a global WitOS storage model.

---

## 103. Consistency Is Observable

Applications that care must be able to inspect the consistency contract.

---

## 104. Remote Storage

Remote storage participates in the same storage model.

Possible providers:

```text
NAS
another WitOS device
cloud object store
remote database service
organization storage
```

---

## 105. Remote Storage Is Not Local Physics

Remote storage may involve latency, network failure, cost, bandwidth, and partial availability.

These characteristics remain observable.

---

## 106. Local Cache of Remote Storage

A remote resource may have a local cache.

The application may interact with one logical resource while provider policy handles caching.

---

## 107. Cache Consistency

The provider must define how cache freshness is maintained.

Applications requiring stronger semantics may request them.

---

## 108. Read-Through Cache

A provider may fetch data on demand.

This is implementation detail unless latency/offline semantics matter.

---

## 109. Write-Back Cache

Write-back caching may delay remote durability.

The actual durability guarantee must remain explicit.

---

## 110. Data Residency

Applications may require:

```text
device-local
home network only
organization domain
specific region
no cloud
```

Resource resolution must respect these constraints.

---

## 111. Trust Domain

Storage may expose a trust domain.

Sensitive data may require storage only within specified trust boundaries.

---

## 112. Cost

Some storage has monetary or resource cost.

Examples include cloud storage, mobile-network synchronization, or archival retrieval.

Applications may express cost constraints.

---

## 113. Energy

Storage operations may consume significant energy on mobile devices.

The system may schedule replication, indexing, backup, or compaction according to energy policy.

---

## 114. Storage Tiers

The system may use:

```text
RAM
persistent memory
NVMe
SSD
HDD
NAS
cloud
archive
```

as different storage tiers.

Logical resources may move between tiers.

---

## 115. Tier Migration

Moving data between physical tiers should not inherently change logical resource identity.

---

## 116. Hot and Cold Data

Providers may place frequently used data on faster storage.

This is an optimization.

---

## 117. Archival Storage

Some data may be stored on very high-latency archival providers.

Resource descriptors should make retrieval characteristics observable.

---

## 118. Removable Storage

USB drives, SD cards, and similar devices are dynamic storage resources.

Removal causes dependent storage resources to become unavailable.

---

## 119. Removable Media Identity

A removable volume should have stable identity where possible independent of mount point.

---

## 120. Mount Point Is a View

Mount location may change without changing underlying storage identity.

---

## 121. Surprise Removal

Applications must tolerate removable storage disappearing unexpectedly.

In-flight operations may fail.

Capabilities become unavailable.

---

## 122. Reattachment

If the same logical removable resource returns, the system may rebind it.

Applications can decide whether to resume.

---

## 123. Storage Health

Physical storage may expose wear, temperature, error count, remaining endurance, and degraded state.

These become provider/resource characteristics.

---

## 124. Storage Failure

A provider may report:

```text
TransientFailure
MediaFailure
Corruption
ReadOnlyDegraded
Unavailable
```

Applications should receive meaningful failures.

---

## 125. Corruption Detection

Providers may use checksums, journaling, redundancy, ECC, or content hashes to detect corruption.

No single mechanism is mandatory.

---

## 126. Integrity

A storage resource may advertise integrity guarantees.

Example:

```text
None
ChecksumVerified
Authenticated
ProviderDefined
```

---

## 127. Content Hash

Immutable content may expose a cryptographic content hash.

This can support deduplication, integrity verification, cache identity, and distribution.

---

## 128. Content-Addressed Storage

Some providers may use content-addressed storage internally or expose it directly.

This is particularly useful for packages, immutable assets, build artifacts, and snapshots.

---

## 129. Resource Identity vs Content Identity

Two resources may contain identical bytes but remain distinct logical resources.

Therefore:

```text
ResourceId
≠
ContentHash
```

---

## 130. Deduplication

Providers may deduplicate identical physical content.

This must not alter logical ownership or capability semantics.

---

## 131. Compression

Storage providers may compress data transparently.

Applications should normally see logical content.

---

## 132. Sparse Data

Sparse files/resources should be representable efficiently where supported.

Standard `FileStream` semantics remain unaffected.

---

## 133. Memory Mapping

Existing .NET memory-mapped file APIs should remain supported where the provider can present appropriate semantics.

---

## 134. Native Memory-Mapped Storage

Advanced APIs may expose mapped storage capabilities directly.

Possible semantics include read-only, read/write, copy-on-write, and persistent memory.

---

## 135. Remote Memory Mapping

Remote storage cannot generally provide true hardware memory mapping.

A compatibility/provider layer must not falsely claim identical semantics.

---

## 136. Direct I/O

Advanced workloads may request reduced-cache or direct I/O behavior.

This is a low-level optimization.

It should be optional and capability-reported.

---

## 137. Alignment Requirements

Direct storage access may impose alignment, buffer size, or block size requirements.

The provider reports them explicitly.

---

## 138. Zero-Copy I/O

Local storage may integrate with RFC 0006 shared buffers.

Example:

```text
storage device
   ↓
DMA buffer
   ↓
application / decoder / GPU
```

This can avoid unnecessary copies.

---

## 139. DMA Storage Path

High-performance storage may transfer directly into controlled memory buffers.

DMA authority remains governed by RFC 0004 and RFC 0007.

---

## 140. GPU Direct Paths

Some systems may allow storage-to-GPU paths.

These are specialized resource compositions, not required baseline behavior.

---

## 141. Asynchronous I/O

Native storage APIs should be asynchronous where operations may block.

Use standard .NET patterns:

```text
Task
ValueTask
CancellationToken
Memory<T>
```

---

## 142. Standard Synchronous I/O

Existing synchronous `System.IO` APIs remain supported.

The compatibility layer may block an execution context where standard semantics require it.

---

## 143. Cancellation

Storage operations should support cancellation where underlying semantics allow.

Cancellation does not necessarily undo already-persisted writes.

---

## 144. Partial Writes

Low-level storage APIs must explicitly define partial-write behavior.

Higher-level file APIs continue to follow standard .NET expectations.

---

## 145. Streaming Large Data

Large objects should support streaming rather than requiring one complete memory allocation.

---

## 146. Chunked Storage

Providers may internally split data into chunks for replication, deduplication, parallel transfer, or integrity checking.

This should normally remain transparent.

---

## 147. Parallel I/O

High-performance applications may issue multiple asynchronous operations concurrently.

Storage providers may expose queue-depth characteristics.

---

## 148. I/O Scheduling

The OS may schedule storage operations based on interactive latency, bulk throughput, background priority, device characteristics, power, and thermal policy.

---

## 149. I/O Intent

An application may express semantic intent:

```text
Interactive
Sequential
Random
Bulk
Background
LatencySensitive
```

The provider may optimize accordingly.

---

## 150. Storage Reservations

Advanced workloads may request capacity, bandwidth, IOPS, queue slots, or temporary space.

Reservations may be granted, partially granted, or denied.

---

## 151. Capacity Reservation

An application may need guaranteed space before beginning a large operation.

Example:

```text
video export requires 200 GB
```

The storage system may reserve capacity temporarily.

---

## 152. Quotas

Storage usage may be constrained by application, user, session, organization, or resource group.

Quotas are policy.

---

## 153. Quota vs Physical Capacity

A device may have free space while an application quota is exhausted.

These are different conditions.

---

## 154. Resource Accounting

Storage consumption should be attributable where practical.

Examples:

```text
application private data
cache
user documents
system data
snapshots
```

---

## 155. Shared Data Accounting

Shared or deduplicated data complicates physical accounting.

WitOS should distinguish logical usage from physical usage when relevant.

---

## 156. Storage Pressure

The system may signal:

```text
LowSpace
CriticalSpace
CachePressure
QuotaPressure
```

Applications may release disposable data.

---

## 157. Automatic Cache Reclamation

Cache resources may be reclaimed without asking the application.

Authoritative application state must not be treated as disposable cache.

---

## 158. Garbage Collection

Object/content-addressed providers may require garbage collection of unreachable physical data.

This is storage-provider implementation detail.

---

## 159. Delete

Deleting a namespace entry may not immediately destroy all physical copies.

Possible reasons:

```text
snapshots
replicas
version history
backup
retention policy
```

---

## 160. Logical Delete

Applications should understand delete primarily as:

```text
resource no longer available through this logical relationship
```

Physical erasure is a separate guarantee.

---

## 161. Secure Erasure

Applications or administrators may request stronger destruction guarantees.

Actual guarantees depend on hardware, encryption, replication, backup, and provider policy.

---

## 162. Crypto Erasure

Encrypted storage may support logical destruction by deleting encryption keys.

This may be faster than physically overwriting flash.

---

## 163. Retention Policy

Organizations or users may impose minimum retention, legal hold, automatic expiry, or backup retention.

These policies affect deletion semantics.

---

## 164. Trash / Recycle Semantics

A user-facing shell may implement recoverable deletion.

This is presentation/policy above the core storage model.

---

## 165. Backup

Backup is distinct from replication.

Replication improves availability.

Backup preserves historical recoverability.

---

## 166. Backup Resources

Snapshots and versioned resources may serve as backup inputs.

The backup service is itself a storage provider/consumer.

---

## 167. Restore

Restore may create a new resource, new version, or replacement of current resource depending on policy.

---

## 168. System Storage

WitOS system components require persistent storage for system image, configuration, driver packages, application packages, security state, and logs.

These should use explicit system resources rather than arbitrary writable directories where possible.

---

## 169. Immutable System Image

The base OS should favor an immutable or versioned system image.

Normal operation should not modify arbitrary core system files in place.

---

## 170. A/B System Updates

WitOS may maintain:

```text
System A
System B
```

or equivalent versioned roots.

Update process:

```text
write new image
verify
activate
boot
rollback if necessary
```

---

## 171. System Image as Resource

A system image may itself be a versioned persistent resource.

This aligns update semantics with the general storage model.

---

## 172. Driver Storage

Drivers and firmware assets may be stored separately from mutable user/application state.

---

## 173. Application Package Storage

Installed application packages should preferably be immutable.

Application state lives separately.

This simplifies updates, rollback, verification, and deduplication.

---

## 174. Package Identity vs Application State

Updating a package should not require copying or rewriting all application data.

---

## 175. Shared Runtime Assets

Common runtime/package assets may be physically shared or deduplicated without changing application isolation.

---

## 176. Logs

Logs are persistent resources with special lifecycle characteristics.

Possible policies:

```text
bounded size
rotation
retention
compression
remote replication
privacy filtering
```

---

## 177. Audit Storage

Security audit records may require stronger durability and access control than ordinary logs.

---

## 178. Crash Data

Crash dumps and diagnostic snapshots are storage resources.

They may contain sensitive data and require restricted capabilities.

---

## 179. Database Workloads

Databases may use files, memory mapping, direct I/O, block resources, or transactional storage depending on their design.

WitOS should not force databases into one storage abstraction.

---

## 180. Relationship to WitDatabase

WitDatabase remains an application/database-layer technology.

It may use standard `System.IO` or future `OutWit.OS.Storage` capabilities.

WitOS storage must not depend on WitDatabase.

Conversely, WitDatabase may later gain a WitOS-native storage provider where useful.

---

## 181. Filesystem Providers

WitOS may support several filesystem implementations.

Examples might include:

```text
native WitOS filesystem
legacy filesystem
network filesystem
memory filesystem
encrypted filesystem
```

The standard API remains unchanged.

---

## 182. Filesystem Is Replaceable

The kernel should not depend on one filesystem implementation.

Filesystem logic belongs above block/storage primitives.

---

## 183. Filesystem Crash

A filesystem/provider failure should ideally be isolated like other services where architecture permits.

---

## 184. Filesystem Recovery

A storage provider may enter Recovery, ReadOnly, or Degraded states rather than causing entire system failure.

---

## 185. Boot Filesystem

The boot environment may use a minimal storage path before the full storage stack is available.

This is an implementation detail.

---

## 186. Root Filesystem Is Not Fundamental

WitOS does not require the conceptual OS to revolve around one writable root filesystem.

A compatibility root namespace may still exist.

---

## 187. Path Syntax

WitOS may define a native path syntax.

However, ordinary .NET code should rely on `Path`, `Directory`, and `File` rather than hard-coded separators.

---

## 188. Case Sensitivity

Namespace providers may differ in case sensitivity.

Standard .NET APIs expose the provider behavior.

Native logical resource identity should not depend unnecessarily on filename case.

---

## 189. Unicode

Native namespaces should use Unicode-safe naming.

Exact normalization rules must be defined by namespace provider contracts.

---

## 190. Reserved Names

WitOS should avoid inheriting unnecessary historical filename restrictions from legacy platforms.

Compatibility providers may impose them when required.

---

## 191. Long Paths

The native model should not impose arbitrary small path-length limits if underlying providers can avoid them.

Compatibility layers may expose stricter limits where necessary.

---

## 192. Symlinks and References

Filesystem-like namespaces may support symbolic references.

A logical storage reference is not automatically equivalent to a POSIX symbolic link.

---

## 193. Reference Resolution

Reference traversal must respect capabilities.

A reference must not become a path-based privilege escalation mechanism.

---

## 194. Hard Links

Providers may support multiple namespace entries pointing to the same logical resource.

This naturally follows the separation of name from identity.

---

## 195. Rename

Renaming changes a namespace binding.

It need not alter resource identity.

---

## 196. Move

Moving within or between namespaces may mean rename binding, change provider, copy then replace, or resource migration.

The operation's guarantees should be explicit.

---

## 197. Cross-Provider Move

A move between storage providers cannot always be atomic.

The API must not silently promise atomicity where impossible.

---

## 198. Copy

Copy normally creates a new logical resource.

Providers may physically deduplicate data.

---

## 199. Clone

A clone operation may create a new logical resource sharing physical storage copy-on-write.

This is stronger semantic information than ordinary copy.

---

## 200. Resource Fork

A versioned resource may be intentionally forked.

This is useful for collaboration, experimentation, or branching state.

---

## 201. Storage and Handoff

During application handoff, persistent logical resources may remain local and accessed remotely, replicated to destination, migrated, or replaced by equivalent provider.

The application should express locality requirements.

---

## 202. Stable Logical Resource

If the application must continue working on the exact same document, it retains the logical `ResourceId`.

---

## 203. Replaceable Storage Resource

If any equivalent scratch storage is acceptable, the system may allocate a new local resource after handoff.

---

## 204. Data Movement Planning

Moving large data should be explicit in resource scheduling.

The system may compare:

```text
move application
move compute
move data
access remotely
```

and choose according to policy.

---

## 205. Bandwidth Awareness

Storage resources may expose estimated transfer bandwidth.

Distributed execution planning can use this information.

---

## 206. Latency Awareness

Small interactive operations may care more about latency than throughput.

Storage policy should distinguish these cases.

---

## 207. Application-Specified Requirements

A request may conceptually specify:

```text
capacity >= 100 GB
durable
encrypted
local preferred
minimum bandwidth
persistent across reboot
```

The resolver returns a matching storage resource.

---

## 208. Requirements vs Preferences

Hard constraints and preferences must be separate.

Example:

```text
Requires:
    100 GB
    persistence

Prefers:
    local NVMe
    encrypted
```

---

## 209. Granted Characteristics

The application should be able to inspect what was actually granted.

---

## 210. Storage Backend Model

`OutWit.OS.Storage` may use replaceable providers.

Conceptually:

```text
OutWit.OS.Storage
      │
      ├── WitOS native provider
      ├── Windows provider
      ├── Linux provider
      ├── macOS provider
      └── generic .NET provider
```

---

## 211. Cross-Platform Storage API

Where practical, advanced storage APIs should remain usable on existing operating systems.

Features unsupported by the host report Supported, BestEffort, or Unavailable.

---

## 212. Windows Backend

A Windows provider may map native capabilities onto NTFS/ReFS, Win32 file APIs, memory mapping, and volume capabilities without exposing those implementation details in the public contract.

---

## 213. Linux Backend

A Linux provider may map onto VFS, filesystem features, mmap, block devices, and io_uring-like mechanisms where appropriate.

---

## 214. Generic .NET Backend

A generic provider may implement basic functionality using `System.IO`, `FileStream`, `Directory`, and `Task`.

Advanced features may report unavailable.

---

## 215. Portable Applications

Using `OutWit.OS.Storage` should not automatically make an application WitOS-only.

The application remains portable unless it explicitly requires unavailable guarantees.

---

## 216. Standard .NET Remains the Baseline

Portable code may continue to use only `System.IO` and ignore the entire advanced storage model.

That must remain a first-class development style.

---

## 217. Native API Should Compose with .NET

A native resource should preferably expose standard abstractions where meaningful.

Example:

```csharp
Stream stream = await resource.OpenStreamAsync();
```

This allows advanced acquisition with ordinary processing code.

---

## 218. Storage Capability Example

Conceptually:

```csharp
await using IWritableStorage document =
    await context.Resources.AcquireAsync<IWritableStorage>(
        documentId);

await using Stream stream =
    await document.OpenWriteAsync();
```

The exact API remains open.

---

## 219. File Compatibility Example

Existing code:

```csharp
using var stream =
    File.Open("report.dat", FileMode.OpenOrCreate);

Process(stream);
```

runs unchanged.

---

## 220. Application State Example

A native application may write:

```text
Workspace State
Version 18
```

atomically and restore it after suspend, reboot, migration, or crash without relying on process snapshots.

---

## 221. Replicated Document Example

One logical document may have:

```text
Replica A — Laptop — Current
Replica B — Home Server — Current
Replica C — Phone — Offline
```

The application sees one logical document plus replication characteristics.

---

## 222. Engineering Dataset Example

A solver requests:

```text
Dataset:
    2 TB

Compute:
    32 cores
    same device preferred
```

The resource resolver may schedule computation near the dataset rather than transferring it.

---

## 223. Media Example

A video editor may request:

```text
4 TB capacity
high sequential bandwidth
temporary render cache
durable project metadata
```

Different storage resources may satisfy different parts.

---

## 224. Database Example

A database may request durable write barriers, direct I/O, memory mapping, and low-latency local storage while storing backups on a remote replicated resource.

---

## 225. Phone Example

A mobile device may keep current documents local, older assets remote, cache reclaimable, and application state replicated without application-specific phone logic.

---

## 226. Server Example

A headless server may expose storage resources to other WitOS devices.

It remains the same storage model.

---

## 227. Security Example

A photo editor receives read/write capability for Photo X and read capability for Photo Y.

It does not receive the whole Pictures namespace.

---

## 228. Plugin Example

A plugin may receive temporary read-only capability for one input document and write capability for one output resource.

---

## 229. Storage Failure Example

A remote provider disappears.

The resource may transition:

```text
Available
    ↓
Degraded
    ↓
Offline
```

The application receives an explicit state change rather than indefinite blocking.

---

## 230. Storage and IPC

Remote storage protocols build on RFC 0006 communication.

Large data may use streaming, chunking, zero-copy local paths, or remote batching depending on locality.

---

## 231. Storage and Hardware

Physical storage drivers consume RFC 0007 resources such as MMIO, interrupts, DMA, and device reset and expose block/storage resources upward.

---

## 232. Storage and Security

Storage authority follows RFC 0004.

Encryption, persistent grants, user consent, and capability delegation remain separate but integrated concepts.

---

## 233. Storage and Lifecycle

RFC 0003 application suspend/restore relies on persistent logical state and resource reacquisition.

This RFC defines the persistence layer supporting that lifecycle.

---

## 234. Storage and Scheduling

I/O work may participate in execution/resource scheduling.

Examples:

```text
interactive document load
background replication
bulk export
maintenance compaction
```

---

## 235. Storage and Presentation

Presentation shells may expose file browser, document picker, storage status, and removable media UI.

These are UI views over storage resources.

---

## 236. File Manager Is Not Storage

A graphical file manager is an application.

It does not own the storage namespace.

Alternative file managers may coexist.

---

## 237. Search Index

Search/indexing services consume storage capabilities.

They should not automatically receive unrestricted access to all private resources.

---

## 238. Index Metadata

Applications or users may grant indexers access only to selected namespaces or metadata.

---

## 239. Thumbnail Service

Thumbnail generation is a separate resource-consuming service.

It may be suspended or deprioritized under Workstation/Gaming profiles.

---

## 240. Background Storage Services

Examples include:

```text
indexing
replication
backup
deduplication
compression
scrubbing
```

These should run under explicit resource policies.

---

## 241. Maintenance Work

Storage providers may perform garbage collection, compaction, scrubbing, or rebalancing.

The scheduler may defer heavy work during interactive load.

---

## 242. Data Scrubbing

Providers with integrity checks may periodically verify data.

Detected corruption may trigger replica repair where available.

---

## 243. Replica Repair

If one replica is corrupted and another is valid, the provider may repair automatically according to policy.

---

## 244. Observability

Storage diagnostics may report:

```text
capacity
logical usage
physical usage
latency
bandwidth
health
replication status
durability
current provider
```

---

## 245. Privacy of Storage Topology

Ordinary applications should not necessarily see physical disk serials, other users' volumes, or system partition layout.

Detailed topology is capability-controlled.

---

## 246. Profiling

Authorized tools may inspect I/O latency, queue depth, cache hit rate, provider path, NUMA locality, and replication traffic.

---

## 247. Debugging

Developers should be able to understand why a storage request was satisfied by a particular provider.

Example:

```text
Requested:
    100 GB
    local preferred
    encrypted

Granted:
    NVMe-backed encrypted storage
    250 GB available
```

---

## 248. Explainable Failure

Acquisition failures should identify reasons where safe:

```text
capacity unavailable
required durability unsupported
policy denied
trust domain unavailable
provider offline
```

---

## 249. Storage Conformance Tests

Providers should pass tests covering read/write correctness, durability, transactions, versioning, namespace semantics, capability enforcement, failure recovery, cancellation, and replication where supported.

---

## 250. Failure Injection

Tests should simulate:

```text
power loss
provider crash
partial write
device removal
network partition
corruption
out-of-space
replica conflict
```

Persistence guarantees must be testable.

---

## 251. Crash Consistency

Providers advertising crash consistency must define what remains valid after unexpected termination.

---

## 252. Power-Failure Consistency

Stronger durability may require surviving abrupt power loss.

This depends on physical-device guarantees.

---

## 253. Honest Guarantees

WitOS must never advertise durability stronger than the underlying provider can actually enforce.

---

## 254. Compatibility Invariants

1. Standard `System.IO` semantics remain supported.
2. Paths are namespace references, not fundamental resource identities.
3. Resource identity remains stable across rename or relocation where semantics allow.
4. Knowing a path or ResourceId does not grant authority.
5. Persistent authority is capability-based.
6. Files remain first-class supported abstractions.
7. Native storage may expose richer object/version semantics without breaking file compatibility.
8. Application logical state is distinct from transient process memory.
9. Local and remote storage participate in one resource model while locality remains observable.
10. Replication is optional and must never make cloud connectivity mandatory.
11. Durability guarantees must be explicit and honest.
12. Replication, backup, versioning, and encryption are distinct concepts.
13. Storage providers are replaceable.
14. Raw block/device access is privileged.
15. System and application package state should favor immutable/versioned deployment.
16. Advanced WitOS storage APIs are additive to standard .NET.

---

## 255. Deferred Questions

### Native Storage API

```text
exact interface hierarchy
stream/object abstractions
resource creation
namespace API
```

### Filesystem

```text
native filesystem design
path syntax
case rules
metadata
journaling
```

### Application State

```text
default state store
serialization guidance
migration framework
group transactions
```

### Versioning

```text
version identifier format
history retention
branch/fork semantics
```

### Replication

```text
replica protocol
conflict metadata
consistency models
offline writes
```

### Encryption

```text
key management
per-user/application domains
recovery
hardware binding
```

### Durability

```text
formal durability levels
flush semantics
hardware mapping
```

### Storage Scheduling

```text
bandwidth reservations
IOPS contracts
latency contracts
```

### System Image

```text
A/B layout
snapshot format
rollback
recovery integration
```

---

## 256. Relationship to Future RFCs

This RFC interacts strongly with:

```text
RFC 0009 — Presentation, Shell & Input
    resource pickers, file managers, removable media UI

RFC 0010 — Application Packaging & Distribution
    immutable packages, application state separation
```

Future RFCs may further define Native Filesystem Architecture, Distributed Storage & Replication, Application State Format, System Update & Rollback, and Backup & Recovery.

---

## 257. Summary

WitOS preserves the familiar .NET storage world:

```text
File
Directory
Path
Stream
FileStream
MemoryMappedFile
```

while separating these compatibility abstractions from the deeper persistence model.

At the native level:

```text
Persistent Resource
      │
      ├── identity
      ├── capability
      ├── namespace bindings
      ├── versions
      ├── replicas
      ├── durability
      ├── locality
      └── storage characteristics
```

A logical resource may live on local NVMe, removable media, another machine, several replicas, cloud storage, or across several tiers without requiring its application-visible identity to change.

Files remain useful.

Directories remain useful.

Paths remain useful.

They simply stop being the only way the operating system understands persistent information.

The defining principle is:

> **WitOS preserves files as a universal compatibility and usability model while treating persistent identity, authority, versioning, durability, and physical location as separate concepts.**
