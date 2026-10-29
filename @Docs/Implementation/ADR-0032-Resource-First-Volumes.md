# ADR 0032: Named volumes and their projection — the resource-first model, first part

**Status:** Accepted 2026-10-28: the concept the author agreed on 2026-10-23
([WitOS-ADR-Resource-First-Public-Model-EN.md](../WitOS-ADR-Resource-First-Public-Model-EN.md)), numbered with plan step
E1 by the author's plan revision of 2026-10-28.
**Date:** 2026-10-28.
**Scope:** How WitOS names its volumes, which identity it keeps for each, and where the POSIX tree that the libc and
upstream .NET see places them; the catalog the namespace service keeps until a catalog service of its own exists. Devices
(the draft's P3) and collections (P4) are not decided here.

## Context

The draft ADR of 2026-10-23 holds that a person and a developer see independent resources — volumes, devices, network
storage, collections — and that the single POSIX tree is a projection for compatibility, not the primary model. WitOS
mounts three volumes by a fixed policy today: the partition named "WitOS ext4" at `/data`, "WitOS FAT" at `/fat`, the
boot volume at `/boot`, and `/tmp` in the service's memory. Phase E needs more: a USB drive plugged in (E3) and a
Windows share mounted (E2) must appear with a name a person recognises, and E0 found that nothing else brings a file in.

The libc learns the mount points once, when it connects to the namespace service, and routes every path below one of
them to the service (D5.1).

## Decision

1. **A volume has an identity, a label and an alias.**
   - **Identity**, from its format: the ext4 superblock's UUID; a GUID partition's unique GUID where the partition table
     gives one; a FAT or exFAT volume's 32-bit serial number, a weaker identity the catalog reports as such; none for a
     volume of the service's memory. Two attached volumes of one identity are a collision the catalog reports, never one
     volume.
   - **Label**, the volume's own name if its format records one; mutable, not unique.
   - **Alias**, unique among the attached volumes whatever the case (PowerShell's drive names ignore it): `Boot`, `Data`,
     `Fat` and `Tmp` for the system's volumes, which with `Media` no other volume takes; for any other, its label with
     every character outside ASCII letters, digits, `-` and `_` turned into `-`, at most 31 characters, or `Volume`
     without a label, and `-2`, `-3`… on a collision. Within a boot a volume that comes back takes its alias again if it
     is free; an alias is never given to another volume while the catalog knows its volume.
2. **The POSIX projection.** The system's volumes keep their mount points. Every other volume — a further partition, a
   removable drive, a network share — mounts at `/media/<alias>`. `/media` is a directory the namespace service serves
   from its start, listing the aliases, so a volume that arrives later is reached by every process without a change to
   its libc. No name of the boot package can be shadowed by a volume.
3. **The catalog lives in the namespace service** (the draft's P1), which mounts the volumes: a request lists each
   attached volume with its identity and its kind, label, alias, format, origin (internal, removable or network),
   mount point, size, free space and whether it is read-only. A catalog service of its own comes with the device catalog
   (P3).
4. **Space.** A volume reports its size and free space, and the libc answers `statfs` and `statvfs` from it, so that
   `DriveInfo` reports sizes. `/etc/mtab`, which the libc makes, lists the volumes attached when it is opened.
5. **PowerShell.** WitOS's profile gives each volume a file system drive named by its alias (`Data:`, `Boot:`, a USB
   drive's), over its mount point. `Data:` is PowerShell's, never a `System.IO` path.
6. **No change to .NET.** `System.IO` keeps the Unix paths of ADR 0024; a native volume API (the draft's `IVolumeCatalog`)
   is the SDK's, later, over the catalog of decision 3.

## Consequences

- E1 implements decisions 1–5 for the volumes of disks: the partition tables of GPT and MBR (a Windows USB drive's),
  every volume with a format WitOS reads mounted, the catalog, the protocol's requests and the libc's calls; its gate is
  a second disk with an MBR and a FAT volume. E2 and E3 add volumes that come and go.
- Notifications of a volume's arrival and departure come with the first volume that departs (E3).
- **Rejected:** a mount point named after the volume at the root (`/usb`): a label could shadow the boot package's
  directories (`/system`, `/etc`).
- **Rejected:** drive letters or a `Name:` path syntax in `System.IO`: ADR 0024 keeps upstream .NET unchanged, and the
  draft rejects letters as uninformative.
