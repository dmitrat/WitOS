# WitOS ADR — Resource-First Public Model of Storage and Devices

**Decision:** Public user and developer model of WitOS resources  
**Date:** October 23, 2026  
**Status:** Concept agreed in the design discussion; draft ADR for repository review, **not** an implemented API contract  
**Scope:** WitOS Shell, Storage/Device Catalogs, Namespace, PowerShell, native .NET SDK; all filesystem providers, including WitFS, FAT, and ext4  
**Out of scope:** Defining a new WitFS on-disk format, modifying .NET CoreLib, or providing full Win32 compatibility  
**Repository changes:** None.

> **Decision.** WitOS presents users and developers with an intelligible set of independent resources: **volumes, devices, network storage, and virtual collections**. Volumes have their own names; devices are presented by type and capabilities. A unified POSIX path tree remains available as a compatibility projection, but **is neither the primary user interface nor a requirement of the native WitOS API**. Upstream .NET remains unchanged.

---

## 1. Motivation

The Unix model exposes nearly all user-visible filesystem storage through paths in a single tree rooted at `/`. This is useful for POSIX applications and abstracts away physical placement. WitOS, however, should make the distinction between a logical volume, a physical drive, a camera, a printer, a GPU, and a network resource obvious to both users and developers.

Distinct entities should not be conflated merely to give them a common path syntax. A camera need not be a special file; an SSD need not be a directory; a search-based document collection need not physically contain the documents it presents.

Relevant precedents:

- **Windows:** Users see volumes as separate drives, although Windows NT has a more elaborate internal object namespace and supports mounting volumes in directories.
- **BeOS/Haiku:** The file manager presents mounted volumes as understandable independent objects.
- **AmigaOS:** Readable volume names and logical assignments instead of requiring one-letter identifiers.
- **PowerShell:** Object-oriented providers and `PSDrive` abstractions, including resources that are not physical directories.
- **WitOS:** The existing Resource & Capability Model (RFC 0002), Storage & Persistent State Model (RFC 0008), and separate managed device and filesystem services.

The goal is **not to copy Windows**, but to expose a coherent resource model on top of WitOS's already selected architecture.

## 2. Terms and Entity Boundaries

| Entity | Meaning | Example | Does **not** imply |
|---|---|---|---|
| **Physical device** | Physical or virtual hardware | NVMe SSD, USB controller, camera, GPU | A directory or file stream |
| **Block device** | Resource exposing a block-storage interface | NVMe namespace, virtual disk, RAID device | A filesystem |
| **Partition** | Bounded range of a block resource | EFI System Partition | A separate user-facing storage volume |
| **Storage pool** | Composition of multiple block resources | RAID/pool | One physical disk |
| **Volume** | Logical volume exposing file or object storage | `System`, `Data`, `Backup` | One particular partition or device |
| **Filesystem** | Implementation of files and directories within a volume | WitFS, ext4, FAT | A model for every OS device |
| **Collection** | Logical view of objects defined by a query or rule | `Recent`, `WitOS Project` | A physical directory or volume |
| **Resource** | Identifiable system entity with capabilities | Volume, document, camera, printer | A universal byte stream |
| **Namespace** | Context in which a name resolves to a resource | Volume alias, PowerShell drive, POSIX mount | One mandatory global directory tree |

**Invariant:** Volumes, devices, and collections may appear side by side in the UI without becoming the same kind of filesystem object.

**Invariant:** One physical SSD may contain multiple volumes; a volume may span multiple devices; some network or virtual volumes do not expose a directly accessible physical drive.

## 3. Public WitOS Model: Three Independent Groups

The top-level UI, provisionally named **Computer**, exposes the following standard categories:

```text
Computer
  Storage
    System                  WitFS, internal
    Data                    WitFS, internal
    USB Backup              exFAT, removable
    Office NAS              network volume
  Devices
    Storage Hardware        NVMe drives, controllers
    Cameras
    Printers
    Displays
    GPU / Compute
    Network Interfaces
  Collections
    Documents
    Recent
    Saved Queries
    Project Views
```

This is **virtual navigation**, not a hierarchy of physical directories. The `Computer` root need not have an actual `/Computer` path or its own filesystem.

User-facing operations:

