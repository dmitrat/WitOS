# WitFS — From WinFS and BeFS to a Unified Modern Filesystem for WitOS

**Research of existing designs, source-code review, and architecture proposal**  
**Date:** October 23, 2026  
**Status:** Research + Architecture Proposal v0.2; **not** an approved on-disk specification; updated to incorporate the agreed resource-first public model  
**Target project:** [WitOS](https://github.com/dmitrat/WitOS) / proposed WitFS component  
**Scope:** Research and conceptual architecture. The WitOS and WitDatabase repositories **were not modified**.

> **Summary.** WitFS should be neither another ext4 implementation nor a SQL database masquerading as a filesystem. It should be a native **copy-on-write (CoW) filesystem in which objects, directories, typed attributes, relationships, indexes, and history participate in one transactional model**. The essential WinFS contribution is its semantic Item/Relationship/Extension model; BeFS contributes integrated file indexing and live queries; ZFS/Btrfs provide a model for durable CoW persistence and snapshots; RedoxFS provides a relatively compact CoW reference for a service-oriented OS; WitDatabase offers reusable .NET B+Tree/KV algorithms, indexes, serialization, and transaction infrastructure. However, simply embedding the present `StoreBTree` does not produce real CoW snapshots and **does not** make file content and metadata commit atomically.

## Contents

1. [Research Scope, Criteria, and Constraints](#1-research-scope-criteria-and-constraints)
2. [Existing WitOS Infrastructure](#2-existing-witos-infrastructure)
3. [WinFS: Information Model and Historical Lessons](#3-winfs-information-model-and-historical-lessons)
4. [BeFS/Haiku BFS: Attributes, Indexes, and Live Queries](#4-befshaiku-bfs-attributes-indexes-and-live-queries)
5. [RedoxFS: Source Code and Portability Constraints](#5-redoxfs-source-code-and-portability-constraints)
6. [OpenZFS and Btrfs: Reliable Storage Mechanisms](#6-openzfs-and-btrfs-reliable-storage-mechanisms)
7. [WitDatabase: Actual Reuse Boundaries](#7-witdatabase-actual-reuse-boundaries)
8. [Comparison and Base Strategy](#8-comparison-and-base-strategy)
9. [WitFS: Product and Semantic Model](#9-witfs-product-and-semantic-model)
10. [Proposed Volume Architecture and Storage Format](#10-proposed-volume-architecture-and-storage-format)
11. [Unified Commit, Snapshots, Recovery, and GC](#11-unified-commit-snapshots-recovery-and-gc)
12. [Metadata, Properties, Relationship Graph, and Indexes](#12-metadata-properties-relationship-graph-and-indexes)
13. [Queries, Live Updates, and Virtual Views](#13-queries-live-updates-and-virtual-views)
14. [System.IO Compatibility and WitOS Security](#14-systemio-compatibility-and-witos-security)
15. [Provisional Architecture APIs](#15-provisional-architecture-apis)
16. [Research, Prototype, and Verification Plan](#16-research-prototype-and-verification-plan)
17. [Reuse Matrix, Risks, and ADR Decisions](#17-reuse-matrix-risks-and-adr-decisions)
18. [Primary Sources and Reviewed Revisions](#18-primary-sources-and-reviewed-revisions)
19. [Resource-First UX: Named Volumes and Typed Devices](#19-resource-first-ux-named-volumes-and-typed-devices)

---

## 1. Research Scope, Criteria, and Constraints

### 1.1. Problem Statement

The starting requirement is that **the WitOS filesystem must be more than a legacy-format provider such as FAT or ext4. Its purpose is to establish a forward-looking information model of the kind WinFS sought to bring into the OS and BeOS partially realized.** The native implementation must run as a managed .NET service above the minimal kernel and consume the existing `IBlockStorage` abstraction.

This must be a **new filesystem**, not merely a file indexer. At the same time, the baseline APIs must remain compatible with `System.IO`, conventional directories, and WitOS's POSIX-like operations.

### 1.2. Measurable Properties of the Target System

1. An ordinary file requires no schema. It can be created, read, renamed, and deleted without invoking semantic services.
2. Every resource has a stable identity independent of its path. Typed properties and relationships are optional.
3. Attribute and relationship queries use native indexes rather than a full-volume scan. The live-query API defines an explicit consistency point.
4. A completed change to file content, metadata, and indexes must be atomic **within one WitFS volume**.
5. After a crash or power loss, the volume mounts at its last **durably committed** state, without half-published indexes or dangling references to unwritten blocks.
6. A snapshot captures one consistent state of ordinary files **and** semantic metadata, not merely the byte content.
7. Block-level integrity is provided. Repair of corrupted data is promised **only where another valid copy exists**.
8. The filesystem runs as a .NET service outside the kernel. The kernel does not parse file paths, inodes, or object schemas.
9. FAT and ext4 remain interchange and compatibility providers. Their continued presence does not dictate WitFS architecture.
10. The disk format is documented, versioned, verifiable, and independent of a particular .NET runtime ABI.

### 1.3. Method and Evidence Levels

- **S (Source):** The specific implementation was reviewed at the cited source revision.
- **D (Docs):** The claim is supported by primary documentation or an author's historical account.
- **P (Proposal):** A proposed WitFS design, **not** an existing WitOS implementation.
- **H (Hypothesis):** Requires a separate experiment, build, benchmark, or failure injection.

Primary sources and exact revisions are listed in §18. Source files were read during this investigation, but the reviewed projects **were not built, benchmarked, or subjected to power-loss tests**. Precise performance estimates or claims that donor code is ready to port without further verification would be unjustified.

---

## 2. Existing WitOS Infrastructure

**Reviewed at commit `cedc0be5b34701aeaca5bb77d874becb9b4452f4`, October 23, 2026.**

### 2.1. Storage Stack (S)

```text
Application → System.IO / libc POSIX
            → Namespace service (.NET / NativeAOT service)
            → IFileSystem [FAT | ext4]
            → IBlockStorage [GPT partition, caching, device channels]
            → managed or native device service → kernel capability/DMA/IPC
```

`IBlockStorage` exposes `ReadAsync(offset)`, `WriteAsync(offset, options)`, `FlushAsync()`, and `DiscardAsync()`. Offsets are byte-addressed and must satisfy `Info.RequiredAlignment`. `FlushAsync()` is documented as a **durability barrier**; `WriteOptions.Durable` represents a durably written individual request (analogous to FUA). This is an excellent foundation for a CoW commit protocol. [Source](https://github.com/dmitrat/WitOS/blob/cedc0be5b34701aeaca5bb77d874becb9b4452f4/src/OutWit.OS/Storage/IBlockStorage.cs); [D3.1](https://github.com/dmitrat/WitOS/blob/cedc0be5b34701aeaca5bb77d874becb9b4452f4/@Docs/Implementation/D3.1-Block-Storage.md).

`IFileSystem` provides lookup, enumeration, open, create, delete, rename, chmod, timestamp operations, flush, and consistency checking. `IFileHandle` supports positional `ReadAsync`/`WriteAsync`, `SetLengthAsync`, and `FlushAsync`, which suits extent-backed files. [IFileSystem](https://github.com/dmitrat/WitOS/blob/cedc0be5b34701aeaca5bb77d874becb9b4452f4/src/OutWit.OS/FileSystems/IFileSystem.cs); [IFileHandle](https://github.com/dmitrat/WitOS/blob/cedc0be5b34701aeaca5bb77d874becb9b4452f4/src/OutWit.OS/FileSystems/IFileHandle.cs).

`FileSystemEntry` already contains `Path`, `Name`, `Kind`, `Length`, `Attributes`, `Created`, `Modified`, `Accessed`, and `Unix`. `FileSystemUnixMetadata.Id` supplies a locally stable inode number. WitFS can preserve this compatibility API without turning `FileSystemEntry` into a universal DTO for the object graph. [FileSystemEntry](https://github.com/dmitrat/WitOS/blob/cedc0be5b34701aeaca5bb77d874becb9b4452f4/src/OutWit.OS/FileSystems/FileSystemEntry.cs).

`Namespace` is a separate service with a mount table and sessions. Its current policy mounts `WitOS ext4` at `/data`, `WitOS FAT` at `/fat`, and sometimes `/boot`. A new provider can therefore be introduced **without rewriting the kernel**. [VolumeMounter.cs](https://github.com/dmitrat/WitOS/blob/cedc0be5b34701aeaca5bb77d874becb9b4452f4/src/Services/Namespace/Volumes/VolumeMounter.cs); [MountTable.cs](https://github.com/dmitrat/WitOS/blob/cedc0be5b34701aeaca5bb77d874becb9b4452f4/src/Services/Namespace/Server/MountTable.cs).

### 2.2. Infrastructure That Should Be Reused (S)

- FAT provider, ext2/3/4 parser, working ext4 writer, and jbd2 journal with dedicated e2fsck/debugfs tests. These are valuable interoperability infrastructure, not disposable scaffolding. [D4.3](https://github.com/dmitrat/WitOS/blob/cedc0be5b34701aeaca5bb77d874becb9b4452f4/@Docs/Implementation/D4.3-Ext4-Writer.md).
- `CachedBlockStorage`, `PartitionBlockStorage`, `MemoryBlockStorage`, GPT, and conformance tests.
- Ext4 recovery, `FileSystemCheckReport`, filesystem errors, namespace protocol, and `System.IO` bridge.
- [RFC 0008](https://github.com/dmitrat/WitOS/blob/cedc0be5b34701aeaca5bb77d874becb9b4452f4/@Docs/RFC-0008-Storage-and-Persistent-State-Model.md) **already** covers resource identity, metadata, transactions, snapshots, change notifications, `FileSystemWatcher`, capability authority, and avoidance of a mandatory universal schema. WitFS must not invent a competing OS resource model.

### 2.3. Implementation Gaps to Account For (S)

1. D5.1 explicitly records that **rename over an existing target first removes the target** and is therefore not atomic. WitFS needs atomic replacement at the filesystem level **and** correct propagation through Namespace; implementing CoW internally alone does not fix external POSIX `rename()`. [D5.1, limits](https://github.com/dmitrat/WitOS/blob/cedc0be5b34701aeaca5bb77d874becb9b4452f4/@Docs/Implementation/D5.1-Namespace-Service.md#limits-kept-explicit).
2. D5.1 also records that `flock` calls on separate opens of the same file do not conflict, and that authority below the volume has not yet been implemented. The native query service must account for future capability authorization; v0.1 must not claim multi-tenant security isolation that does not yet exist.
3. `IFileSystem` is path-based; the namespace protocol does not yet expose `Query`, `GetAttribute`, `Watch`, `Snapshot`, or `Reflink`. Prefer **separate capability endpoints** or opt-in extensions rather than expanding the mandatory `IFileSystem` surface.
4. RFC 0008 distinguishes OS-level `ResourceId`, persistent grants, and replication policy from ext4 inode metadata. WitFS must likewise distinguish object identity from authority.
5. The .NET runtime executing the filesystem service cannot depend on a volume that only that service can mount. The bootstrap filesystem/boot package remains independent.

---

## 3. WinFS: Information Model and Historical Lessons

### 3.1. What WinFS Was—and Was Not (D)

**WinFS was not a replacement on-disk filesystem for NTFS.** Developed by Microsoft during the Longhorn era, it was a structured-storage platform combining files and information within a relational model implemented above NTFS. Public Beta 1 appeared on August 29, 2005. On June 23, 2006, Microsoft announced that WinFS would not ship as a standalone product and that the planned Beta 2 would not follow. Some research and technologies were redirected toward ADO.NET/SQL Server; it would be incorrect to claim that the eventual Windows release incorporated the entire WinFS vision. [BetaNews interview with Quentin Clark (2005)](https://betanews.com/article/ms-explains-winfs-releases-beta-1/); [WinFS: A First Look (2005)](https://www.directionsonmicrosoft.com/reports/winfs-a-first-look/); [InfoQ on Microsoft's decision (2006)](https://www.infoq.com/news/WinFS-Officially-Dropped/).

### 3.2. Core Entities (D)

- **Item:** The unit of identity and access; potentially a document/file, contact, task, or event. **Not every Item requires a byte stream.**
- **Property:** A named typed characteristic of an Item, constrained by a schema.
- **Item Extension:** Structured properties added without indefinitely enlarging the original item type.
- **Relationship / Link:** A directed typed association between Items, possibly with its own fields. Historical designs distinguished *holding* and *reference* relationships and associated lifecycle rules.
- **Schemas:** Domain-specific types, including built-in schemas for documents, messages, people, and tasks. Their expressive power came with a substantial increase in system scope.
- **Object API, queries, and synchronization:** Operations on Items as information entities rather than merely blobs with filenames. Microsoft's 2006 research described an optimistic peer-to-peer WinFS replication protocol with conflicts and eventual convergence.

[The original WinFS model (2004)](https://visualstudiomagazine.com/articles/2004/02/01/winfs-the-longhorn-data-layer.aspx); [architecture diagram archive](https://archive.visualstudiomagazine.com/resources/longhorn/winfs.asp); [Microsoft Research: Peer-to-Peer Replication in WinFS](https://www.microsoft.com/en-us/research/publication/peer-to-peer-replication-in-winfs/).

### 3.3. Reusable Ideas for WitFS (P)

**Retain:** Stable `ObjectId`; typed extensions with namespaces and schema versions; typed relationships; a unified query model; first-class relationship objects (optionally carrying properties of their own); consistent data and indexes; path-independent object identity.

**Simplify:** One fundamental `Resource`/`Object` type with `Kind`, optional `ContentStream`, properties, and relationships. No installation of a `Person` schema should be necessary to save a file. There is no mandatory system ontology for email, contacts, or calendars; applications own their domain schemas. A type identifier is an optional convention, not a condition for object existence.

**Do not repeat:** An attempt to replace applications' databases with one universal system-wide SQL store; mandatory deep P2P replication in the first release; a SQL compiler in the critical path of file I/O; an unportable dependency on a large storage infrastructure; overly complex ownership/holding-relationship lifecycles by default.

### 3.4. Historical Lesson Without Inventing Causality

It is established that Microsoft **cancelled WinFS as a separately shipped product**. There is no definitive public technical postmortem proving a single cause. The breadth of its API, NTFS integration, indexing complexity, user comprehensibility, and resource costs are **engineering risks** to consider, but it is inaccurate to claim that each of them demonstrably "killed WinFS." Its hardware generation and SQL-like infrastructure also differ substantially from WitOS.

**WitFS decision:** Semantic facilities must be an optional, useful extension to a reliable self-sufficient POSIX-like filesystem, not an enormous mandatory layer between every `open()` and the underlying bytes.

---

## 4. BeFS/Haiku BFS: Attributes, Indexes, and Live Queries

### 4.1. Implemented Features (D/S)

BFS, developed for BeOS, is a **complete 64-bit journaled filesystem** with ordinary files/directories, extended typed attributes, B+Tree attribute indexes, queries, and *live queries*. Unlike WinFS, BeFS has a simpler semantic model that is more tightly integrated with an actual on-disk filesystem.

A first-hand account is Dominic Giampaolo, *Practical File System Design with the Be File System*, Morgan Kaufmann, 1999, particularly chapters 4–5, 7, and 10; [Haiku's archival copy](https://www.haiku-os.org/legacy-docs/practical-file-system-design.pdf). Practical Haiku documentation: [Attributes](https://i18n.haiku-os.org/userguide/data/export/docs/userguide/en/attributes.html) and [Queries](https://i18n.haiku-os.org/userguide/data/export/docs/userguide/ru/queries.html).

Files could be zero bytes long and, to the user, consist almost entirely of attributes (for example, People records). Tracker could edit them as fields; indexes made their values searchable; saved queries continued updating as files changed.

### 4.2. Reviewed Haiku BFS Source (S)

The following files were examined at Haiku revision `660ea78079ca9ee36623a0562208476bce89b11e`:

- [`Attribute.cpp`](https://github.com/haiku/haiku/blob/660ea78079ca9ee36623a0562208476bce89b11e/src/add-ons/kernel/file_systems/bfs/Attribute.cpp): Distinct attribute cookies; reads/writes pass through `Inode` and `Transaction`, with a small-data path for short attributes.
- [`Index.cpp`](https://github.com/haiku/haiku/blob/660ea78079ca9ee36623a0562208476bce89b11e/src/add-ons/kernel/file_systems/bfs/Index.cpp): Typed index creation and B+Tree updates as attributes change; predefined `name`, `size`, and `last_modified` indexes.
- [`Query.cpp`](https://github.com/haiku/haiku/blob/660ea78079ca9ee36623a0562208476bce89b11e/src/add-ons/kernel/file_systems/bfs/Query.cpp): Query iterators and registration with `Volume` for live queries; `LiveUpdate` and `LiveUpdateRenameMove` handling.
- [`BPlusTree.cpp`](https://github.com/haiku/haiku/blob/660ea78079ca9ee36623a0562208476bce89b11e/src/add-ons/kernel/file_systems/bfs/BPlusTree.cpp): A tree stored within an inode data stream, deeply coupled to `block_cache`, transactions, `Inode`, and the vnode layer.
- [`Journal.cpp`](https://github.com/haiku/haiku/blob/660ea78079ca9ee36623a0562208476bce89b11e/src/add-ons/kernel/file_systems/bfs/Journal.cpp): Transactional journal, block-cache integration, logging, and recovery.
- [`Volume.cpp`](https://github.com/haiku/haiku/blob/660ea78079ca9ee36623a0562208476bce89b11e/src/add-ons/kernel/file_systems/bfs/Volume.cpp): Mounting, superblock, allocation groups, and query registration.

One subtle detail: `Index::Update()` calls `Volume::UpdateLiveQueries(...)` **before** updating the index tree in that function. Therefore, without further end-to-end verification, we must not assume that historic BFS live notifications strictly mean "event published only after durable commit." WitFS can and should define a stronger contract using a commit-revision journal.

### 4.3. Design Lessons

**Adopt:** Typed attributes as native filesystem data; indexes where the corresponding files actually live; `{Query, Subscribe}` rather than an external crawler; saved queries as first-class UI objects; stable file identity across rename; simple zero-byte information objects; a fast metadata path.

**Improve:** Queries without indexes require an explicit expensive/bounded execution plan; online index creation must backfill existing data; subscriptions must carry revisions, support resumption, and handle overflow; index-key and type limitations must be documented; typed relationship graphs must be first-class rather than merely attributes on individual files.

**Do not port wholesale:** BFS `BPlusTree.cpp`, `Inode.cpp`, and `Journal.cpp` form a C++ kernel implementation tightly coupled to Haiku vnode/cache/locking machinery. WitFS must remain managed and kernel-isolated; even a C# translation would amount largely to a rewrite.

### 4.4. The Gap Neither WinFS nor BFS Fully Closed

BeFS indexed attributes associated with an individual inode. WinFS pursued relationships among many domain-specific item types. **WitFS can put indexed relationships and typed attributes into one consistent transaction.** This requires indexes to participate in the committed state, not to live in an uncoordinated external service notified after a file change.

---

## 5. RedoxFS: Source Code and Portability Constraints

### 5.1. Available Implementation (S)

The [redox-os/redoxfs](https://github.com/redox-os/redoxfs) repository, a mirror of the Redox OS project, was reviewed at revision `b87b0976ee122036b2ec040159b890e1e06ca139`, September 7, 2026. The implementation is Rust under the MIT license. Its README advertises CoW, data/metadata checksums, transparent encryption, conventional Unix attributes, and operation on Redox or Linux/FUSE. `Cargo.toml` lists cryptographic, LZ4, and other dependencies. [README](https://github.com/redox-os/redoxfs/blob/b87b0976ee122036b2ec040159b890e1e06ca139/README.md); [Cargo.toml](https://github.com/redox-os/redoxfs/blob/b87b0976ee122036b2ec040159b890e1e06ca139/Cargo.toml).

Conceptual layout:

```text
RedoxFS FileSystem<D: Disk>
  ├── Header / HEADER_RING / generation / root pointers
  ├── Transaction<D>
  ├── BlockPtr{physical address, hash}
  ├── alloc/release lists
  ├── Tree / Node / Dir
  └── Disk trait → DiskFile, DiskIo, DiskMemory, DiskCache ...
```

`Disk` provides `read_at`, `write_at`, `write_at_batched`, and `size`; memory- and file-backed implementations exist. [`src/disk/mod.rs`](https://github.com/redox-os/redoxfs/blob/b87b0976ee122036b2ec040159b890e1e06ca139/src/disk/mod.rs). The root header carries a generation, UUID, tree/allocator/release pointers, and a **256-slot `HEADER_RING`**. [`src/header.rs`](https://github.com/redox-os/redoxfs/blob/b87b0976ee122036b2ec040159b890e1e06ca139/src/header.rs). `BlockPtr` stores a physical pointer and 64-bit hash. [`src/block.rs`](https://github.com/redox-os/redoxfs/blob/b87b0976ee122036b2ec040159b890e1e06ca139/src/block.rs).

### 5.2. Key Finding: Durability Contract (S/H)

The `Disk` trait contains **no `flush`, `sync`, or FUA operation**. In [`Transaction::sync()`](https://github.com/redox-os/redoxfs/blob/b87b0976ee122036b2ec040159b890e1e06ca139/src/transaction.rs#L267-L323), cached blocks are written by `write_at_batched` and a new Header generation is written afterward. No explicit durability barrier supplied by `Disk` is visible between those two steps.

This **does not prove that every RedoxFS deployment loses data**: a particular backend, host filesystem, or other layer may provide stronger guarantees. Nevertheless, the **generic `Disk` contract does not express the ordering/durability condition needed here**, unlike WitOS's `IBlockStorage`. WitFS should adopt the **root-commit idea**, not copy this behavior without redesign and power-loss testing.

### 5.3. Features Not Demonstrated as Ready-Made RedoxFS Capabilities

- [`src/clone.rs`](https://github.com/redox-os/redoxfs/blob/b87b0976ee122036b2ec040159b890e1e06ca139/src/clone.rs) **copies objects and content between filesystems**. It is **not** evidence of per-file reflink cloning or a snapshot API, and contains a TODO for hard links.
- CoW plus root generations are prerequisites for snapshots, not a complete retention/refcount/recovery/GC design.
- Filesystem-check tooling, long-term data retention, fragmentation, worst-case GC, and concurrent metadata operations require separate evaluation. Reading code is not an acceptance test.
- A SeaHash checksum is not a cryptographic authenticity guarantee. Encryption must address tampering and authentication explicitly; AES-XTS must not be mistaken for AEAD.

### 5.4. Proposed Project Role (P)

RedoxFS is a **valuable external reference for a compact CoW engine**, but not sufficient reason to embed Rust wholesale into a critical managed WitOS service. A host-only FFI adapter or test oracle might help compare semantics. Production WitFS should remain managed unless an explicit system-level decision permits native filesystem services. The MIT license may allow selective algorithm/code reuse subject to preservation of notices.

---

## 6. OpenZFS and Btrfs: Reliable Storage Mechanisms

### 6.1. OpenZFS: The Integrity Tree (D)

OpenZFS relies on **copy-on-write publication of a new consistent root**. A modified block is written at a new location; ancestors receive new block pointers and checksums; finally a newly published uberblock makes that tree authoritative. An essential distinction from an in-block CRC is that **the parent stores the child's checksum**. The documentation describes import selecting the valid uberblock with the highest transaction identifier. [OpenZFS: Copy-on-Write](https://openzfs.github.io/openzfs-docs/Basic%20Concepts/Copy-on-write.html).

Consequences include snapshots through retained roots, sharing-based clones, scrubbing, and send/receive across versions. Costs include write amplification, fragmentation, and the need for free space even while deleting. **Checksums cannot repair data without a redundant valid copy.** [Checksums](https://openzfs.github.io/openzfs-docs/Basic%20Concepts/Data%20Storage/Checksums.html); [Scrub and Resilver](https://openzfs.github.io/openzfs-docs/Basic%20Concepts/Operations/Scrub%20and%20Resilver.html).

**Transaction groups versus `fsync`:** ZFS batches asynchronous writes into transaction groups (`txg`). Synchronous durability uses the ZFS Intent Log (ZIL), with replay on recovery. WitFS v0.1 **does not need to replicate ZIL immediately**; it may initially perform a more expensive but correct durable-root commit at `fsync()`, measuring whether later acceleration is justified. [OpenZFS discussion of txg and ZIL](https://openzfs.github.io/openzfs-docs/Basic%20Concepts/Copy-on-write.html).

### 6.2. Btrfs: Several Specialized Trees (D)

Btrfs organizes metadata into a **family of CoW B-trees**, not one enormous flat table. Its source and documentation describe the root tree, filesystem/subvolume trees, extent tree, checksum tree, chunk mapping tree, free-space tree, and others. Each subvolume/snapshot root identifies its inode namespace; extent references permit block sharing. [Btrfs B-trees](https://btrfs.readthedocs.io/en/latest/dev/dev-btrees.html); [Design](https://btrfs.readthedocs.io/en/latest/dev/dev-btrfs-design.html).

- **Subvolume snapshots** initially share a root and referenced data, then diverge through CoW. They are not full directory copies.
- **Reflinks** create a separate file/metadata identity while sharing extents until changed; they are neither hard links nor symbolic links. [Reflink](https://btrfs.readthedocs.io/en/stable/Reflink.html).
- **Checksums** for data/metadata, compression, repair when redundancy is available, and send/receive are practical features.
- **Backreferences and reference-count accounting** complicate multiple snapshots; cross-tree dependencies can increase metadata write amplification.

The Btrfs lesson for WitFS is **one tree mechanism plus several key spaces or independent roots**, not a literal recreation of Btrfs's RAID, chunk/balance, RAID56, quota-group, or other subsystems. A single-device v0.1 needs a substantially smaller core.

### 6.3. Synthesis

| Property | OpenZFS | Btrfs | Proposed WitFS |
|---|---|---|---|
| Persistent data | CoW tree | CoW B-trees | CoW data extents and metadata trees |
| Atomicity boundary | Uberblock / txg | Superblock/root commit | Generation root record; two flush phases |
| Metadata | Tree and object sets | Typed key spaces | Objects, directories, attributes, graph, indexes |
| Checksum placement | Parent block pointer | Checksum tree/metadata nodes | Parent metadata pointers; data-extent checksums |
| Snapshot | Dataset/root | Subvolume/root | Volume-consistent root including query indexes |
| Synchronous writes | ZIL | Tree log | Initially durable-root commit; optimized log later |
| Reflink | Block cloning | Shared extents | Later, after retention/GC design |
| Automatic repair | With redundancy | With redundancy | No guarantee until redundancy exists |
| Multiple disks | Integrated pool | RAID/chunks | WitOS composition layer, not duplicated in WitFS v1 |

**Do not directly copy OpenZFS (CDDL) or Btrfs (GPLv2) code** if WitOS is to remain independently implemented under Apache-2.0. Published architecture and ideas can inform the design; source reuse would require separate licensing and compatibility review. [OpenZFS license](https://openzfs.github.io/openzfs-docs/License.html); [btrfs-progs](https://github.com/kdave/btrfs-progs).

---

## 7. WitDatabase: Actual Reuse Boundaries

**Source:** [`dmitrat/WitDatabase`](https://github.com/dmitrat/WitDatabase), revision `ba00a08a0f2dfffa3fc97d440b758aab5c0dd37c`, August 19, 2026. The review focused on `Sources/Core/OutWit.Database.Core` (Engine 14.0.1, .NET 10), not merely the public SQL API. Its Apache-2.0 license makes it a license-compatible source of algorithms for WitOS.

### 7.1. Actual Modules (S)

```text
OutWit.Database.Core
 ├─ IStorage                        pages: Read/Write/Flush/SetSize
 ├─ PageManager / page cache        management of mutable pages
 ├─ BTree / StoreBTree              ordered KV, overflow pages, range scans
 ├─ StoreLsm                        MemTable, SSTables, WAL, compaction
 ├─ TransactionalStore             buffered logical changes and journaling
 ├─ WriteAheadLog / journals       durable logical redo/recovery
 ├─ MVCC / savepoints / locking    logical row/version transaction facilities
 ├─ IndexManager / SecondaryIndex  secondary indexes over KV stores
 ├─ encrypted storage              AES-GCM / provider abstractions
 └─ provider registry              composable cache/storage/KV factories
```

Sources: [`EXTENSIBILITY.md`](https://github.com/dmitrat/WitDatabase/blob/ba00a08a0f2dfffa3fc97d440b758aab5c0dd37c/Sources/Core/OutWit.Database.Core/EXTENSIBILITY.md); [`OutWit.Database.Core.csproj`](https://github.com/dmitrat/WitDatabase/blob/ba00a08a0f2dfffa3fc97d440b758aab5c0dd37c/Sources/Core/OutWit.Database.Core/OutWit.Database.Core.csproj).

### 7.2. `IStorage` Is a Good Integration Point, Not a Complete Filesystem (S)

`IStorage` exposes `ReadPage(long)`, `WritePage(long)`, asynchronous equivalents, `Flush()`, `FlushAsync()`, `SetSize(long pageCount)`, `PageSize`, `PageCount`, and `IsReadOnly`. [`IStorage.cs`](https://github.com/dmitrat/WitDatabase/blob/ba00a08a0f2dfffa3fc97d440b758aab5c0dd37c/Sources/Core/OutWit.Database.Core/Interfaces/IStorage.cs).

An adapter could connect `IStorage` to a dedicated **logical page range** backed by `IBlockStorage`. That is correct only with explicit free-page management, physical placement, `SetSize`, ordering, caching, and crash recovery. A native WitFS volume must not use `StorageFile`: it lives above `System.IO` and would create an undesirable cyclic dependency.

```text
BAD:  WitFS -> WitDatabase(StorageFile) -> System.IO -> WitFS
GOOD: WitFS -> WitDatabase.Core(IStorage) -> CoW page provider -> IBlockStorage
```

**Additional challenge:** WitDatabase exposes both synchronous and asynchronous methods; many current BTree operations are synchronous, whereas WitOS `IBlockStorage` is asynchronous. Implementing `ReadPage() => ReadAsync().GetAwaiter().GetResult()` within a single-threaded IPC/event loop risks deadlock, contention, and high latency. A proof of concept must evaluate an async-aware storage/cache, a dedicated worker, or a new explicit asynchronous model.

### 7.3. The BTree Is Not a Persistent CoW Tree (S)

[`StoreBTree`](https://github.com/dmitrat/WitDatabase/blob/ba00a08a0f2dfffa3fc97d440b758aab5c0dd37c/Sources/Core/OutWit.Database.Core/Stores/StoreBTree.cs) accepts `IStorage` or `PageManager`. During insertion, [`BTree.Insert.cs`](https://github.com/dmitrat/WitDatabase/blob/ba00a08a0f2dfffa3fc97d440b758aab5c0dd37c/Sources/Core/OutWit.Database.Core/Tree/BTree.Insert.cs) modifies an existing page and calls `leafPage.MarkDirty()`. `PageHeader.RightChild` contains the `uint` page number of a child. [`PageHeader.cs`](https://github.com/dmitrat/WitDatabase/blob/ba00a08a0f2dfffa3fc97d440b758aab5c0dd37c/Sources/Core/OutWit.Database.Core/Pages/PageHeader.cs).

Thus the current BTree uses a **mutable logical page-number space**. It is **not** a physically immutable tree whose old versions are preserved automatically by publishing a new root. Its pointers and caches are not isolated by generation. Copying the root page number into a snapshot table is insufficient.

One possible but **nontrivial** adaptation is to keep logical pages and add a snapshot-aware `logicalPage → physicalCoWBlock` translation, versioning the translation table itself for each snapshot. But a shared `PageManager` may cache pages from a newer generation; snapshot readers require an isolated root view. `SetSize`, free lists, overflow pages, and root metadata must also commit atomically. This requires a spike; it is **not** an approved or demonstrated implementation.

### 7.4. WAL and Transactions Do Not Equal a Volume Transaction (S)

- [`TransactionalStore`](https://github.com/dmitrat/WitDatabase/blob/ba00a08a0f2dfffa3fc97d440b758aab5c0dd37c/Sources/Core/OutWit.Database.Core/Transactions/TransactionalStore.cs) coordinates KV operations through `ITransactionJournal`; durability is not promised when no journal is provided, as its source explicitly notes.
- [`WriteAheadLogBase`](https://github.com/dmitrat/WitDatabase/blob/ba00a08a0f2dfffa3fc97d440b758aab5c0dd37c/Sources/Core/OutWit.Database.Core/Wal/WriteAheadLogBase.cs) opens a **separate `FileStream`** for the WAL. `Sync()` invokes `Flush(flushToDisk: true)`.
- [`WalTransactionJournal`](https://github.com/dmitrat/WitDatabase/blob/ba00a08a0f2dfffa3fc97d440b758aab5c0dd37c/Sources/Core/OutWit.Database.Core/Wal/WalTransactionJournal.cs) adapts logical Put/Delete operations to the WAL journal interface.
- [`StoreLsm`](https://github.com/dmitrat/WitDatabase/blob/ba00a08a0f2dfffa3fc97d440b758aab5c0dd37c/Sources/Core/OutWit.Database.Core/Stores/StoreLsm.cs) relies on a directory of SSTable files and a WAL; it is **not** a direct `IBlockStorage` backend without substantial new implementation.
- [`PageManager.Flush()`](https://github.com/dmitrat/WitDatabase/blob/ba00a08a0f2dfffa3fc97d440b758aab5c0dd37c/Sources/Core/OutWit.Database.Core/Managers/PageManager.cs#L650-L733) deliberately flushes dirty pages, then the storage, then the header, then the storage again. **Its own comment cautions** that old headers and prematurely evicted overwritten pages are not consistent without journaling. A filesystem therefore needs an independently verifiable committed-generation model.

**WitFS invariant:** If one operation changes a data extent, inode size, path, and index key, **a separately committed WAL for any subset cannot guarantee whole-operation atomicity**. Options are (A) one CoW root covering data and metadata, or (B) a purpose-built multi-participant atomic protocol. Option (A) is preferable initially.

### 7.5. Where Existing Code Is Most Valuable (S/P)

| WitDatabase component | Reuse readiness | Proposed use |
|---|---|---|
| Typed `byte[]` KV keys, comparison, `Scan` | **High** | Object/index key spaces and range scans |
| B+Tree search, split, traversal, overflow | **High algorithmically**, moderate integration effort | Adapt to immutable/CoW pages or place behind a CoW layer |
| `IStorage`, `IPageCache` | **High as contracts** | Implement `CoWPageStorage`; reconcile page size and flush semantics |
| `SecondaryIndexKeyValueStore` | **Moderate** | Reuse key/lookup techniques; own atomic updates coordinated with Objects |
| `IndexManager` | **Moderate/low for native FS** | Review factory/API design; do not assume all indexes share one transaction |
| `TransactionalStore` and WAL | **High as references, low for direct reuse** | Eliminate `FileStream` dependency; avoid redundant journals if committing roots |
| `StoreLsm` | **Low in first native FS** | Defer; relies on SSTable directories, files, compaction, and WAL |
| MVCC rows, savepoints, locks | **Moderate** | Read-view and conflict semantics; snapshots also require physical retention |
| `StorageEncrypted` | **Moderate** | Crypto concepts/interfaces; verify key handling and block granularity |
| Higher-level SQL engine | **Low on critical path** | Later query tooling; do not put an SQL parser on `open()` |

A potentially **important shortcut** is that `StoreBTree(IStorage)` makes a **semantic-catalog prototype backed by host ext4** easy to build. Such a prototype must not be presented as the final crash-consistent on-disk format.

### 7.6. Recommended Independent WitDatabase Spikes (H)

**Spike DB-1 — Logical pages behind a CoW mapping.** Place `StoreBTree` on snapshot-aware `IStorage` with isolated commits; create two snapshots, change 10,000 random keys, restart the process, and read both versions using separate page caches. Measure write amplification and CoW mapping churn.

**Spike DB-2 — Adapt BTree for path copying.** Modify the smallest possible part of the BTree: updating a leaf/child writes new physical pages along the path and publishes a new root. Determine whether a global logical-to-physical tree is no longer necessary and snapshots become simpler. Measure the actual source-change footprint.

**Spike DB-3 — Async compatibility.** Execute random lookups and BTree range scans over a real WitOS asynchronous block channel. Measure latency, synchronous waits, cancellation, backpressure, and cache memory.

**Reuse decisions must follow these experiments**, not the superficial similarity of interface types.

---

## 8. Comparison and Base Strategy

### 8.1. Mechanism Matrix

| Requirement | WinFS | BeFS | RedoxFS | OpenZFS/Btrfs | WitDatabase | WitFS decision |
|---|---|---|---|---|---|---|
| File byte-stream API | Backed by NTFS | Native | Native | Native | `FileStream` | `IFileSystem`, native provider |
| Objects without content | Items | Zero-byte files + attributes | Ordinary inodes | Files/attributes | KV records | `Object.Kind` + optional content |
| Typed properties | Rich schemas | Typed attributes | Unix attributes | Extended attributes | Key/value | Extensible typed attributes |
| Graph links | Native relationships | No general graph model | None | No semantic graph | Feasible over KV | Native typed `Relation` |
| Indexed queries | Relational | B+Tree indexes | No equivalent to WinFS | No native semantic queries | Ordered KV/indexes | Indexes under unified commit |
| Live queries | Platform design | Production feature | None | None | Possible change events | Revision-based watch |
| CoW atomic root | No; above NTFS | Journal | Generation header | Core mechanism | Mutable pages | Dedicated CoW root |
| Data + attribute snapshots | Not a disk-level FS snapshot | No native CoW snapshot | Not demonstrated complete | Available | Separate logical MVCC | Volume-consistent snapshots |
| Managed .NET service fit | No viable direct source port | Tightly coupled C++ | Costly Rust port | Complex native code and licensing | Native C# | Original managed core |

### 8.2. Three Approaches and the Decision

**A. WitFS = ext4 plus a separate attributes database.** Low upfront cost and an excellent UX laboratory. But it introduces two durability domains, recovery/reconciliation requirements, no unified atomic snapshot, and complex rename/index synchronization. **Decision:** Use only for a semantic prototype or transitional bridge.

**B. Port RedoxFS or incorporate Btrfs/ZFS internals.** CoW and integrity mechanisms already exist and might shorten early storage work. However, Rust/C++/kernel assumptions, ZFS/Btrfs licensing constraints, and insufficiently established RedoxFS flush/snapshot semantics remain major risks. **Decision:** Reference implementation and test oracle, not the direct foundation.

**C. Build a compact WitFS CoW volume engine in C#, reusing WitDatabase Core selectively.** More initial work, but full control over the disk format, commit boundary, and semantics. Managed storage becomes a native part of WitOS without SQL or `FileStream` dependencies during mount. **Decision: Recommended primary strategy**, conditional on early BTree reuse spikes and independent crash-consistency verification.

### 8.3. Option C Does Not Mean Rebuilding All of ZFS

WitFS v0.1 must not include built-in RAIDZ, multi-level adaptive replacement caching, pool migration, distributed filesystems, SQL parsing, a full graph-database engine, a FUSE wire protocol, mandatory deduplication, or transactional network replication. The first deliverable is **one local volume on one `IBlockStorage` resource**, a POSIX-like file API, durable CoW storage, basic attributes and relationships, indexes/queries, snapshots, and rigorous tests.

---
## 9. WitFS: Product and Semantic Model

### 9.1. Core Definition

> **WitFS is a filesystem in which a data entity (`Resource`) has stable identity, optional byte content, typed properties, relationships to other resources, and versioned state; conventional paths are only one mechanism for organizing and accessing these resources.** Physical data and all metadata are persisted within one atomic CoW transaction.

**Keep distinct:** `ObjectId` (identity of the entity owning content), `Path` (namespace name), `Revision` (version), `SnapshotId` (retained state), and `Capability` (authority). These are five different concepts.

### 9.2. Minimal Universal Entity

```text
Resource {
  ObjectId         : stable ID; proposed pair (VolumeId:128-bit, LocalId:64-bit)
  Kind             : File | Directory | Symlink | ValueObject | Special
  ContentStream?   : byte sequence for file-like objects
  Created, Modified, Changed
  Owner/PolicyRef? : persistent policy metadata, NOT live authority token
  Optional TypeId  : e.g., "org.outwit.project", "image/jpeg", custom kind
  Attributes       : (namespace, name, typed value)[]
  Relations        : typed directed edges to ObjectIds[]
  StorageVersion   : revision/generation
}
```

`ValueObject`, without byte content, is a lightweight native Item inspired by WinFS. It may describe a project, person, experiment, CAD model, or task. Traditional file applications should see such resources only through an explicitly enabled compatibility representation (for example, a zero-byte file with attributes), not unexpectedly receive fabricated files.

### 9.3. Application-Level Example

```text
/data/Projects/Lever/cast-lever.step           conventional CAD file
  attrs:
    content.type        = "model/step"
    cad.material        = "Al 6061"
    cad.project         = "CastLever"
    experiment.status   = "reviewed"
  links:
    isInputFor → /data/Projects/Lever/load-001
    describedBy → /data/Projects/Lever/report.pdf

/data/Projects/Lever/load-001                 Resource without bytes
  attrs:
    simulation.solver   = "CalculiX"
    simulation.loadN    = 750.0
    simulation.status   = "finished"
  links:
    produces → /data/Projects/Lever/stress.vtu
```

The system can answer "all STEP models in project CastLever that have a completed simulation with load ≥ 700 N" without parsing paths or maintaining advance copies in a separate search service. The internal implementation may use indexed joins or a staged lookup.

### 9.4. Benefits Without Requiring Deep WitFS Knowledge

- Ordinary `FileStream` consumers, editors, command-line tools, and older .NET applications see **a normal filesystem**, not a mandatory new API.
- An Explorer/Finder-like shell can display typed-property columns, smart folders, saved queries, virtual collections, and live updates.
- Native developers obtain `Get/SetAttribute`, `CreateRelation`, `QueryAsync`, `WatchQueryAsync`, and `CreateSnapshotAsync` APIs.
- Researchers and engineers can connect project structures, CAD/CAE artifacts, inputs, simulations, and results, then query their history.
- Administrators obtain pre-upgrade snapshots, integrity scrubs, corruption diagnostics, and intelligible recovery tools.

---

## 10. Proposed Volume Architecture and Storage Format

### 10.1. Layers

```text
  Apps                        WitOS Shell / semantic apps
   │                                 │
  System.IO / POSIX             IWitResourceStore / Query
   │                                 │
   └──────────── Namespace Service ──┘  (path resolution, security policy)
                     │
               WitFS Volume Service      (C#, user space, privileged)
                     │
     ┌───────────────┼─────────────────────────────────┐
     │               │                                 │
   File/Dir API   Semantic Catalog                  Query Engine
     │             (Object/Attr/Relations)         (secondary indexes)
     │               │                                 │
     └───────────────┼─────────────────────────────────┘
                     │
              Transaction Coordinator
                     │
      CoW metadata trees + extent/data allocator
                     │
           Root set + integrity checksums
                     │
          IBlockStorage (durable barriers)
                     │
       partition / cache / encryption / RAID / device
```

`Namespace Service` and `WitFS Volume Service` may initially share a process to simplify debugging. Their public contracts must nevertheless permit separate services with capability endpoints and minimally privileged partition access. There is no reason to put filesystem logic into the kernel.

### 10.2. Proposed On-Disk v0.1 Regions (P; Not a Final Binary Specification)

```text
Block 0..N             Reserved / volume identification / boot-independent label
Selected fixed slots   Root Record Ring: version, generation, checksums, roots
Other blocks           CoW metadata nodes, immutable after commit
Other blocks           File content extents and optionally packed small data
Embedded trees         Free space / extents / Object / Directory / Attr / Index /
                       Relation / RelationReverse / Snapshot / ChangeLog
```

The volume contains **no separate "database file"** and no second full filesystem namespace in a reserved area. Its primary control structures are native CoW blocks. A root-record set binds **all** trees to a common generation:

```text
WitFsRootRecord [illustrative, NOT ABI]
  Magic / FormatMajor / FormatMinor / FeatureFlags
  VolumeUUID / Generation / CommitTimestamp / BlockSize
  ObjectTreeRoot
  DirectoryTreeRoot
  ExtentTreeRoot
  FreeSpaceTreeRoot
  AttributeTreeRoot
  IndexDirectoryRoot
  RelationTreeRoot
  ReverseRelationTreeRoot
  SnapshotTreeRoot
  ChangeLogRoot
  RootChecksum / Selector-or-Sequence
```

The eventual layout must minimize the number of roots; it should not create a separate tree without demonstrated value. For example, several logical tables may share **one typed KV tree** with prefixed keys. Ten independent roots in a superblock are illustrative, not a requirement.

### 10.3. Block Addressing

- Physical blocks will commonly be 4 KiB, **but** must obey the actual `Info.RequiredAlignment`, block size, and device-sector durability semantics rather than assuming 4096 bytes everywhere.
- Map file contents through logical ranges to physical extents. Large streams benefit from contiguous extents, sparse holes, `SetLength` without mandatory zero writes, and sequential prefetch.
- Tiny files (perhaps below 512–2048 bytes) could eventually be stored inline within object/extent metadata nodes. This is an **optimization after correctness**; choose a threshold from measurements.
- Published data extents are immutable. A modified range creates CoW extents for the affected region, including read/modify/write for the appropriate checksum/compression unit.
- An absent data extent represents logical zeroes (sparse semantics); this must never be confused with an unwritten allocation after a crash.

### 10.4. Integrity

- Metadata-node headers include type, format version, generation, validated checksum, expected lengths/bounds, and parent pointer hash/authentication. Validate these **before trusting** lengths, addresses, counters, or offsets.
- Data extents carry checksums correctly bound to their contents and version, including shared/extant data. ZFS stores checksums in parent pointers; WitFS may do the same or use a dedicated checksum tree. The choice must be documented in an ADR.
- Offline `witfsck` checks reachability, membership, reverse links, reflink references, the free map, checksums, directory structure, and index consistency. Runtime `scrub` performs a full integrity traversal.
- Never promise automatic repair without a valid replica or mirror. On checksum mismatch, report corruption/I/O failure instead of silently returning invalid data.

### 10.5. Free-Space Management

Provide free ranges/bitmaps, **deferred reclamation** of blocks retained by snapshots, and recoverable interrupted garbage collection. For v0.1, prefer allocation from a known free map, staged allocations/deferred frees, and periodic mark/sweep as a leak-safety mechanism. Every commit must account for the fact that the free-space metadata itself uses CoW blocks.

Avoid the recursion "updating the free tree requires allocating a block, which itself requires updating the free tree." Use a transaction-local allocator with a reserved emergency metadata budget; publish the free-map changes in the same transaction. Do not promise that deletion never requires free space. Reserve capacity and test `ENOSPC` behavior.

### 10.6. Division of Responsibility with WitOS Volume Composition

Volume encryption, mirroring, and RAID belong in the existing **block composition layer** where they are general resource properties. WitFS needs to understand the lower layer's durability, integrity, alignment, and latency characteristics, but must not recreate the whole ZFS disk manager. File-level encryption may be added later, without conflating security threat models across layers.

---

## 11. Unified Commit, Snapshots, Recovery, and GC

### 11.1. Fundamental Invariant

**For every published generation `G`, all reachable file data, directory entries, inodes, secondary indexes, relationships, allocation metadata, and snapshot roots must describe one valid state.**

"Published" means that the root record has reached the durability level guaranteed by `IBlockStorage.FlushAsync()`. Incomplete scratch/dirty state must never become authoritative during recovery. A logical transaction may touch several keys: `rename` changes two directories, a name index, a path index, timestamps, and the change log, but is published with a single root switch.

### 11.2. Proposed Crash-Safe Commit Protocol (P/H)

```text
1. Read/lock working root generation G.
2. Stage a logical transaction T (data ranges + object/dir/attr/relations/index changes).
3. Allocate fresh blocks; write new data extents and modified CoW metadata paths.
4. Finish free-space state, refcount/reachability state and ChangeLog entry G+1.
5. Validate internal references, checksums, capacities and cycle constraints.
6. IBlockStorage.FlushAsync()     ← contents and metadata reach durable media
7. Write a new Root Record slot (generation G+1, checksum, full root set).
8. IBlockStorage.FlushAsync()     ← new committed root is durable
9. Publish G+1 in-memory, advance watch streams.
10. Collect previously unreachable blocks only when safe for all retained roots.
```

**Important qualifications:**

- This is a **proposed protocol**, not an already demonstrated implementation property. A torn root write is rejected by checksum; previous root slots remain intact. Blocks still reachable from prior roots must not be reused while recovery can fall back to those roots after power loss.
- If writing the root slot succeeds but the final flush does not complete, the caller may face an **indeterminate outcome**. Recovery could reveal that the commit succeeded. Write/commit APIs should therefore consider idempotency keys or transaction identifiers; the failure is not necessarily equivalent to rollback.
- Specify the exact distinction between what `fsync(file)` guarantees for content and the containing directory changes needed to find that file. Applications sometimes need to sync the **directory** after create/rename. A simple v0.1 can commit the whole volume at `fsync()`—costly but easier to verify.
- Hardware caches, dishonest flush behavior, defective media, memory corruption, and torn writes require dedicated testing. A flush API is necessary but not sufficient for safety.
- The volume needs either one commit serialization point or a proven optimistic conflict-detection/rebase algorithm. Otherwise concurrent writers can publish incompatible roots. For v0.1: concurrent readers with **one commit sequencer**.

### 11.3. Snapshots

`SnapshotId → (Generation, RootSet)`. A snapshot **retains reachable blocks**; it does not copy entire files. It must capture **all** semantic structures and indexes at one generation, not only the file directory. Read-only status, creation time, and retention policy are metadata layered over the retained root. Deleting a snapshot starts deferred reclamation that must be crash-safe and resumable.

A filesystem snapshot is **filesystem-consistent**, not automatically an application-consistent backup. An application with unflushed in-memory state may need a quiesce/flush protocol. A database with its own WAL may need a checkpoint or recovery from the crash-consistent image.

### 11.4. Reflinks and Copy Avoidance

`CloneContent(source,target)` would create a new file entry/ID with shared extents. Subsequent changes to either file must not modify the other. This is **not** a hard link, which gives two names to one object. Begin with volume-wide snapshots if necessary and defer per-file reflinks until reclamation/refcounts are proven. An underspecified reference model creates unbounded delete costs and data-integrity risks.

### 11.5. Root Ring and Recovery

At mount, scan a bounded set of verifiable root records, order by generation, and reject bad checksums, unsupported features, or incomplete mandatory roots. **A valid root checksum does not by itself prove that all descendant blocks exist.** Full scrubbing is expensive; minimally, validate critical metadata-tree roots and rely on the proven write/barrier ordering, with optional background verification.

If the newest generation is invalid, attempt recovery from the previous valid root. Make degraded read-only and recovery modes explicit. Never silently "repair" a free map by destroying user data.

### 11.6. Normal and Failure States (P)

| Sudden interruption point | Expected recovery |
|---|---|
| Before writing any new blocks | Remain at `G` |
| During data/metadata writes | Remain at `G`; orphan blocks may later be reclaimed |
| After first flush, before root record | Remain at `G`; new blocks are not yet published |
| During torn root-record write | Reject `G+1` by checksum and keep `G` |
| Root written, before its durable flush | Either `G` or `G+1`, as determined by recovery; do not promise one outcome |
| After the second durable flush | `G+1` must be recoverable |
| During GC | Every published root must remain reachable |

### 11.7. Performance and Future Optimization

Two durability barriers per small transaction provide a sound initial model but may be slower than ext4/ZFS. Possible optimizations include batched commits, grouped `fsync`, a specialized intent log, and read caches. Add them **only after demonstrating root atomicity and passing power-loss tests**. CoW can carry substantial random-write overhead for databases and virtual-machine images. A future dataset/profile may offer alternative tradeoffs, but must never silently disable checksum/CoW without documenting the lost guarantees.

---

## 12. Metadata, Properties, Relationship Graph, and Indexes

### 12.1. Paths and Identity Are Separate Models

**Object** metadata describes *what exists*. A **DirectoryEntry** describes *which name points to it within a particular directory*. No essential index should depend on a file's full canonical path. Thus an atomic rename changes directory entries and relevant name/path indexes, but neither `ObjectId` nor the relationships targeting that object.

Illustrative internal representation:

```text
Objects[objectId] = {kind, flags, size, timestamps, metadataRoot, extentRoot, ...}
DirEntry[(parentObjectId, normalizedName)] = {displayName, targetObjectId}
DirOrder[(parentObjectId, entryOrdinal)] = {normalizedName, targetObjectId}
Extents[(objectId, logicalOffset)] = {physicalBlock, length, checksum, flags}
```

`DirOrder` is optional. It can provide a cheap/stable `readdir` cursor, but inserts/renames would have to update it atomically alongside the main directory table. A simpler alternative is a prefix scan over `DirEntry` using a key-based cursor. Do not complicate the disk layout before measurements justify it.

**Hard link:** Several directory entries refer to one `ObjectId`; link counts are stored/verified. **Symbolic link:** A separate object containing a path string. **Reference relation:** An independent directed association between objects, **not** a directory entry or hard link. **Snapshot copy:** A reference to an older generation root, not another directory in the active generation. **Reflink:** A separate object with shared physical data extents. These concepts must remain distinct both on disk and in the APIs.

### 12.2. Typed Properties

```text
AttributeKey = (ObjectId, NamespaceId, AttributeId [, ElementId])
AttributeValue = (TypeCode, Version, Length, BinaryValue or BlobRef)
```

Examples: `system.mime` (string), `org.outwit.cad.material` (string), `org.outwit.simulation.load` (float64 with units), `user.tags` (set of strings), `media.width` (uint32), `content.sha256` (byte[32]). A small, stable catalog of scalar types covers strings, integers, decimal/binary floating point, booleans, timestamps, bytes, `ObjectId`, arrays, and sets; versioned schema extensions handle specialized types. This retains WinFS's key insight **without requiring a rich schema for every Item**.

**To be specified:** Unicode normalization and collation (for example, UTF-8 + NFC with ordinal case-sensitive lookup by default and an optional case-folded view), and lexicographically sortable numeric key encodings. Storing a little-endian `long` as an index key does not preserve numeric sort order. Timestamp precision and UTC conventions must also be fixed.

**No implicit content interpretation:** A query on `media.width` must not decode a JPEG for every predicate if that attribute was never set. A separate extraction service may materialize derived metadata with extractor version and provenance. Core metadata (names, sizes, modification time, explicitly written attributes) is strongly consistent. Derived content metadata is eventually consistent and separately marked.

### 12.3. Three Index Classes

1. **Mandatory structural indexes:** `ObjectId`, `(parentId, normalizedName)`, extent lookup, and snapshot directory. Filesystem correctness depends on these; they are always transactionally consistent.
2. **Built-in semantic indexes:** `TypeId` when present, `modified`, perhaps `tags`, depending on the selected profile. Such indexes remain within the CoW catalog and commit atomically.
3. **User-/application-defined indexes:** Fields such as `cad.material`, `sim.status`, and `photo.takenAt` are indexed through an explicit API. Each definition specifies its type, collation, version, and `Building`/`Ready`/`Failed` state.

Possible composite index keys:

```text
IX_ATTR: (AttributeDefId, TypeTag, SortableValue, ObjectId) → (optional payload)
IX_REL:  (RelationTypeId, SourceObjectId, TargetObjectId, RelationId) → properties
IX_RREL: (RelationTypeId, TargetObjectId, SourceObjectId, RelationId) → pointer
IX_DIR:  (ParentObjectId, NormalizedName) → ObjectId
```

`SortableValue` is a normalized representation, not a raw dump of a .NET object. Long strings require either a fixed key prefix plus verification against the complete value, or separate hashed equality versus sortable range indexes. Silent truncation that causes false matches is unacceptable, especially with collation, diacritics, `LIKE`, and prefix queries.

**Unique indexes** and inter-object consistency constraints must be evaluated within the volume transaction. An independent index store must never commit before the authoritative object store.

### 12.4. Building an Index After Millions of Objects Already Exist

Historical BeFS could leave pre-existing files outside newly created indexes. That is not an acceptable WitFS user experience. Proposed online backfill:

```text
1. Mark IndexDef = Building; record snapshot generation R.
2. Scan all matching objects at R into new private index root.
3. Concurrent commits append relevant changes to a bounded/catch-up stream.
4. Apply all changes from R+1 to latest committed generation.
5. Under short commit lock verify catch-up boundary; atomically publish IndexDef=Ready + index root.
6. Queries use index only from published Ready generation, otherwise report unavailable
   or explicit bounded scan with plan/cost limit.
```

Risks include reserved memory, inability to catch up during intense writes, crashes mid-backfill, and schema changes. The job must restart or resume from a trustworthy checkpoint; `Ready` must never be published before completion.

### 12.5. Relationship Graph

```text
Relation {
  RelationId : stable id
  TypeId     : namespaced relation type
  SourceId   : ObjectId
  TargetId   : ObjectId  // same volume initially
  Properties : optional typed values
}
```

Maintain **forward and reverse indexes** in the same commit; support typed incoming/outgoing traversals. Cross-volume or cross-machine relations are a separate extension: store a local external `ResourceRef` but **do not** imply distributed ACID or treat the external reference as a capability. A stored address string or GUID conveys no authority.

Directory structure should remain a specialized `DirEntry` mapping with unique names and POSIX semantics, not become an arbitrary graph: otherwise directory-cycle checks, rename, and link counts become hard to reason about or make efficient. Semantic relationships have their own lifecycle rules: **references, not ownership, by default**, and no surprise cascading deletion. Optional `OnDelete=Restrict/Cascade/KeepDangling` policies can be considered for schema v2 after precisely defining their effects.

### 12.6. Deletion Policy

- Removing a directory entry (`unlink`) decreases link count. Where POSIX semantics are supported, an object remains accessible through other names or open handles until those references close.
- Semantic relations must not unpredictably prevent `unlink`. Prefer an explicitly specified delete/mark-dangling policy for `Reference` edges. Relationship indexes remain consistent with object deletion.
- Removing an item from a smart folder does not automatically delete its source file. The shell must distinguish `Remove from view` (for example, modifying an attribute) from `Delete object` and obtain an explicit choice.
- Virtual folders do not own their contents; objects available only through a snapshot remain read-only.

### 12.7. Index Integrity Verification

The main rule is that each index is a **verifiable derivative of authoritative object data**, even though it is synchronously maintained. Every `(AttributeDefId, value, objectId)` index entry must correspond to an attribute record in the same root. `witfsck` must check index counts, detect orphan index entries, and support full reindexing in recovery/administrative mode without blindly trusting corrupt filename/path pointers.

---

## 13. Queries, Live Updates, and Virtual Views

### 13.1. A Simple and Predictable Query Language

The v0.1 filesystem service does not need SQL Server or a complete OPath implementation. An AST/API suffices:

```text
WitQuery = And | Or | Not | Compare(Attribute, operator, Value)
         | HasType(TypeId)
         | InDirectory(DirectoryId, recursive)
         | HasRelation(Type, direction, targetPredicate)
         | OrderBy(Attribute | SystemField)
         | Limit(N)
```

Use indexed execution. Initially implement index scans plus intersection/union and post-filtering, adding a cost-based planner only where justified. `HasRelation` must have bounded traversal depth/hops and explicit execution budgets. v0.1 must **not** execute arbitrary graph recursion or application-supplied code while holding filesystem locks.

Example:

```text
And(
  Eq("cad.project", "CastLever"),
  Eq("content.type", "model/step"),
  HasRelation(
    type: "isInputFor",
    direction: Outgoing,
    target: And(Eq("simulation.status", "finished"),
                Gte("simulation.loadN", 700.0))))
```

**Result:** `(ObjectId, optional PathHints, selected metadata, StorageVersion)`. Paths can change; an object can have no path or multiple names. Thus `ObjectId` is the primary result field; `PathHints` is a requested presentation, not identity.

### 13.2. One Consistency Point

`QueryAsync(query, ReadView=Generation G)` observes **one published root**, never a mixture of generations. CoW simplifies this greatly. Large results require cursors/pagination and a maximum lifetime for pinned reader snapshots; otherwise a UI or search client could hold physical blocks indefinitely and block garbage collection.

Potential read policies: `LatestCommitted`, `Snapshot(snapshotId)`, and `PinnedGeneration(G)` with TTL/resource limits. Do not silently pin a generation forever for every long-running client.

### 13.3. Loss-Free Live Query Contract

The classic "query first, then subscribe" sequence loses events between those operations. "Subscribe first, then query" risks duplicates. The recommended contract is:

```text
WatchQuery(query):
  1. Under snapshot read choose generation R.
  2. Enumerate current matches at R (snapshot phase).
  3. Atomically register/replay change feed starting after R.
  4. Send {Enter, Leave, Update} with Generation, ObjectId, sequence number.
```

`ChangeLog` records are created **in the same CoW transaction** as objects and indexes. Events are emitted only after the corresponding commit has been durably published. Subscribers use `R+n` cursors, backpressure, and bounded retention. When a client falls too far behind, history is lost, or buffers overflow, return **`ResyncRequired`** rather than pretending to guarantee infinite retention. Clients may resume after restart if the necessary committed history remains.

For multiple changes in one commit, group by `(generation, objectId)` so that `Enter`/`Leave` reflect the **final state** of the commit, not intermediate `Put` operations within it.

### 13.4. General `ChangeFeed`, Not Only `FileSystemWatcher`

`FileSystemWatcher` is a compatibility view over path events. Native `WatchResources`/`WatchQuery` APIs report object and semantic-predicate events. One committed rename may become old/new path events, but subscribers must not have to reconstruct the old name by querying an object already deleted. The ChangeLog should contain the necessary old/new names, parent IDs, and property identifiers, without exposing sensitive values by default.

### 13.5. Smart Folders Are Views, Not a Second Consistency Model

A saved query is a Resource containing an AST; for example, `My Simulations` with a predicate. The shell may present it as a folder and enumerate matching results. The v0.1 filesystem protocol may initially expose this only through the separate query API. A pseudo-directory namespace such as `/views` should come later, once POSIX `readdir`, pagination, multiple paths for one object, unlink/move behavior, and security have been specified.

**No implicit shell inference:** Dragging an item into a smart folder must not silently duplicate the file. An explicit UX action could modify an attribute, create a relationship, navigate to the source directory, or reject the operation. The backend should not choose one implicitly.

### 13.6. Full-Text Search and AI Semantics Belong in Separate Services

PDF/DOCX full-text extraction, OCR, vector embeddings, and AI tagging should run in separate, low-privilege job services. These use stable Resource APIs and write `derived.*` attributes with extractor version/provenance through the committed metadata API. **No content parser executes inside a filesystem transaction.** This preserves reliability and avoids coupling the filesystem lifecycle to libraries for hundreds of document formats.

For future OmnibusCloud/WitOS metadata, schema provenance, artifact relationships, and content hashes are sufficient foundations. Do not move distributed indexing or AI workflows into the volume commit coordinator.

---

## 14. System.IO Compatibility and WitOS Security

### 14.1. POSIX-Like Contracts to Preserve

- `open/create/truncate/close`, relative/absolute path normalization, `read/pread/write/pwrite`, directory enumeration, `chmod`, `stat`, and timestamp semantics.
- `rename(source,target)` must atomically replace a permissible target within one volume; cross-volume rename returns `EXDEV`. The current D5.1 defect is important: deletion of the target before rename must be removed from the namespace path before this guarantee is claimed.
- **Unlink of an open file:** Where POSIX compatibility is promised, an open handle remains valid until closed, even after the directory name is removed. Snapshots may retain extents independently of open-handle counts.
- Specify `fsync(fd)` and `fsync(dir)` guarantees precisely. With POSIX-like semantics, syncing a target file's bytes may not be sufficient to make a rename durable. A stronger but slower v0.1 whole-volume commit is an option.
- Symbolic links, prohibition of directory cycles, correct link counts, absence of dangling directory entries, and predictable directory enumeration/cursors.
- Choose Unicode normalization and default case sensitivity at format time, or make the policy explicit at `mkfs`. Comparison rules must not change for an existing volume without migrating the format/indexes.

### 14.2. Native API Versus Compatibility Namespace

Instead of adding twenty mandatory methods to `IFileSystem`, introduce opt-in `IWitSemanticStore`, `IChangeFeed`, `ISnapshotStore`, and `IIntegrityStore` contracts. Providers advertise `Capabilities`, and callers negotiate feature availability. `System.IO` remains unaware of relationships and indexes; FAT/ext4 must not provide misleading no-op implementations that appear to guarantee unsupported features.

Initially, WitFS can be the third `IFileSystem` provider. Add a semantic IPC endpoint beneath Namespace or grant a direct service capability afterward. Do not tunnel native queries through cleverly encoded filenames.

### 14.3. Security

WitOS RFC 0002/RFC 0008 requires separating `ResourceId` from authority. An attribute index can reveal that a hidden file exists **even if its content is never returned**. Query evaluation must apply authorization constraints **before exposing results or counts**, including during relationship traversal. Knowing an `ObjectId` must not confer access.

- Distinguish capabilities for reading/writing content, reading/writing metadata, creating relations, enumerating directories, creating/managing snapshots, and administering indexes.
- Namespace services/brokers can issue attenuated capabilities. The volume may store persistent ownership/policy metadata, but it must not serialize live capability handles as trusted persistent data.
- A relationship to a private object must not implicitly grant access to that target, even when the source is accessible.
- Hidden-result counts can themselves be sensitive and must not be exposed without authority.
- Updating reserved/system attributes must require different authority from writing arbitrary user attributes.
- Bound query complexity, index sizes, attribute/relation counts, extent recursion, and event subscriptions. The filesystem service is an attack surface.

**Current implementation note:** D5.1 has no authority enforcement below the volume. Implementing these security rules is therefore **future integration work**, not a feature WitOS already delivers.

### 14.4. Bootstrap and Dependencies

WitFS runs under .NET, but the runtime, bootstrap program, and required SDK must be available **before** WitFS is mounted. Do not immediately replace `/boot` or early runtime storage with an unproven new format. Keep the boot package/FAT/ext4 as an independent baseline, mount WitFS later, and only consider boot-path integration after the format and recovery have been validated.

---

## 15. Provisional Architecture APIs

All C# declarations in this section are **proposals**, not current WitOS APIs. They identify the intended boundaries and prevent paths, IDs, and capabilities from being conflated. Types are deliberately schematic.

### 15.1. Native Semantic API

```csharp
public readonly record struct WitObjectId(Guid VolumeId, ulong LocalId);
public readonly record struct WitGeneration(ulong Value);
public readonly record struct WitSnapshotId(Guid Value);

public enum WitValueType { String, Int64, UInt64, Float64, Bool, Time, Bytes, ObjectId }
public sealed record WitAttribute(string Namespace, string Name, WitValueType Type, object Value);
public sealed record WitRelation(Guid RelationId, string TypeId, WitObjectId Source, WitObjectId Target);

public interface IWitResourceStore
{
    ValueTask<WitObject> GetObjectAsync(WitObjectId id, WitReadView view, CancellationToken ct);
    ValueTask<IReadOnlyList<WitAttribute>> GetAttributesAsync(WitObjectId id, WitReadView view, CancellationToken ct);
    ValueTask<IReadOnlyList<WitRelation>> GetRelationsAsync(WitObjectId id, RelationFilter filter, WitReadView view, CancellationToken ct);
    ValueTask<IWitTransaction> BeginTransactionAsync(CancellationToken ct);
}

public interface IWitTransaction : IAsyncDisposable
{
    ValueTask SetAttributeAsync(WitObjectId id, WitAttribute value, CancellationToken ct);
    ValueTask AddRelationAsync(WitRelation relation, CancellationToken ct);
    ValueTask RenameAsync(WitObjectId sourceParent, string sourceName,
                           WitObjectId targetParent, string targetName,
                           RenamePolicy policy, CancellationToken ct);
    ValueTask<WitGeneration> CommitAsync(CancellationToken ct);
}
```

**Mandatory guarantee:** Property/relationship changes and file-data operations participating in one `IWitTransaction` must commit at one generation. If `IFileHandle` cannot join such a transaction, additional `ITransactionalFileHandle` or `WriteObjectData` operations are needed. Do not promise unified transactions until this mechanism has been designed and tested.

### 15.2. Queries and Subscriptions

```csharp
public interface IWitQueryService
{
    IAsyncEnumerable<WitObjectId> QueryAsync(
        WitQuery query, WitReadView view, QueryBudget budget, CancellationToken ct);

    IAsyncEnumerable<WitQueryDelta> WatchQueryAsync(
        WitQuery query, WitGeneration? resumeAfter, QueryBudget budget, CancellationToken ct);
}

public sealed record WitQueryDelta(
    WitGeneration Revision, long Sequence, WitObjectId ObjectId,
    WitDeltaKind Kind, string? ChangedAttribute, bool ResyncRequired);
```

Before the first delta stream, the protocol needs `InitialSnapshot(R)` or a separate consistent query phase at `R`; otherwise there is a race. The illustrative `WatchQueryAsync` is a statement of intent, not a complete wire protocol.

### 15.3. Snapshot and Integrity APIs

```csharp
public interface IWitSnapshotStore
{
    ValueTask<WitSnapshotId> CreateSnapshotAsync(string? label, CancellationToken ct);
    ValueTask DeleteSnapshotAsync(WitSnapshotId id, CancellationToken ct);
    ValueTask<WitReadView> OpenSnapshotAsync(WitSnapshotId id, CancellationToken ct);
}

public interface IWitIntegrityStore
{
    IAsyncEnumerable<IntegrityFinding> ScrubAsync(ScrubOptions options, CancellationToken ct);
}
```

Introduce `RestoreSnapshot` separately after specifying rollback semantics: what happens to clients, open handles, watch cursors, persistent capabilities, and indexes when an active volume is reverted? For v0.1, restoration may be restricted to offline mode under an exclusive mount.

### 15.4. Internal Low-Level Interfaces

```csharp
// Internal only: conceptual; actual return types must carry ownership / lifetimes.
interface IWitCoWBlockStore
{
    ValueTask<BlockRef> WriteNewBlockAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct);
    ValueTask ReadVerifiedBlockAsync(BlockRef block, Memory<byte> destination, CancellationToken ct);
    ValueTask FlushDataAsync(CancellationToken ct);
    ValueTask PublishRootAsync(WitRootSet roots, ulong nextGeneration, CancellationToken ct);
}

interface IWitMetadataTree
{
    ValueTask<byte[]?> GetAsync(ReadOnlyMemory<byte> key, WitReadView view, CancellationToken ct);
    IAsyncEnumerable<KeyValuePair<byte[], byte[]>> ScanAsync(KeyRange range, WitReadView view, CancellationToken ct);
    ValueTask<TreeRoot> ApplyAsync(TreeRoot parent, IReadOnlyList<KeyValueChange> changes, CancellationToken ct);
}
```

**Requires a separate decision:** Implement `IWitMetadataTree` as a new CoW B+Tree or adapt WitDatabase's BTree behind snapshot-aware page mapping. Both alternatives need one coordinating transaction and a crash-consistency argument. These interfaces deliberately do not call `System.IO`.

---

## 16. Research, Prototype, and Verification Plan

### Stage 0 — Establish Correctness and Platform Constraints

**Deliverables:** `WitFS-Requirements.md`, four to six ADRs, a specification of minimal native operations, and a host conformance suite. Fix the single-writer policy, durability semantics, block/sector alignment, mount/bootstrapping order, resource limits, Unicode policy, zero-byte objects, identity rules, and ACL integration milestones.

**Exit criteria:** Every critical operation has testable invariants; current security/durability guarantees are clearly distinguished from future features; a POSIX compatibility matrix exists for current `IFileSystem`/Namespace. **Do not freeze the on-disk format before the first two spikes.**

### Stage A — Semantic Model on Existing Storage

Build a **host-only prototype** of metadata/relationships over existing ext4 or regular `StorageFile` + `StoreBTree`, with a simple `ResourceId` map. Validate query modeling, graph examples, index backfill, and revision-aware live queries. "Host-only" means experimental code, **not** a change to production WitOS mounting.

**Constraint:** As long as these are separate stores, do not promise genuine atomic volume transactions. Test crash reconciliation/repair for the prototype, but do not present it as final WitFS durability.

**Acceptance:** 100,000–1,000,000 logical objects with combinations of indexed properties and relations; queries checked against a reference in-memory evaluator; correct committed `Enter/Leave` events; online indexes including objects created before index definition.

### Stage B — Minimal CoW Block Engine (in Parallel with A)

Initially avoid WitDatabase. Use `MemoryBlockStorage`, two root slots or a ring, a free-space allocator, verifiable immutable blocks, and one key/value mapping or file-to-extent table. Implement flush-based commit and inject power failures **at every write/flush/commit boundary**. Remount must select the last valid durably published root.

**Acceptance:** Compare against a model reference after thousands of operation sequences and every I/O cutoff; corrupt or unwritten blocks are never interpreted as valid content; a power failure never creates dangling pointers reachable from a committed root.

### Stage C — Compare Two WitDatabase Reuse Approaches

- **C1:** `StoreBTree` + transaction-scoped `IStorage` with logical→physical-CoW mapping and isolated page-cache generations.
- **C2:** A dedicated immutable CoW B+Tree reusing WitDatabase BTree code/algorithms where practical.

**Decide using evidence:** Source-change footprint, testing cost, write amplification, random-insert throughput, scans, independent snapshot reads, I/O counts, and synchronous-API deadlock risk. **Decision gate:** If C1 fundamentally disrupts `PageManager`/cache assumptions, move to C2 rather than endlessly adapt C1.

### Stage D — First Native WitFS Filesystem Provider

Implement `Create/Open/Read/Write/Truncate/Mkdir/Enumerate/Delete/Rename/Stat/Flush/Check`, extents, Unix metadata, and symbolic links on the new volume format. Claim feature support only after testing. Integrate with `IFileSystem`, host tests, and QEMU `IBlockStorage`. Mount first at a dedicated `/witfs-test` projection; **do not automatically migrate `/data`**.

**Acceptance:** The same baseline scenarios succeed on ext4 and WitFS except for documented differences; corrupt data never triggers out-of-range access; recovery handles rename-replace and truncate crashes; background load does not violate flush barriers. Implement `witfs-mkfs`, `witfs-check`, and `witfs-inspect` **before trusting real user data**.

### Stage E — Native Attributes, Relationships, and Indexes

Add `IWitResourceStore`, typed attributes, and the graph. Include secondary indexes in **one CoW commit**. Implement index creation/backfill, query parser/planner, and change streams. Save query metadata, but virtual directories need not exist yet.

**Acceptance:** Create/rename/set-attribute/add-relation operations produce one consistent state; crashes during any write/flush between data/index changes cannot publish a false index; a query at snapshot `R` always matches root `R`.

### Stage F — Complete Read-Only Snapshots

Implement retained roots, reference-aware reclamation, snapshot read views, and scrub; add reflinks afterward. Test foreground I/O plus GC plus crash injection while deleting snapshots. Start with conservative GC and optimize reference accounting only after proving invariants.

**Acceptance:** Pre-/post-snapshot bytes, attributes, relations, and indexes compare correctly; repeated create/delete snapshot cycles neither lose live data nor leak storage without bound; space overhead is measured.

### Stage G — Hardening and Production Readiness

- Host fuzz corpus, property-based randomized sequences, and malformed/corrupt disk-image corpus.
- Fault injection: Torn block, lost write, reordering before/after barriers, I/O error, `ENOSPC`, out-of-memory, cancellation mid-operation, service kill/restart, checksum corruption, simultaneous mounts/read-only mounts.
- Concurrency: 1..N readers, one writer, old-snapshot readers, watch streams, async deadlock detection, `FileSystemWatcher` compatibility, and open/unlink behavior.
- Benchmarks: `fio`-like random/sequential I/O, one million small files, one million attributes, ten million index entries, one million relations, snapshot creation/deletion, mass rename, restart/mount latency, cache pressure, and GC backlog.
- Tooling: `mkfs`, `inspect`, `check`, `scrub`, image exporter, metadata dumper, root viewer, offline recovery.
- Reproducibility: Fixed random seeds, baseline ext4/Btrfs for POSIX behavior, and RedoxFS under comparable workloads where feasible. **Verify data contents, not just exit codes.**

**Only then** consider migration of production `/data`, early-boot support, or public claims of data durability.

### WitOS-Specific Tests

1. Verify that `IBlockStorage.FlushAsync()` acts as a genuine durability barrier across `CachedBlockStorage → partition → virtio/NVMe`. Use no-reordering fault injection, not only mocks.
2. Test USB/removable media, hot-unplug, `CHANNEL_PEER_CLOSED`, driver-service restart, partial transfers, and device capability revocation.
3. Test concurrent `NamespaceSession` sessions, locking, and atomic rename. The present per-process single-request path may significantly constrain throughput.
4. Test `System.IO` streams, directory enumeration, timestamps, private/shared memory mapping, asynchronous cancellation, delayed disposal, and symlink resolution.
5. Verify that WitFS root metadata never depends on `StorageFile` atop the **same** WitFS volume (detect recursive dependencies with simulated failures).

---

## 17. Reuse Matrix, Risks, and ADR Decisions

### 17.1. Reuse Matrix

| Source | Mechanism / code | Preferred reuse | Risk | Status |
|---|---|---|---|---|
| WinFS | Items / Extensions / Relationships | Adapt semantics | Schema breadth and complexity | D→P |
| WinFS | P2P synchronization/conflict model | Future research only | Distributed scope | Deferred |
| BeFS | Typed attributes and live-query UX | Reuse concept, not C++ implementation | Need a dedicated atomic/replayable watcher protocol | P |
| Haiku BFS | B+Tree / Index / Journal | Algorithmic reference | Tightly coupled to vnode/kernel | S |
| RedoxFS | Header ring / CoW generations / BlockPtr hashes | Reference, host-side test oracle | `Disk` durability barrier not explicit | S/H |
| RedoxFS | Tree/allocator algorithms | Selective adaptation after spike; preserve MIT notices | Rust-to-.NET port; missing required attrs/relations | H |
| OpenZFS | Root commit / checksum tree / snapshot semantics | Reimplement architecture | CoW overhead and complexity | D→P |
| Btrfs | Tree separation, backrefs, reflinks | Design reference, not GPL code | Write amplification/reference management | D→P |
| WitDatabase `IStorage` | Page I/O contract | New native CoW backend | Sync/async split | S/H |
| WitDatabase BTree | Search/split/scan/encoding | CoW tree or logical-page map after spike | Mutable pages and cache assumptions | S/H |
| WitDatabase WAL | Journaling and recovery tests | Patterns/testing methods | Separate `FileStream` and transaction boundary | S |
| WitDatabase IndexManager | Index-storage abstractions | Algorithms/lookup APIs under shared root | No automatic volume-wide commit | S/H |
| WitOS ext4/FAT | Compatibility and migration inputs | Keep | Legacy formats lack WitFS-native semantic graph | S |
| WitOS `IBlockStorage` | Durable flush, async I/O, alignment | Consume directly | Verify every layer's flush implementation | S/H |

### 17.2. Principal Risk Register

| ID | Risk | Why it matters | Mitigation |
|---|---|---|---|
| R1 | Data loss from incorrect write/flush ordering | Metadata tree and user data corruption | Fault injection at each stage; durable-root protocol |
| R2 | WitDatabase BTree cannot support CoW without substantial changes | No snapshot-safe metadata; mixed cache generations | DB-1/DB-2 before format freeze |
| R3 | `ENOSPC` during metadata commit/GC | Even deletion may fail | Reserves, staged allocation, deterministic tests |
| R4 | Split ext4 + metadata database | Stores diverge after crash | Experimental prototype only |
| R5 | Live event published before commit or lost | Incorrect UI and automation | Snapshot + durable change-feed cursor |
| R6 | Index/graph bypasses authorization | Leakage of names, attributes, sizes | Capability filtering; authorize before results |
| R7 | Long-lived snapshots block GC | Storage exhaustion | Retention policies, quotas, pinned-reader limits |
| R8 | Write amplification and fragmentation | Random-write performance | Extent design, batching, later profiles |
| R9 | Overly complex ontology harms usability | Repeat WinFS scope risk | Schema-less baseline, optional types |
| R10 | Disk format accidentally depends on .NET object ABI | Runtime/CPU version incompatibility | Explicit endian-aware serialization, versioned fields |
| R11 | Filesystem requires runtime from its own unmounted volume | Boot dependency cycle | Independent boot package, staged mount |
| R12 | Implicit repair promises without redundancy | Incorrect reliability expectations | Always detect; repair only from valid copy |

### 17.3. Decisions That Can Be Recorded Now

**ADR-WFS-001 — WitFS as a managed userspace filesystem service.** Reuse the WitOS service architecture; add no filesystem policy to the kernel.

**ADR-WFS-002 — Native on-disk format, not ext4 plus a sidecar.** Keep ext4 for compatibility/prototyping; production uses one root and one volume-local commit boundary.

**ADR-WFS-003 — `ObjectId != Path != Capability != Revision`.** Identity, namespace membership, and authority are different concepts.

**ADR-WFS-004 — Schema-independent baseline files.** Ordinary files can be created without installing types or relations.

**ADR-WFS-005 — CoW root and enforced flush barriers for durable commit.** The first release need not have ZIL if durable-root commits are correct.

**ADR-WFS-006 — Indexes and relations participate in the same volume commit.** Native explicitly written attributes cannot rely on permanently eventually consistent indexes; eventual consistency is reserved for extracted/derived metadata.

**ADR-WFS-007 — Single-device local volume first.** RAID/block composition remains outside WitFS; external relationship transactions are outside local ACID scope.

### 17.4. Decisions That **Must Wait for Spikes**

- **ADR-WFS-008:** Reuse the mutable `StoreBTree` behind CoW mapping or write an immutable tree using selected WitDatabase algorithms.
- **ADR-WFS-009:** Use one prefixed-keyspace KV tree or multiple independently rooted CoW metadata trees.
- **ADR-WFS-010:** Use parent-pointer hashes or a separate checksum tree for file data.
- **ADR-WFS-011:** Block and record sizes, inline files, extents, and deduplication/reflink strategy.
- **ADR-WFS-012:** Unicode case-folding policy and whether per-directory overrides are permitted.
- **ADR-WFS-013:** First snapshot scope: whole volume versus subvolumes/datasets.
- **ADR-WFS-014:** GC design: refcounts/backrefs, mark/sweep, background cleaner, transaction-safe reserves.
- **ADR-WFS-015:** Security integration: persistent permissions, capability broker, ACLs, grants.

### 17.5. Value First, Complexity Later

```text
     Semantic Model                           Durable Volume
       (WinFS+BeFS)                         (ZFS+Btrfs+Redox)
            │                                      │
  Object/Attrs/Graph/Query                   CoW root + barriers
            │                                      │
  Prototype on host KV                       Host block image tests
            └──────────────────┬───────────────────┘
                               ▼
                        WitFS C# provider
                               │
                      Durable native indexes
                               │
                        Read-only snapshots
                               │
                Search/shell integration and hardening
                               │
                   Reflink / future replication
```

Do not make the entire plan sequential. The semantic prototype on existing storage and the CoW engine investigation **can proceed independently** and then converge through one commit coordinator.

---

## 18. Primary Sources and Reviewed Revisions

### 18.1. Reviewed Repository Source

- **WitOS:** commit [`cedc0be5b34701aeaca5bb77d874becb9b4452f4`](https://github.com/dmitrat/WitOS/tree/cedc0be5b34701aeaca5bb77d874becb9b4452f4) (October 23, 2026). Key files: [`IBlockStorage`](https://github.com/dmitrat/WitOS/blob/cedc0be5b34701aeaca5bb77d874becb9b4452f4/src/OutWit.OS/Storage/IBlockStorage.cs), [`IFileSystem`](https://github.com/dmitrat/WitOS/blob/cedc0be5b34701aeaca5bb77d874becb9b4452f4/src/OutWit.OS/FileSystems/IFileSystem.cs), [`NamespaceSession`](https://github.com/dmitrat/WitOS/blob/cedc0be5b34701aeaca5bb77d874becb9b4452f4/src/Services/Namespace/Server/NamespaceSession.cs), [`VolumeMounter`](https://github.com/dmitrat/WitOS/blob/cedc0be5b34701aeaca5bb77d874becb9b4452f4/src/Services/Namespace/Volumes/VolumeMounter.cs), [`RFC 0008`](https://github.com/dmitrat/WitOS/blob/cedc0be5b34701aeaca5bb77d874becb9b4452f4/@Docs/RFC-0008-Storage-and-Persistent-State-Model.md), [`D5.1`](https://github.com/dmitrat/WitOS/blob/cedc0be5b34701aeaca5bb77d874becb9b4452f4/@Docs/Implementation/D5.1-Namespace-Service.md).
- **WitDatabase:** commit [`ba00a08a0f2dfffa3fc97d440b758aab5c0dd37c`](https://github.com/dmitrat/WitDatabase/tree/ba00a08a0f2dfffa3fc97d440b758aab5c0dd37c) (August 19, 2026). [`IStorage`](https://github.com/dmitrat/WitDatabase/blob/ba00a08a0f2dfffa3fc97d440b758aab5c0dd37c/Sources/Core/OutWit.Database.Core/Interfaces/IStorage.cs), [`StoreBTree`](https://github.com/dmitrat/WitDatabase/blob/ba00a08a0f2dfffa3fc97d440b758aab5c0dd37c/Sources/Core/OutWit.Database.Core/Stores/StoreBTree.cs), [`BTree.Insert`](https://github.com/dmitrat/WitDatabase/blob/ba00a08a0f2dfffa3fc97d440b758aab5c0dd37c/Sources/Core/OutWit.Database.Core/Tree/BTree.Insert.cs), [`PageManager`](https://github.com/dmitrat/WitDatabase/blob/ba00a08a0f2dfffa3fc97d440b758aab5c0dd37c/Sources/Core/OutWit.Database.Core/Managers/PageManager.cs), [`TransactionalStore`](https://github.com/dmitrat/WitDatabase/blob/ba00a08a0f2dfffa3fc97d440b758aab5c0dd37c/Sources/Core/OutWit.Database.Core/Transactions/TransactionalStore.cs), [`WriteAheadLogBase`](https://github.com/dmitrat/WitDatabase/blob/ba00a08a0f2dfffa3fc97d440b758aab5c0dd37c/Sources/Core/OutWit.Database.Core/Wal/WriteAheadLogBase.cs), [`IndexManager`](https://github.com/dmitrat/WitDatabase/blob/ba00a08a0f2dfffa3fc97d440b758aab5c0dd37c/Sources/Core/OutWit.Database.Core/Indexes/IndexManager.cs).
- **Haiku BFS:** commit [`660ea78079ca9ee36623a0562208476bce89b11e`](https://github.com/haiku/haiku/tree/660ea78079ca9ee36623a0562208476bce89b11e) (October 22, 2026). [`Attribute.cpp`](https://github.com/haiku/haiku/blob/660ea78079ca9ee36623a0562208476bce89b11e/src/add-ons/kernel/file_systems/bfs/Attribute.cpp), [`Index.cpp`](https://github.com/haiku/haiku/blob/660ea78079ca9ee36623a0562208476bce89b11e/src/add-ons/kernel/file_systems/bfs/Index.cpp), [`Query.cpp`](https://github.com/haiku/haiku/blob/660ea78079ca9ee36623a0562208476bce89b11e/src/add-ons/kernel/file_systems/bfs/Query.cpp), [`BPlusTree.cpp`](https://github.com/haiku/haiku/blob/660ea78079ca9ee36623a0562208476bce89b11e/src/add-ons/kernel/file_systems/bfs/BPlusTree.cpp), [`Journal.cpp`](https://github.com/haiku/haiku/blob/660ea78079ca9ee36623a0562208476bce89b11e/src/add-ons/kernel/file_systems/bfs/Journal.cpp).
- **RedoxFS:** commit [`b87b0976ee122036b2ec040159b890e1e06ca139`](https://github.com/redox-os/redoxfs/tree/b87b0976ee122036b2ec040159b890e1e06ca139) (September 7, 2026). [`src/disk/mod.rs`](https://github.com/redox-os/redoxfs/blob/b87b0976ee122036b2ec040159b890e1e06ca139/src/disk/mod.rs), [`src/header.rs`](https://github.com/redox-os/redoxfs/blob/b87b0976ee122036b2ec040159b890e1e06ca139/src/header.rs), [`src/transaction.rs`](https://github.com/redox-os/redoxfs/blob/b87b0976ee122036b2ec040159b890e1e06ca139/src/transaction.rs), [`src/block.rs`](https://github.com/redox-os/redoxfs/blob/b87b0976ee122036b2ec040159b890e1e06ca139/src/block.rs), [`src/clone.rs`](https://github.com/redox-os/redoxfs/blob/b87b0976ee122036b2ec040159b890e1e06ca139/src/clone.rs), [`LICENSE`](https://github.com/redox-os/redoxfs/blob/b87b0976ee122036b2ec040159b890e1e06ca139/LICENSE).

### 18.2. Historical WinFS Sources

1. [WinFS: The Longhorn Data Layer (Kathleen Dollard, 2004)](https://visualstudiomagazine.com/articles/2004/02/01/winfs-the-longhorn-data-layer.aspx) — Original Items/Relationships/Extensions model.
2. [Inside Longhorn Architecture / WinFS (archive)](https://archive.visualstudiomagazine.com/resources/longhorn/winfs.asp) — Separation of APIs, schemas, relational engine, and NTFS.
3. [WinFS: A First Look (Directions on Microsoft, 2005)](https://www.directionsonmicrosoft.com/reports/winfs-a-first-look/) — Contemporary description of the beta and its features.
4. [WinFS Beta 1: Quentin Clark (BetaNews, 2005)](https://betanews.com/article/ms-explains-winfs-releases-beta-1/) — Emphasizes SQL-like technology **above** NTFS.
5. [WinFS Officially Dropped (InfoQ, 2006)](https://www.infoq.com/news/WinFS-Officially-Dropped/) — Cancellation as a separately shipped product.
6. [Peer-to-Peer Replication in WinFS (Microsoft Research, 2006)](https://www.microsoft.com/en-us/research/publication/peer-to-peer-replication-in-winfs/) — Distributed replication model; **not** evidence that it shipped in Windows.

### 18.3. BeFS and Haiku

7. [Dominic Giampaolo, *Practical File System Design with the Be File System* (1999)](https://www.haiku-os.org/legacy-docs/practical-file-system-design.pdf) — Author's account of structures, indexes, queries, and journaling. Web access provided PDF text, but the page-screenshot tool returned a cache miss during visual verification; figures were **not reconstructed from unverified imagery**.
8. [Haiku User Guide: Attributes](https://i18n.haiku-os.org/userguide/data/export/docs/userguide/en/attributes.html) — Examples of information objects with empty content and editable fields.
9. [Haiku User Guide: Queries](https://i18n.haiku-os.org/userguide/data/export/docs/userguide/ru/queries.html) — Indexed/live queries and saved queries.
10. [Haiku License.md](https://github.com/haiku/haiku/blob/660ea78079ca9ee36623a0562208476bce89b11e/License.md) — Primarily MIT, with possible file-specific exceptions.

### 18.4. Modern CoW Sources

11. [RedoxFS README](https://github.com/redox-os/redoxfs) — Advertised capabilities.
12. [OpenZFS: Copy-on-Write, Uberblock, txg, ZIL](https://openzfs.github.io/openzfs-docs/Basic%20Concepts/Copy-on-write.html).
13. [OpenZFS: Checksums and Their Use](https://openzfs.github.io/openzfs-docs/Basic%20Concepts/Data%20Storage/Checksums.html).
14. [OpenZFS: Scrub and Resilver](https://openzfs.github.io/openzfs-docs/Basic%20Concepts/Operations/Scrub%20and%20Resilver.html).
15. [OpenZFS License](https://openzfs.github.io/openzfs-docs/License.html) — CDDL.
16. [Btrfs developer documentation: B-trees](https://btrfs.readthedocs.io/en/latest/dev/dev-btrees.html).
17. [Btrfs developer documentation: On-disk Format](https://btrfs.readthedocs.io/en/latest/dev/On-disk-format.html) — Maintainers flag some content as outdated; cross-check against the B-tree documentation.
18. [Btrfs: Design](https://btrfs.readthedocs.io/en/latest/dev/dev-btrfs-design.html).
19. [Btrfs: Reflink](https://btrfs.readthedocs.io/en/stable/Reflink.html).
20. [Btrfs utilities + GPLv2](https://github.com/kdave/btrfs-progs).

### 18.5. Licensing and Research Boundaries

- WitOS and WitDatabase: Apache-2.0; RedoxFS: MIT; principal reviewed Haiku BFS files: MIT, subject to individual file headers; OpenZFS: CDDL; Btrfs/Linux implementation: GPL family (btrfs-progs GPLv2, `libbtrfsutil` LGPLv2.1).
- No third-party source code was incorporated into this document; only conceptual diagrams and illustrative APIs appear. Links point to the relevant sources and reviewed revisions.
- This research **does not establish** that a working WitFS prototype, on-disk image, benchmark data, or proven RedoxFS/WitDatabase port exists. It is an architecture proposal with explicit validation experiments.

---

## 19. Resource-First UX: Named Volumes and Typed Devices

**Status:** Principle agreed October 23, 2026; not implemented. **Full architectural decision:** [WitOS ADR — Resource-First Public Model of Storage and Devices](WitOS-ADR-Resource-First-Public-Model-EN.md).

### 19.1. WitFS Responsibility Boundary

WitFS is the filesystem of **one particular logical volume**. Its CoW trees, indexes, relationships, snapshots, and unified commit must not define how all of WitOS presents disks, cameras, GPUs, and network resources. A system-wide **Volume Catalog** discovers logical volumes, a **Device Catalog** discovers physical and virtual devices, and a **Namespace Resolver** establishes visible names and authorized resource projections for each session. These system services are not part of the WitFS on-disk format.

### 19.2. Public Interface

The file manager's top-level **Computer** view contains **Storage**, **Devices**, and **Collections**, rather than exposing one mandatory `/` directory. Volumes appear as independent objects with meaningful names such as `System`, `Data`, `Backup`, and `Work`. A camera need not be represented as a file. Within a selected volume, conventional directories and files remain available; semantic collections may combine objects from several volumes.

**A volume's name is not its identity.** Distinguish `VolumeId`, mutable `DisplayLabel`, and short `Alias`. Persist a stable ID or capability for long-lived references rather than the string `Data`.

### 19.3. .NET and PowerShell Compatibility

**Preserve** the ADR 0024 decision to use upstream Unix-form .NET. Ordinary `File.OpenRead("/data/...")` continues to work; `Data:` is not declared a universally accepted absolute `System.IO.Path`. The native SDK first identifies and opens a volume and then works with relative paths. A bridge may return a compatible path in the current process namespace when mounting and permissions permit.

PowerShell uses named **PSDrives** for volumes (`Data:`, `Work:`) and dedicated providers/cmdlets for devices (`Devices:`). A PSDrive must not be confused with an absolute `System.IO` path.

### 19.4. Practical Impact

Keep the existing `IFileSystem`, `IBlockStorage`, `MountTable`, and `/boot`, `/data`, `/fat` mounts. Treat `MountTable` as a **POSIX projection** for standard applications, not the authoritative volume registry. Add Volume/Device Catalogs and public SDK APIs to the WitOS roadmap independently of WitFS. An initial catalog can describe existing ext4/FAT volumes; migrating to WitFS is not a prerequisite.

**Storage guarantees are unchanged:** CoW/atomic commit applies to one WitFS volume. Federated queries across volumes are views, not automatically shared transactions or snapshots.

---

## Conclusion: A Native WitFS, Not a Compilation of Other Filesystems

The most important decision is **not to start by porting all of WinFS, BeFS, RedoxFS, Btrfs, or ZFS**. WitFS's novel value lies at the intersection: full filesystem semantics combined with objects, typed properties, and relationships, all sharing the physical atomicity of ordinary file content. WinFS did not reach general deployment as a standalone product; BeFS demonstrated the usefulness of indexed attributes and live queries but did not pursue a rich typed graph or modern CoW snapshots; ZFS/Btrfs address physical reliability without providing a WinFS-style first-class semantic graph; WitDatabase contains many useful .NET building blocks but is not currently a CoW filesystem.

**Recommended next engineering action:** Do not immediately implement one enormous `WitFsFileSystem.cs`. In parallel, build **(A) a semantic prototype for objects, directories, attributes, relationships, and queries** and **(B) a verified CoW volume engine with durable barriers**. Then perform the *C1/C2 comparison* to identify the appropriate boundary for reuse of actual WitDatabase code. Freeze the binary disk format only after these experiments.
