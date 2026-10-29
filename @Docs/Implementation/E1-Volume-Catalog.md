# E1 — The volume catalog

Plan step E1 (phase E, goal Ц6) implements decisions 1–5 of
[ADR 0032](ADR-0032-Resource-First-Volumes.md) for the volumes of disks: the namespace service keeps a catalog of the
volumes it mounts, each with an identity, a label and an alias; every volume a format WitOS reads holds is mounted, the
system's at their places and every other one at `/media/<alias>`; the protocol lists the catalog and a volume's space;
the libc answers `statfs`, `fstatfs` and `/etc/mtab` from it; PowerShell's profile gives each volume a drive. E0 found
that nothing brings a file in; E2 (a network share) and E3 (a USB drive) add volumes through this catalog.

## 1. A volume in the catalog

`VolumeCatalog` (`src/Services/Namespace/Volumes`) holds the attached volumes in the order they came, over the mount
table, whose points it fills:

- **Identity.** An ext4 volume's superblock UUID (`FileSystemVolume.Identity`, lower-case 8-4-4-4-12); for a FAT or
  exFAT volume of a GUID partition, the partition's unique GUID, which is stronger than the volume's serial; for any
  other FAT or exFAT volume its 32-bit serial as `XXXX-XXXX`, flagged weak, since a copied image shares it; none for the
  volume in the service's memory. A volume whose identity an attached volume already has is mounted all the same and
  flagged as a collision (`VOLUME_COLLISION`), and its mount line says so.
- **Label.** The format's own: ext4's volume name, FAT's label, `tmp` for `/tmp`.
- **Alias.** `Boot`, `Data`, `Fat` and `Tmp` for the system's volumes, which mount at `/boot`, `/data`, `/fat` and
  `/tmp`; for every other volume its label's stem (ASCII letters, digits, `-` and `_` kept, every other character a `-`,
  the dashes at the ends dropped, at most 31 characters, `Volume` when nothing is left), then `-2`, `-3`… until it is
  free among the attached volumes and the five reserved names (the four and `Media`) whatever the case, since
  PowerShell's drive names ignore it. A volume whose identity the catalog saw earlier in the boot takes that alias again
  when it is free. Such a volume mounts at `/media/<alias>`.
- **Origin.** `ORIGIN_INTERNAL` for a disk's volume, `ORIGIN_MEMORY` for `/tmp`; `ORIGIN_REMOVABLE` and
  `ORIGIN_NETWORK` are defined for E3 and E2. `ORIGIN_SYSTEM` names the package, which the libc lists itself.

`/media` is a directory the service mounts at its start (`MediaDirectory`): its listing is the aliases mounted below it
at the moment, each a directory, and it refuses every change with `EROFS`, so no program makes a name there that a volume
could later shadow. The libc learns the mount points once, when it connects (D5.1); `/media` is one of them, so a volume
that arrives later is reached by every process through the service's longest-prefix resolution
(`MountTable.Resolve`), without a change to its libc.

## 2. Which volumes mount

`VolumeMounter` mounts every volume of every disk the device manager gives the service:

- A GUID disk (D3.1): its EFI system partition at `/boot`, read-only whatever the disk (H3.3); the partitions named
  "WitOS ext4" and "WitOS FAT" at `/data` and `/fat`; every other partition holding ext4 or FAT below `/media`.
- A disk with an MBR (`MbrPartitionTable`, `OutWit.OS.Storage`): the four primary entries of block 0 and the logical
  partitions an extended entry (types 0x05, 0x0F, 0x85) chains through its extended boot records, as Linux's msdos
  parser reads them, at most 64. Block 0 is no MBR table when it is a protective MBR (an entry of type 0xEE, which the
  GPT reader is asked first anyway), a FAT or exFAT boot sector (a whole-disk volume, which also ends in 0x55AA), or an
  entry's active flag is neither 0x00 nor 0x80; a partition outside the disk, two that overlap and a chain that loops are
  refused. `WriteAsync` writes primary partitions, for the host's tools and tests. Each partition holding ext4 or FAT
  mounts below `/media`, as a USB drive Windows formats does.
- A disk with no table that holds FAT: `/boot` while it is free, `/media` otherwise.

A volume that lies below `/media` and that its provider refuses (a feature the ext4 provider does not serve, a damaged
FAT) is reported (`not mounted: <reason>`) and left out; a system volume that fails still fails the service, as before.
A space with no volume of a format the service reads is reported as such. The mount lines keep their form, with the
point, the format, the label, the size, the source and the cache.