- **Storage:** List logical volumes and their status, capacity, available space, filesystem type, origin (internal, removable, or network), snapshot and encryption capabilities; browse a selected volume.
- **Devices:** List devices by type, status, driver/provider, and permitted operations; expose an appropriate typed interface.
- **Collections:** Open virtual views of documents. Results may come from multiple volumes, but mutation operations must still have a well-defined target and appropriate authority.

Headless configurations expose the same entities through native APIs and PowerShell commands. **The GUI is an optional presentation, not an architectural dependency.**

## 4. Volume Identity, Display Label, and Alias

Three distinct values are required:

| Field | Purpose | Example |
|---|---|---|
| `VolumeId` | Stable logical volume identity, where supported by the format/provider | UUID |
| `DisplayLabel` | Human-readable name; mutable and not necessarily unique | `USB Backup` |
| `Alias` | Convenient unique name within a user/session namespace | `Data`, `System`, `Work` |

The native API should use a `VolumeId` or a granted **capability**. Alias resolution is appropriate for interactive use but must not replace persistent identity in long-lived references.

**Proposed naming rules:**

1. Names such as `System`, `Data`, `Work`, and `Backup` are preferable to `C`, `D`, and `E`. Drive letters are neither required nor privileged.
2. `DisplayLabel` is not a unique identifier: several volumes may be labeled `Backup`.
3. Conflicting aliases must be resolved explicitly, by user rename or deterministic temporary alias. A previously known alias must never silently resolve to unrelated content.
4. A disconnected volume may remain in the catalog with status `Unavailable`; reconnection must match its identity, not merely its label or port.
5. Copying or cloning a volume can duplicate an ID. Import must detect this collision and apply a documented policy rather than assume equal UUIDs represent one currently attached instance.
6. Knowing an alias or `VolumeId` **does not grant access**.
7. For formats without a durable on-disk volume ID, the catalog must communicate the weaker identity guarantee instead of inventing one.

**Not specified yet:** Exact Unicode alias grammar, case-sensitivity rules outside individual providers, and the persistent volume-registry format. These belong to subsequent API/design reviews.

## 5. Native Addressing and Compatibility Paths Need Not Match

### 5.1. Native Addressing

Prefer a **`VolumeId + RelativePath`** pair, or a volume capability and a relative path, over a global absolute path string.

```csharp
// Illustrative API names. These are not implemented in the WitOS SDK.
var volume = await WitStorage.Volumes.OpenAsync("Data");
await using var stream =
    await volume.OpenReadAsync("Documents/report.pdf");
```

For durable references, an application should persist a volume/resource identity or a persistent grant. An alias is a human-friendly input mechanism.

A readable form such as `Data:/Documents/report.pdf` may be used by WitOS UI, URI-like references, or diagnostics, but **standard `System.IO` support for that syntax is not promised**.

### 5.2. Standard .NET / System.IO

WitOS has selected the upstream Unix-form .NET runtime (`TARGET_UNIX`, `TargetOS=witos`). Standard `System.IO.Path` therefore accepts Unix paths such as `/data/Documents/report.pdf`.

```csharp
using var stream = File.OpenRead("/data/Documents/report.pdf");
```

To use a volume discovered through the native API with an ordinary `FileStream`, the SDK **may** expose an adapter returning a compatibility path accessible to the current process:

```csharp
// Concept only; the method name and contract require design review.
string? ioPath = volume.TryGetSystemIoPath("Documents/report.pdf");
if (ioPath is null)
    throw new NotSupportedException("No System.IO path projection");
using var stream = File.OpenRead(ioPath);
```

**Adapter properties:**

- `ioPath` is a string in the caller's current namespace, not a stable ID or an independent capability.
- Not every resource needs a `System.IO` projection. For example, access to one document may be granted without permission to enumerate its parent directory.
- A returned path can become stale after mount-policy changes, disconnection, or revocation.
- `File.OpenRead("Data:/...")` **must not** be advertised as a supported standard call without explicit verification and a corresponding change to the .NET platform contract.

**Decision:** Do not fork CoreLib, impersonate Windows, or globally redefine `Path` semantics merely to achieve the preferred volume presentation.

### 5.3. POSIX-Compatible Namespace

The existing `/boot`, `/data`, and `/fat` tree remains a working projection for libc, `System.Native`, portable .NET, utilities, and compatible applications. It does not define the inventory of physical devices and must not become the primary interactive navigation model.

In the future, each process may receive a constrained projection of authorized resources, as RFC 0008 already permits. Mounting must not bypass capability enforcement.

