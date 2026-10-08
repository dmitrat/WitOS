#include "uefi.h"
#include "witos/boot.h"
#include "witos/flat.h"
#include "witos/package.h"
#include "witos/platform.h"

/* UEFI 2.10 LoadedImage / SimpleFileSystem / File protocol prefixes.
 * Firmware interfaces stay here; the handoff contains only owned bytes. */
typedef struct LoadedImagePrefix {
    WitU32 Revision;
    EfiHandle Parent;
    void *System;
    EfiHandle Device;
} LoadedImagePrefix;
typedef struct BootFile BootFile;

struct BootFile {
    WitU64 Revision;
    EfiStatus (*Open)(BootFile *, BootFile **, WitU16 *, WitU64, WitU64);
    EfiStatus (*Close)(BootFile *);
    void *Delete;
    EfiStatus (*Read)(BootFile *, WitU64 *, void *);
};

typedef struct BootVolume {
    WitU64 Revision;
    EfiStatus (*OpenVolume)(struct BootVolume *, BootFile **);
} BootVolume;

_Static_assert(sizeof(LoadedImagePrefix) == 32, "LoadedImage device prefix");
_Static_assert(sizeof(BootFile) == 40, "File read prefix");

/* The boot package and the root task image (plan step K4), each read whole into page-aligned extents of at most a
 * MiB below 4 GiB that the kernel maps; the tables live in this image, where the kernel requires them. */
static WitBootStorageExtent package_extents[WIT_MAX_STORAGE_EXTENTS];
static WitBootStorageExtent root_extents[WIT_MAX_ROOT_EXTENTS];

static int read_exact(BootFile *file, WitU8 *data, WitU64 size)
{
    WitU64 done = 0;
    while (done < size) {
        WitU64 count = size - done;
        if (count > 1024 * 1024) {
            count = 1024 * 1024;
        }
        const WitU64 requested = count;
        if (file->Read(file, &count, data + done) != EFI_SUCCESS || !count || count > requested) {
            return 0;
        }
        done += count;
    }
    return 1;
}

/* One file of the boot volume into extents. The size comes from the first 32 bytes when size_offset is nonzero
 * (the package carries its own) and is validated against the limits; otherwise the file is read until it ends.
 * On failure every page taken is freed and zero is returned; phase tells where. */
static int read_file(EfiBootServicesPrefix *services, BootFile *root, WitU16 *path, const WitU8 *magic,
    WitU64 size_offset, WitU64 max_bytes, WitBootStorageExtent *extents, WitU32 capacity, WitU32 *allocated,
    WitU64 *bytes, WitU32 *phase)
{
    BootFile *file = 0;
    WitU64 size = 0;
    WitU8 header[32], extra = 0;
    *allocated = 0;
    *bytes = 0;
    *phase = 4;
    if (!root->Open || !root->Close || root->Open(root, &file, path, 1, 0) != EFI_SUCCESS || !file) {
        return 0;
    }
    *phase = 5;
    if (file->Revision < 0x10000 || !file->Close || !file->Read || !read_exact(file, header, sizeof(header))) {
        goto failed;
    }
    if (magic) {
        for (WitU32 i = 0; i < 8; ++i) {
            if (header[i] != magic[i]) {
                goto failed;
            }
        }
    }
    if (size_offset) {
        for (WitU32 i = 0; i < 8; ++i) {
            size |= (WitU64)header[size_offset + i] << (i * 8);
        }
        if (size < 32 || size > max_bytes) {
            goto failed;
        }
    } else {
        size = max_bytes; /* Read to the end; the final extent is trimmed to what the file held. */
    }
    WitU64 done = 0;
    while (done < size) {
        WitU64 chunk = size - done;
        if (chunk > 1024 * 1024) {
            chunk = 1024 * 1024;
        }
        const WitU64 pages = (chunk + 4095) / 4096;
        WitU64 physical = 0xFFFFFFFFULL;
        *phase = 7;
        if (*allocated == capacity || services->AllocatePages(1, 2, pages, &physical) != EFI_SUCCESS) {
            goto failed;
        }
        extents[*allocated].Base = physical;
        extents[*allocated].Length = pages * 4096;
        ++*allocated;
        if (!physical || (physical & 4095) || physical > 0x100000000ULL - pages * 4096) {
            goto failed;
        }
        for (WitU64 i = 0; i < pages * 4096; ++i) {
            ((WitU8 *)physical)[i] = 0;
        }
        WitU64 copied = 0;
        if (!done) {
            for (WitU32 i = 0; i < sizeof(header); ++i) {
                ((WitU8 *)physical)[i] = header[i];
            }
            copied = 32;
        }
        *phase = 8;
        if (size_offset) {
            if (!read_exact(file, (WitU8 *)physical + copied, chunk - copied)) {
                goto failed;
            }
            done += chunk;
        } else {
            WitU64 count = chunk - copied;
            if (file->Read(file, &count, (WitU8 *)physical + copied) != EFI_SUCCESS || count > chunk - copied) {
                goto failed;
            }
            done += copied + count;
            if (count < chunk - copied) {
                size = done; /* The file ended inside this extent. */
                extents[*allocated - 1].Length = ((done - (size - copied - count)) + 4095) / 4096 * 4096;
                break;
            }
        }
    }
    *phase = 9;
    WitU64 tail = 1;
    if (file->Read(file, &tail, &extra) != EFI_SUCCESS || tail) {
        goto failed;
    }
    *phase = 10;
    const EfiStatus close = file->Close(file);
    file = 0;
    if (close != EFI_SUCCESS) {
        goto failed;
    }
    *bytes = size;
    return 1;
failed:
    if (file && file->Close) {
        (void)file->Close(file);
    }
    for (WitU32 i = 0; i < *allocated; ++i) {
        (void)services->FreePages(extents[i].Base, extents[i].Length / 4096);
    }
    *allocated = 0;
    return 0;
}