## 3. The protocol

Two requests of `witos/namespace.h` (version 1 still; `NamespaceMessages` on the managed side):

- `WIT_NAMESPACE_VOLUMES` (25): a `WitNamespaceVolume` for each attached volume into the window, in the catalog's order;
  with `WIT_NAMESPACE_SPACE` in the flags, each with its size and free space, which a FAT volume counts through its whole
  allocation table (exFAT's bitmap), as Linux's vfat does.
- `WIT_NAMESPACE_STATFS` (26): the record of the volume a handle of the session or a path (Handle 0, the path in the
  window) lies on, with its space; `ENOENT` for a path that is not there, as Linux's `statfs` answers. A mount the
  catalog does not know (`/media` itself) answers a record of its own, read-only.

`WitNamespaceVolume` is 256 bytes: `Version`, `Size`, `Flags` (`READ_ONLY`, `WEAK_IDENTITY`, `COLLISION`, `SPACE`),
`Origin`, `TotalBytes`, `FreeBytes`, `AllocationUnit`, `Index` (the mount's number, unique within the boot), then
NUL-terminated UTF-8 fields: the alias (32 bytes), the point (64), the format (16), the identity (40) and the label (64).
A host test fixes the layout against the header.

`IFileSystem.GetSpaceAsync` gives the space: ext4 from the superblock's block counts, FAT from its clusters, the memory
volume from its capacity and use.

## 4. The libc

- `statfs` and `fstatfs` (`__wit_statfs`, `files.c`), which musl's `statvfs` and `fstatvfs` call: a path or descriptor
  of a volume asks `STATFS` and answers Linux's magic number for its format (ext2, ext3 and ext4 0xEF53, FAT 0x4D44,
  exFAT 0x2011BAB0, `/tmp` 0x01021994), the allocation unit as the block size, the blocks, the free blocks as free and
  available, `ST_RDONLY` for a read-only volume and 16 plus the volume's index as `f_fsid`; `/dev/shm` and its files are
  a tmpfs with no blocks (device 3); every other path, the package's, is a read-only ROMFS (0x7275) whose blocks are the
  package's pages, all used (device 2). A negative descriptor is `EBADF`.
- `/etc/mtab` (`__wit_mounts_text`, `namespace.c`) is read from `VOLUMES` at each open, so a volume that came later is
  there: `package / witos-package ro 0 0`, then a line per volume — its alias as the device, its point, its format as
  Linux names its type (`ext4`, `vfat`, `exfat`, `tmpfs`), `ro` or `rw`. Before E1 the libc listed the mount points it
  learned at the connection, as `witos-volume`.

## 5. .NET and PowerShell

Nothing of .NET changes (ADR 0032 decision 6). `DriveInfo.GetDrives()` and `Environment.GetLogicalDrives()` list
`/etc/mtab`'s points as before; `DriveInfo.TotalSize`, `TotalFreeSpace` and `AvailableFreeSpace` now report the
volume's space through `statvfs`, and `DriveFormat` and `DriveType` follow the magic number as on Linux.

The profile every interactive PowerShell session runs (`build/console/profile.ps1`) reads `/etc/mtab` before its
banner and gives each volume a `FileSystem` drive named by its alias over its point: `Data:`, `Boot:`, `Fat:`, `Tmp:`
and a drive's, such as `TRANSFER:`. A drive is PowerShell's; `System.IO` keeps the paths. The banner's volume field
names the aliases.

## 6. The gate

`runtime-volumes` (`RuntimeVolumes`, after `runtime-terminal` in `runtime-witos`) boots the device services and the
.NET install with a third disk (`BootRequest.ExtraDisk`): a 64 MiB image with an MBR whose one partition, of type 0x0C
from block 2048 as Windows aligns it, holds a FAT32 volume labelled `TRANSFER` in clusters of 512 bytes, as Windows
formats that size, which the host formats with the same provider (`FatFileSystem.FormatAsync` takes the format by name) and fills with a text file and `tests/Runtime.Witos/Volumes`, an ordinary program the stock SDK publishes
without a RID. The runner hangs the disk as a virtio block device in the slot whose INTx line is the entropy source's,
the one a keyboard or a network adapter takes otherwise (the virt boards have four lines), so the request refuses a
keyboard, an adapter, the pc board and `raspi4b` beside it. `/bin/init` runs the program from the drive,
`/dotnet/dotnet /media/TRANSFER/volumes/Volumes.dll`, whose ten checks are: `/media` lists the drive; the program runs
from it; `DriveInfo.GetDrives()` names `/media/TRANSFER` and `/data`; the drive's and `/data`'s sizes and free space;
its `/etc/mtab` line says `vfat rw`; its text reads back; a copy to `/data`; a 100,000-byte file written to a new
directory on the drive lowers its free space by at least as much; and `/media` refuses a directory. The scenario
requires the mount line, the partition's type, five volumes mounted, `10 of 10 checks passed` and the exit with 0, and
after the boot the host reads the program's file through the same partition table and provider and requires its bytes.

`runtime-powershell` types `'drive=' + (Get-PSDrive Data).Root` at the interactive prompt and requires `drive=/data`;
`runtime-devices`' boots keep their mount lines and their four volumes, and its `/bin/init` (`devices_init.c`) checks
`statfs` of `/data`, `/fat`, `/tmp` and the package (magic number, blocks, free blocks, `ST_RDONLY`), `fstatfs` of a file
of `/data` against `statfs` of its volume, `statvfs`'s `ST_RDONLY` on `/boot`, `ENOENT` for a missing path, and the
alias, type and access of the mtab lines of `/data` and `/boot`. The host tests cover the MBR reader and
writer (primary, logical, protective, boot sector, overlaps, loops), the aliases, the record's layout and each
provider's space and identity.

## 7. What ran

On 2026-10-28, in the Linux container of `build/linux/Dockerfile`; CI's `linux-host` legs run every scenario of the table
again in their `runtime-witos`:

| ISA | `runtime-services` (`runtime-devices`' boots) | `runtime-volumes` | `runtime-powershell` | `runtime-compatibility` | `test` |
| --- | --- | --- | --- | --- | --- |
| x64 | nine passed; `/bin/init` 124 checks, 0 failed | passed in 30 s, 10 of 10 | passed, `drive=/data` | 125 checks, 2 recorded differences, 0 unexpected | 36 scenarios passed |
| ARM64 | eight passed; `/bin/init` 124 checks, 0 failed | passed in 34 s, 10 of 10 | passed, `drive=/data` | 125 checks, 2 recorded differences, 0 unexpected | 32 scenarios passed |
| riscv64 | nine passed; `/bin/init` 124–127 checks, 0 failed | passed in 39 s, 10 of 10 | passed, `drive=/data` | see below | 29 scenarios passed |

The drive's mount line on x64:

```text
[NAMESPACE] mounted /media/TRANSFER: FAT32 volume "TRANSFER" of 66060288 bytes, writable, from pci/00:07.0/1af4:1001, partition 1 of type 0x0C, through a cache of 4 MiB in blocks of 4096 bytes
```

The first run formatted the drive as the library chose for its size, FAT16 in a partition of type 0x0C, which passed
the same ten checks; the scenario now formats FAT32, as the partition's type says and Windows formats such a drive.

The container's riscv64 runtime was built on 2026-10-21, before W4.2 gave `System.Net.NetworkInformation` its Unix
flavor, so its compatibility suite failed `Network.Interfaces` and `Network.AddressChange` with the whole assembly's
`PlatformNotSupportedException` and passed every other check; nothing of E1 is in that path. CI's riscv64 leg builds
the runtime anew and judges the suite there.

## 8. Limits kept explicit

- No notification of a volume's arrival or departure yet: the disks are the device manager's, given before the service
  answers ready, and none leaves. `VolumeCatalog.Detach` exists for E2 and E3, whose volumes come and go; their events
  come with them (ADR 0032, Consequences).
- An alias lasts the boot: a volume that comes back after a reboot takes its label's stem again, which is the same alias
  unless another volume took it first.
- `statfs` reports the free blocks as available: ext4's blocks reserved for the super-user are not subtracted, since
  every process runs as root (N2.2). It reports no inode counts (`f_files`, `f_ffree` are 0), as for a FAT volume on
  Linux.
- A FAT volume's free space is counted through its whole allocation table at each `STATFS` or `VOLUMES` with
  `SPACE`, as Linux's vfat does at mount; a drive of tens of gigabytes reads its table through the cache once.
- The catalog is the namespace service's; a catalog service of its own and a native volume API come with the device
  catalog (ADR 0032 decisions 3 and 6).