## 6. PowerShell as the First Native Shell for the Resource Model

PowerShell has already been selected as the default WitOS command shell (RFC 0020) and supports its own **PSDrives** and **providers**.

Proposed user experience:

```powershell
PS> Get-PSDrive
PS> Set-Location Data:
PS Data:\> Get-ChildItem
PS> Set-Location Work:
PS Work:\> Get-ChildItem Projects
PS> Set-Location Devices:
PS Devices:\> Get-ChildItem Cameras
```

Implementation in stages:

1. **File volumes:** Ordinary PowerShell `FileSystem` PSDrives mapped to the existing POSIX projections (`Data` → `/data`, for example). Verify on an actual WitOS guest rather than assume theoretical compatibility.
2. **Volume properties:** A new WitOS module returning typed `VolumeDescriptor` instances/handles instead of only path strings.
3. **Devices:** A dedicated provider/cmdlets exposing devices and device-specific operations, not artificial files.
4. **Semantic collections:** A separate provider/cmdlet set built on future WitFS query APIs and system-wide indexing.

`Data:` and `Devices:` are **PowerShell namespaces**, not necessarily absolute `System.IO` paths. Documentation and diagnostics must make this distinction clear.

## 7. Developer Experience: Two APIs, Not Two Operating Systems

### 7.1. Portable .NET

Ordinary applications use `System.IO`, `DirectoryInfo`, `FileStream`, and `DriveInfo` without depending on the WitOS SDK. Paths come from configuration, a file picker, the environment, or the caller. No Win32 or Linux P/Invoke compatibility is promised.

### 7.2. Native WitOS SDK

A native WitOS application must be able to:

- Enumerate **accessible** volumes with labels, IDs, types, properties, status, and permitted operations.
- Open a volume using a granted capability or an authorized alias.
- Open a file using a path relative to the selected volume.
- Subscribe to volume attachment, detachment, and property changes.
- Enumerate **authorized** devices by type (`ICamera`, `IPrinter`, `IComputeDevice`, and so on).
- Request operations on a particular device through typed interfaces.
- Work with virtual collections without treating them as physical directories.

Illustrative contracts, **not implemented today**:

```csharp
public readonly record struct VolumeId(Guid Value);
public readonly record struct VolumePath(
    VolumeId Volume,
    string RelativePath);

public interface IVolumeCatalog
{
    IAsyncEnumerable<VolumeDescriptor> ListAccessibleAsync(
        CancellationToken cancellationToken = default);

    ValueTask<IVolumeAccess> OpenAsync(
        VolumeId id,
        CancellationToken cancellationToken = default);

    ValueTask<VolumeId?> ResolveAliasAsync(
        string alias,
        CancellationToken cancellationToken = default);
}

public interface IVolumeAccess : IAsyncDisposable
{
    VolumeId Id { get; }
    ValueTask<Stream> OpenReadAsync(
        string relativePath,
        CancellationToken cancellationToken = default);
}

public interface IDeviceCatalog
{
    IAsyncEnumerable<DeviceDescriptor> ListAccessibleAsync(
        CancellationToken cancellationToken = default);
}
```

`VolumeDescriptor`, `DeviceDescriptor`, and the capability types remain deliberately unspecified. This ADR fixes their **semantics**, not a premature binary API contract.

### 7.3. Security Requirements

- `VolumeId`, `ResourceId`, device names, and aliases are **identifiers**, not authorities.
- Resource discovery/enumeration and content access require distinct permissions.
- A typed handle must be granted through capability policy and must support revocation.
- A POSIX compatibility path must not bypass authorization checks.
- Device drivers continue to expose narrow capabilities. Opening a camera must not grant direct MMIO access or access to its entire USB controller.
- A cross-volume collection returns only authorized objects. Combining results does not create a new global permission.

## 8. Relationship to WitFS

This ADR applies to **WitOS as a whole**. It does not define the WitFS implementation.

Each WitFS volume has its own CoW engine, transaction roots, and snapshots. It may provide native attributes, relationships, query subscriptions, and saved-query collections. However, **WitFS must not own the catalog of all physical devices** and must not be required to serve as the system filesystem.

A system-wide collection may combine documents from multiple volumes, including FAT/ext4 and network sources when indexing adapters are available. This is a federated query, **not** one atomic transaction. Unless a separate cross-volume protocol is designed, the unified-commit guarantee applies only within an individual WitFS volume.

