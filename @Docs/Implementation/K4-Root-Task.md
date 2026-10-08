# K4 — The root task and the package (boot ABI v6)

Plan step K4 gives the kernel its first component of the new direction ([RFC 0011 v3 §7.11](../RFC-0011-Kernel-Architecture-and-ABI.md)):
the root task, started from a flat image the boot disk carries, with a startup descriptor and three initial
capabilities and nothing else. The boot package stops being something the kernel parses for a component: it is a
read-only memory object the root task maps. The PE loader stays only for the frozen Windows-form scenarios of the
self-test kernel; the release kernel creates no component from a PE.

## What changed

**Boot ABI v6: the root task extents.** `WitBootInfo` (160 bytes) carries the extents and size of the root task
image the UEFI loader read from `\WITROOT.BIN` beside the package, zero when the file is absent; the kernel maps
them like the package. Everything before them is v5 unchanged.

**The flat image.** `witos/flat.h`: a 64-byte header (magic `WITFLAT1`, version 1, the entry, the segment count)
followed by four 32-byte segment records and the page-aligned bytes. A segment is an address in the component's
image window (`WIT_USER_IMAGE_BASE` to `WIT_USER_LIMIT`), a file range, a memory size and one of three
protections. No names, no imports, no relocations, no sections the kernel must understand. `wit_flat_validate`
checks the whole image before a page is mapped: magic, version, header size, count, every segment's alignment,
protection, file range, window and disjointness, the entry inside an executable segment, and that the unused
records and reserved words are zero.

**The root task is created like any component, from the flat image.** `wit_user_create_flat` reserves the
component's kernel-provided pages (the startup block, the data pages, the first stack and TLS) as for every
component, maps each segment into fresh owned pages, copies its bytes, zero-fills the rest, sets the protection
and publishes executable pages; the first thread starts at the entry with the startup descriptor's address in
its argument register. `WitRootStartup` (`witos/root.h`, 128 bytes at `WIT_USER_INFO`) carries the version, the
ABI-1 version and feature mask, the dynamic data and code arenas, the package size and a counted table of
handles: the kernel log (a console handle with `WRITE`), the boot package (a memory object of kind `PACKAGE`
with `MAP`, `QUERY`, `DUPLICATE`, `TRANSFER`) and the device table (kind `TABLE` with `ACQUIRE`). K6 appends the
UTC capability, which is why the table is counted.

**The package is a memory object.** Memory objects gained extents: an adopted object of kind `PACKAGE` maps the
boot extents (up to 128 of a MiB each) without a page array; `MEMORY_OBJECT_MAP` of a window of it is the only
way a component reads the package. The kernel's own parse of the package (`storage.c`, the file namespace) stays
for the frozen line until K8 and is not used for the root task.

**The release kernel starts it.** After initialization and the hello marker, a kernel without the self-test layer
runs the root task to its exit and finishes with the test exit device; a root task that faults or ends the
component is reported and the boot fails. The self-test kernel runs the same root task image embedded in it as
one more scenario (`Root.Started`), then the validation of broken images (`Root.ImageValidation`).

## Kernel

- `flat.c`: validation. `root_task.c`: the startup descriptor and the initial capabilities, the release path.
- `user.c`: `wit_user_create_flat`; `user_memory_object.c`, `user_space.c`: objects over extents.
- `virtual.c`: the root task image mapped beside the package; `Boot.Uefi/storage.c`: the second file read into
  extents, optional.
- `tools/WitOS.Dev/Images/FlatImage.cs`: a fixed native PE's sections become the flat image's segments; the
  image is embedded for the self-test and written to the boot disk as `WITROOT.BIN`.

## Fixtures

`tests/User.X64/root.asm` and `tests/User.A64/root.asm`, linked at the image window and converted to the flat
format: they check the descriptor (version, size, ABI version, the feature mask, the arenas, the package size, the
handle count), write `[ROOT] started` through the log handle, map the package's first page and check `WITPAK01`
(a writable view is refused), map the device table and check its version and that the board published devices,
write `[ROOT] package and devices` and exit with zero, as a root task does. `tests/Kernel/root_task_tests.c` starts the
embedded image through the same creation path and then feeds the validation broken copies: a wrong magic, a
foreign version, an empty or overlong segment table, a segment over the kernel's pages or at the limit, a file
range beyond the image, a file size beyond the memory size, a writable executable segment, an entry outside the
image; each is refused whole and the restored image is accepted again.

## Limits kept explicit

- One root task, one slot, the test profile's quotas; processes beyond it are K5.2.
- The flat image is a boot format for the root task, not an application format: phase S brings ELF and `ld.so`.
- The root task's first thread gets the startup descriptor in the Microsoft x64 register today; the SysV
  convention and the `SYSCALL` transport come with K5.2 on x64, ARM64 is unchanged.
- The package object is read-only and whole; the root task reads what it needs through windows of it.
- The PE loader, the file namespace over the package and the kernel-held process state remain in the tree for
  the frozen line's scenarios until K8 and are unreachable from the release kernel's component path.