int wit_boot_storage(EfiHandle image, EfiBootServicesPrefix *services, WitBootInfo *boot)
{
    static const EfiGuid loadedGuid = {0x5B1B31A1, 0x9562, 0x11d2, {0x8e, 0x3f, 0, 0xa0, 0xc9, 0x69, 0x72, 0x3b}};
    static const EfiGuid volumeGuid = {0x964e5b22, 0x6459, 0x11d2, {0x8e, 0x39, 0, 0xa0, 0xc9, 0x69, 0x72, 0x3b}};
    static const WitU8 magic[8] = {'W', 'I', 'T', 'P', 'A', 'K', '0', '1'};
    WitU16 package_path[] = {'\\', 'W', 'I', 'T', 'O', 'S', '.', 'P', 'A', 'K', 0};
    WitU16 root_path[] = {'\\', 'W', 'I', 'T', 'R', 'O', 'O', 'T', '.', 'B', 'I', 'N', 0};
    LoadedImagePrefix *loaded = 0;
    BootVolume *volume = 0;
    BootFile *root = 0;
    WitU32 phase = 1, package_count = 0, root_count = 0;
    WitU64 package_bytes = 0, root_bytes = 0;
    if (!services->HandleProtocol || !services->AllocatePages || !services->FreePages) {
        goto failed;
    }
    if (services->HandleProtocol(image, &loadedGuid, (void **)&loaded) != EFI_SUCCESS ||
        !loaded ||
        loaded->Revision < 0x1000 ||
        !loaded->Device) {
        goto failed;
    }
    phase = 2;
    if (services->HandleProtocol(loaded->Device, &volumeGuid, (void **)&volume) != EFI_SUCCESS ||
        !volume ||
        volume->Revision < 0x10000 ||
        !volume->OpenVolume) {
        goto failed;
    }
    phase = 3;
    if (volume->OpenVolume(volume, &root) != EFI_SUCCESS || !root) {
        goto failed;
    }
    if (!read_file(services, root, package_path, magic, 24, WIT_PACKAGE_MAX_BYTES, package_extents,
            WIT_MAX_STORAGE_EXTENTS, &package_count, &package_bytes, &phase)) {
        goto failed;
    }
    /* The root task image is optional: a volume without it boots the kernel alone. */
    {
        WitU32 root_phase = 0;
        if (!read_file(services, root, root_path, 0, 0, WIT_FLAT_MAX_BYTES, root_extents, WIT_MAX_ROOT_EXTENTS,
                &root_count, &root_bytes, &root_phase)) {
            if (root_phase != 4) {
                phase = 20 + root_phase; /* Present but unreadable: a real failure. */
                goto failed;
            }
            wit_console_write("[BOOT] No root task image\n");
            root_count = 0;
            root_bytes = 0;
        } else {
            wit_console_write("[BOOT] Root task image read\n");
        }
    }
    phase = 11;
    const EfiStatus close = root->Close(root);
    root = 0;
    if (close != EFI_SUCCESS) {
        goto failed;
    }
    boot->StorageExtents = package_extents;
    boot->StorageBytes = package_bytes;
    boot->StorageExtentCount = package_count;
    boot->StorageReserved = 0;
    boot->RootTaskExtents = root_count ? root_extents : 0;
    boot->RootTaskBytes = root_bytes;
    boot->RootTaskExtentCount = root_count;
    boot->RootTaskReserved = 0;
    return 1;
failed:
    wit_console_write("[BOOT] Package failure phase: ");
    wit_console_write_u64(phase);
    wit_console_write("\n");
    if (root && root->Close) {
        (void)root->Close(root);
    }
    for (WitU32 i = 0; i < package_count; ++i) {
        (void)services->FreePages(package_extents[i].Base, package_extents[i].Length / 4096);
    }
    for (WitU32 i = 0; i < root_count; ++i) {
        (void)services->FreePages(root_extents[i].Base, root_extents[i].Length / 4096);
    }
    return 0;
}