## 9. Existing WitOS Implementation and Scope of Change

Components examined when preparing this decision:

| Component | Current responsibility | Required treatment |
|---|---|---|
| `IBlockStorage` | Block-storage abstraction | **Keep**; do not couple it to drive letters |
| `IFileSystem` | Filesystem contract | **Keep**; `VolumeCatalog` is not a replacement for a filesystem |
| `src/Services/Namespace/Server/MountTable.cs` | POSIX mount point → `IFileSystem` | Treat as a **projection**, not the primary volume catalog |
| `src/Services/Namespace/Volumes/VolumeMounter.cs` | Provisional policy mapping known GPT partitions to `/boot`, `/data`, `/fat` | Later separate volume discovery, alias assignment, capability issuance, and POSIX mount configuration |
| `RFC 0002` | Resources and capabilities | **Use as the foundation** |
| `RFC 0008` | Persistent resources and namespaces | **Clarify public resource presentation** |
| `ADR 0024`, `RFC 0015` | Unix-form upstream .NET | **Do not change** |
| `RFC 0020` | PowerShell, terminal, and console | Add **volumes/devices as first-class providers** |

**Immediate architectural boundary:** `VolumeCatalog` knows **which** logical volumes exist; `DeviceCatalog` knows **which** devices exist; `NamespaceResolver` knows **where** a resource is projected and which rights are available in a process/session. They must not be collapsed into one list of `/data`, `/boot`, and `/fat` paths.

Existing components continue to boot and serve their current routes. Migration must be incremental and must not break CoreCLR, PowerShell, or ext4.

## 10. Implementation Phases

### P0 — Fix the Contract and Terminology

- Accept this ADR with corresponding clarifications in RFC 0008 and RFC 0020.
- Define `VolumeId`, origin, display label, alias, and collision policy.
- Separate discovery, enumeration, open, alias rename, mount, and capability-grant operations.
- Specify the mapping between volume identity, provider, and process namespace.

### P1 — Volume Catalog on Top of the Existing Namespace Service

- Maintain a registry of all discovered logical volumes, even without user-visible mount points.
- Provide `ListAccessible`, `ResolveAlias`, status, and notification APIs.
- Map existing `/data`, `/fat`, and `/boot` to `Data`, `Fat`, and `Boot`, without relocating or reformatting files.
- Test alias/label collisions, hot-unplug/replug, duplicate IDs, read-only volumes, and insufficient privileges.

### P2 — PowerShell and Developer Experience

- Test the standard PowerShell FileSystem provider with `New-PSDrive -PSProvider FileSystem -Root /data -Name Data` inside a WitOS guest.
- Provide native commands such as `Get-WitVolume` and `Open-WitVolume` (provisional names), returning typed objects.
- Provide native volume-handle file access in the SDK; validate the `System.IO` bridge separately.
- Document that `Data:` is a shell namespace, not a new `System.IO.Path` syntax.

### P3 — Device Catalog

- Provide typed capability acquisition, device discovery, hotplug notifications, and permissions.
- Expose `Devices:` through a PowerShell provider/cmdlets; the file manager shows the same descriptors.
- Test headless and graphical configurations.

### P4 — Collections and Federated Queries

- Build on the WitFS query API and indexing adapters for other volumes.
- Support saved searches and collections that are not physical directories.
- Enforce capabilities/ACLs across federated results without promising cross-volume atomicity.

## 11. Acceptance Criteria

1. The file manager shows independent volumes, devices, and collections. A user need not understand `/mnt`, `/dev`, or `/data` to find storage.
2. A native WitOS developer can enumerate accessible volumes, obtain descriptors, and open a file **without knowing its mount point**.
3. Portable .NET applications continue using ordinary Unix-form `System.IO` paths unchanged.
4. PowerShell supports named PSDrives; `Data:` semantics are not confused with `File.OpenRead`.
5. Physical disks and logical volumes remain distinct: one disk may contain multiple volumes, and one volume may span multiple disks.
6. Changing `DisplayLabel` or an alias does not alter `VolumeId` or volume contents.
7. Two USB volumes with the same label never silently redirect an alias to the wrong media.
8. Disconnect/reconnect is handled explicitly, without leaking stale handles or falsely matching by label.
9. No path, ID, or alias bypasses capability policy, including filesystem projections and federated queries.
10. Drivers and filesystems remain separate services; the kernel gains no user-space namespace policy.
11. Headless WitOS manages the same resource catalogs through PowerShell and APIs.
12. WitFS can still be formatted, mounted, and read without the Shell; its disk format does not depend on the resource-first GUI.

