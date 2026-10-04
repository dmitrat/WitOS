#include "uefi.h"
#include "witos/boot.h"
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
static WitBootStorageExtent extents[WIT_MAX_STORAGE_EXTENTS];

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

int wit_boot_storage(EfiHandle image, EfiBootServicesPrefix *services, WitBootInfo *boot)
{
    static const EfiGuid loadedGuid = {0x5B1B31A1, 0x9562, 0x11d2, {0x8e, 0x3f, 0, 0xa0, 0xc9, 0x69, 0x72, 0x3b}};
    static const EfiGuid volumeGuid = {0x964e5b22, 0x6459, 0x11d2, {0x8e, 0x39, 0, 0xa0, 0xc9, 0x69, 0x72, 0x3b}};
    WitU16 path[] = {'\\', 'W', 'I', 'T', 'O', 'S', '.', 'P', 'A', 'K', 0};
    LoadedImagePrefix *loaded = 0;
    BootVolume *volume = 0;
    BootFile *root = 0, *file = 0;
    WitU32 phase = 1, allocated = 0;
    WitU64 size = 0;
    WitU8 header[32], extra = 0;
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
    phase = 4;
    if (!root->Open || !root->Close || root->Open(root, &file, path, 1, 0) != EFI_SUCCESS || !file) {
        goto failed;
    }
    phase = 5;
    if (file->Revision < 0x10000 || !file->Close || !file->Read || !read_exact(file, header, sizeof(header))) {
        goto failed;
    }
    for (WitU32 i = 0; i < 8; ++i) {
        size |= (WitU64)header[24 + i] << (i * 8);
    }
    if (size < 32 || size > WIT_PACKAGE_MAX_BYTES) {
        goto failed;
    }
    static const WitU8 magic[8] = {'W', 'I', 'T', 'P', 'A', 'K', '0', '1'};
    for (WitU32 i = 0; i < 8; ++i) {
        if (header[i] != magic[i]) {
            goto failed;
        }
    }
    WitU64 done = 0;
    while (done < size) {
        WitU64 bytes = size - done;
        if (bytes > 1024 * 1024) {
            bytes = 1024 * 1024;
        }
        const WitU64 pages = (bytes + 4095) / 4096;
        WitU64 physical = 0xFFFFFFFFULL;
        phase = 7;
        if (allocated == WIT_MAX_STORAGE_EXTENTS || services->AllocatePages(1, 2, pages, &physical) != EFI_SUCCESS) {
            goto failed;
        }
        extents[allocated].Base = physical;
        extents[allocated].Length = pages * 4096;
        ++allocated;
        if (!physical || (physical & 4095) || physical > 0x100000000ULL - pages * 4096) {
            goto failed;
        }
        WitU64 copied = 0;
        if (!done) {
            for (WitU32 i = 0; i < sizeof(header); ++i) {
                ((WitU8 *)physical)[i] = header[i];
            }
            copied = 32;
        }
        phase = 8;
        if (!read_exact(file, (WitU8 *)physical + copied, bytes - copied)) {
            goto failed;
        }
        for (WitU64 i = bytes; i < pages * 4096; ++i) {
            ((WitU8 *)physical)[i] = 0;
        }
        done += bytes;
    }
    phase = 9;
    WitU64 tail = 1;
    if (file->Read(file, &tail, &extra) != EFI_SUCCESS || tail) {
        goto failed;
    }
    phase = 10;
    EfiStatus close = file->Close(file);
    file = 0;
    if (close != EFI_SUCCESS) {
        goto failed;
    }
    close = root->Close(root);
    root = 0;
    if (close != EFI_SUCCESS) {
        goto failed;
    }
    boot->StorageExtents = extents;
    boot->StorageBytes = size;
    boot->StorageExtentCount = allocated;
    boot->StorageReserved = 0;
    return 1;
failed:
    wit_console_write("[BOOT] Package failure phase: ");
    wit_console_write_u64(phase);
    wit_console_write("\n");
    if (file && file->Close) {
        (void)file->Close(file);
    }
    if (root && root->Close) {
        (void)root->Close(root);
    }
    for (WitU32 i = 0; i < allocated; ++i) {
        (void)services->FreePages(extents[i].Base, extents[i].Length / 4096);
    }
    return 0;
}