## 12. Rejected Alternatives and Non-Goals

| Alternative | Decision | Reason |
|---|---|---|
| Expose everything under `/` with `/dev`, `/mnt`, and `/proc` | **Not the primary UX** | Conflates resource categories and exposes OS implementation details |
| Introduce full Windows/Win32 namespace and drive-letter semantics into CoreCLR | **Do not implement** | Conflicts with ADR 0024 and the goal of portable upstream .NET |
| Require `C:` and `D:` as the only volume names | **Reject** | Uninformative, limited, and unstable |
| Eliminate the POSIX mount tree entirely | **Do not do this** | Currently required by libc, CoreCLR, `System.IO`, and compatible tools |
| Force every device to appear as a file | **Not required** | Devices have distinct capabilities and operation semantics |
| Make WitFS the owner of all device inventories | **Do not do this** | Filesystems must not manage hardware/resource discovery |
| Require distributed transactions for virtual collections | **Do not promise** | Current transactional guarantees are scoped to a provider/volume |

## 13. Questions for Subsequent ADRs

- Exact namespace and persistence format for user aliases; multi-user and multi-session handling.
- Authorization for volume/device catalogs before the full account system is available.
- The precise `VolumeId` contract for FAT, ext4, network volumes, and cloned images.
- User-facing separators and URI syntax for OS commands and GUI (without modifying `System.IO`).
- Whether standard `DriveInfo` needs a complete volume inventory or whether the mounted-path projection is sufficient.
- Persistent grants for references to files moved between volumes.
- Disconnection semantics for active file handles and PowerShell PSDrives.

These questions do not block the agreed concept: independent resources, named volumes, typed devices, and preservation of the standard .NET contract.

---

## 14. Sources and Related Decisions

Relevant WitOS documents and sources:

1. [WitOS RFC 0002 — Resource & Capability Model](https://github.com/dmitrat/WitOS/blob/main/%40Docs/RFC-0002-Resource-and-Capability-Model.md).
2. [WitOS RFC 0008 — Storage & Persistent State Model](https://github.com/dmitrat/WitOS/blob/main/%40Docs/RFC-0008-Storage-and-Persistent-State-Model.md), especially §§ 6–17 and 76–85.
3. [WitOS ADR 0024 — Three Layers and Unix-form Runtime](https://github.com/dmitrat/WitOS/blob/main/%40Docs/Implementation/ADR-0024-Three-Layers-and-Unix-Form-Runtime.md).
4. [WitOS RFC 0015 — .NET Runtime Port & Compatibility Contract](https://github.com/dmitrat/WitOS/blob/main/%40Docs/RFC-0015-DotNet-Runtime-Port-and-Compatibility-Contract.md).
5. [WitOS RFC 0020 — Terminal, Console and PowerShell](https://github.com/dmitrat/WitOS/blob/main/%40Docs/WitOS-RFC0020-Terminal-Console-and-PowerShell-Architecture.md).
6. [WitOS Namespace MountTable](https://github.com/dmitrat/WitOS/blob/main/src/Services/Namespace/Server/MountTable.cs).
7. [WitOS VolumeMounter](https://github.com/dmitrat/WitOS/blob/main/src/Services/Namespace/Volumes/VolumeMounter.cs).
8. [WitOS D5.1 — Namespace Service](https://github.com/dmitrat/WitOS/blob/main/%40Docs/Implementation/D5.1-Namespace-Service.md).
9. [.NET `Path.Unix.cs`](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/IO/Path.Unix.cs) and [.NET `PathInternal.Unix.cs`](https://github.com/dotnet/runtime/blob/main/src/libraries/Common/src/System/IO/PathInternal.Unix.cs).
10. [WitFS — Architectural Research & Concept](WitFS-Architectural-Research-and-Concept-v0.2-EN.md), §19 (companion document).

**Evidence status:** Descriptions of current interfaces and the adopted Unix-form runtime are grounded in reviewed documents/source files. Volume/device catalogs, new APIs, PowerShell drives, and GUI features are **agreed design requirements**, not verified implemented capabilities.
