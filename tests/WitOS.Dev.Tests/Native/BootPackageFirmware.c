#define _CRT_SECURE_NO_WARNINGS /* fopen and the rest of standard C on MSVC */
#if defined(_WIN32)
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#else
#include <sys/mman.h>
#endif
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stddef.h>
#include <stdint.h>
#include "../../../src/Boot.Uefi/storage.c"
#include "PackageReader.h"
_Static_assert(offsetof(EfiBootServicesPrefix, AllocatePages) == 40, "UEFI AllocatePages offset");
_Static_assert(offsetof(EfiBootServicesPrefix, HandleProtocol) == 152, "UEFI HandleProtocol offset");
_Static_assert(offsetof(EfiBootServicesPrefix, ExitBootServices) == 232, "UEFI ExitBootServices offset");
_Static_assert(offsetof(EfiBootServicesPrefix, LocateProtocol) == 320, "UEFI LocateProtocol offset");

void wit_console_write(const char *value)
{
    (void)value;
}

void wit_console_write_u64(WitU64 value)
{
    (void)value;
}

static unsigned mode, allocCalls, allocations, frees, rootCloses, fileCloses;
static unsigned char *source;
static size_t sourceSize, cursor;
static void *held[8];
static WitU64 heldPages[8];
static BootFile fakeRoot, fakeFile;
static BootVolume fakeVolume;
static LoadedImagePrefix fakeImage = {0x1000, 0, 0, (void *)2};

static EfiStatus protocol(EfiHandle handle, const EfiGuid *guid, void **output)
{
    if (mode == 1) {
        return EFI_INVALID_PARAMETER;
    }
    if (handle == (void *)1 && guid->A == 0x5B1B31A1) {
        *output = &fakeImage;
        return EFI_SUCCESS;
    }
    if (handle == (void *)2 && guid->A == 0x964e5b22) {
        *output = &fakeVolume;
        return EFI_SUCCESS;
    }
    return EFI_INVALID_PARAMETER;
}

static EfiStatus volume_open(BootVolume *volume, BootFile **output)
{
    if (volume != &fakeVolume || mode == 2) {
        return EFI_INVALID_PARAMETER;
    }
    *output = &fakeRoot;
    return EFI_SUCCESS;
}

static EfiStatus file_open(BootFile *root, BootFile **output, WitU16 *path, WitU64 access, WitU64 attributes)
{
    static const WitU16 expected[] = {0x5c, 'W', 'I', 'T', 'O', 'S', '.', 'P', 'A', 'K', 0};
    if (root != &fakeRoot || mode == 3 || access != 1 || attributes || memcmp(path, expected, sizeof(expected))) {
        return EFI_INVALID_PARAMETER;
    }
    *output = &fakeFile;
    return EFI_SUCCESS;
}

static EfiStatus file_close(BootFile *file)
{
    if (file == &fakeRoot) {
        ++rootCloses;
        return mode == 11 ? EFI_INVALID_PARAMETER : EFI_SUCCESS;
    }
    if (file != &fakeFile) {
        return EFI_INVALID_PARAMETER;
    }
    ++fileCloses;
    return mode == 10 ? EFI_INVALID_PARAMETER : EFI_SUCCESS;
}

static EfiStatus file_read(BootFile *file, WitU64 *count, void *output)
{
    if (file != &fakeFile) {
        return EFI_INVALID_PARAMETER;
    }
    if (mode == 8) {
        ++*count;
        return EFI_SUCCESS;
    }
    if (mode == 7 && cursor >= 1024 * 1024) {
        return EFI_INVALID_PARAMETER;
    }
    size_t end = mode == 4 ? 16 : sourceSize;
    size_t bytes = cursor < end ? end - cursor : 0;
    if (bytes > *count) {
        bytes = (size_t)*count;
    }
    if (mode == 13 && bytes > 17) {
        bytes = 17;
    }
    memcpy(output, source + cursor, bytes);
    if (mode == 12 && !cursor && bytes == 32) {
        memset((unsigned char *)output + 24, 0xFF, 8);
    }
    if (mode == 9 && cursor == sourceSize && *count) {
        *(unsigned char *)output = 0xAA;
        bytes = 1;
    }
    cursor += bytes;
    *count = bytes;
    return EFI_SUCCESS;
}

/* Pages at a fixed address below 4 GiB, as the firmware hands them out, or zero where the range is taken (plan step
 * T2.2: Windows or POSIX memory calls). */
static void *fixed_pages(uintptr_t at, WitU64 pages)
{
#if defined(_WIN32)
    return VirtualAlloc((void *)at, (SIZE_T)pages * 4096, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
#else
    void *block = mmap((void *)at, (size_t)pages * 4096, PROT_READ | PROT_WRITE,
        MAP_PRIVATE | MAP_ANONYMOUS | MAP_FIXED_NOREPLACE, -1, 0);
    return block == MAP_FAILED || block != (void *)at ? 0 : block;
#endif
}

static int release_pages(void *block, WitU64 pages)
{
#if defined(_WIN32)
    (void)pages;
    return VirtualFree(block, 0, MEM_RELEASE) != 0;
#else
    return munmap(block, (size_t)pages * 4096) == 0;
#endif
}

static EfiStatus allocate_pages(WitU32 type, WitU32 memory, WitU64 pages, WitU64 *address)
{
    ++allocCalls;
    if (type != 1 || memory != 2 || !pages || pages > 256 || *address != 0xFFFFFFFFULL || allocations == 8) {
        return EFI_INVALID_PARAMETER;
    }
    if (mode == 5 || (mode == 6 && allocCalls == 2)) {
        return EFI_INVALID_PARAMETER;
    }
    for (uintptr_t at = 0x20000000; at < 0xE0000000; at += 0x200000) {
        void *block = fixed_pages(at, pages);
        if (block) {
            held[allocations] = block;
            heldPages[allocations++] = pages;
            *address = (WitU64)block;
            return EFI_SUCCESS;
        }
    }
    return EFI_INVALID_PARAMETER;
}

static EfiStatus free_pages(WitU64 address, WitU64 pages)
{
    for (unsigned i = 0; i < allocations; ++i) {
        if (held[i] == (void *)address && heldPages[i] == pages) {
            if (!release_pages(held[i], pages)) {
                return EFI_INVALID_PARAMETER;
            }
            held[i] = 0;
            ++frees;
            return EFI_SUCCESS;
        }
    }
    return EFI_INVALID_PARAMETER;
}

int boot_package_firmware_test(const char *path)
{
    FILE *file = fopen(path, "rb");
    if (!file) {
        return 0;
    }
    if (fseek(file, 0, SEEK_END)) {
        fclose(file);
        return 0;
    }
    long length = ftell(file);
    if (length <= 2 * 1024 * 1024 || length > 4 * 1024 * 1024 || fseek(file, 0, SEEK_SET)) {
        fclose(file);
        return 0;
    }
    sourceSize = (size_t)length;
    source = (unsigned char *)malloc(sourceSize);
    if (!source || fread(source, 1, sourceSize, file) != sourceSize) {
        fclose(file);
        return 0;
    }
    fclose(file);
    fakeRoot = (BootFile){0x10000, file_open, file_close, 0, 0};
    fakeFile = (BootFile){0x10000, 0, file_close, 0, file_read};
    fakeVolume = (BootVolume){0x10000, volume_open};
    EfiBootServicesPrefix services = {0};
    services.HandleProtocol = protocol;
    services.AllocatePages = allocate_pages;
    services.FreePages = free_pages;
    for (mode = 0; mode < 14; ++mode) {
        cursor = 0;
        allocCalls = allocations = frees = rootCloses = fileCloses = 0;
        memset(held, 0, sizeof(held));
        WitBootInfo boot;
        unsigned char before[sizeof(boot)];
        memset(&boot, 0xA5, sizeof(boot));
        memcpy(before, &boot, sizeof(boot));
        const int success = wit_boot_storage((void *)1, &services, &boot), expected = mode == 0 || mode == 13;
        if (success != expected || (!success && memcmp(before, &boot, sizeof(boot)))) {
            printf("FAIL firmware publication mode=%u\n", mode);
            return 0;
        }
        if (success) {
            unsigned char *joined = (unsigned char *)malloc(sourceSize);
            size_t done = 0;
            if (!joined || boot.StorageBytes != sourceSize || boot.StorageExtentCount != 3 || boot.StorageReserved) {
                return 0;
            }
            for (unsigned i = 0; i < boot.StorageExtentCount; ++i) {
                const WitBootStorageExtent *extent = &boot.StorageExtents[i];
                size_t bytes = sourceSize - done;
                if (bytes > extent->Length) {
                    bytes = (size_t)extent->Length;
                }
                memcpy(joined + done, (void *)extent->Base, bytes);
                for (size_t j = bytes; j < extent->Length; ++j) {
                    if (((unsigned char *)extent->Base)[j]) {
                        return 0;
                    }
                }
                done += bytes;
                if (free_pages(extent->Base, extent->Length / 4096) != EFI_SUCCESS) {
                    return 0;
                }
            }
            WitPackage checked;
            int valid = done == sourceSize &&
                !memcmp(joined, source, sourceSize) &&
                wit_package_open(joined, sourceSize, &checked) == WitPackageOk;
            free(joined);
            if (!valid) {
                return 0;
            }
        }
        if (frees != allocations ||
            rootCloses != ((mode == 1 || mode == 2) ? 0U : 1U) ||
            fileCloses != ((mode >= 1 && mode <= 3) ? 0U : 1U)) {
            printf("FAIL firmware cleanup mode=%u allocated=%u freed=%u root=%u file=%u\n", mode, allocations, frees,
                rootCloses, fileCloses);
            return 0;
        }
    }
    free(source);
    puts("PASS: 14 firmware transport/publication/rollback cases including fragmented and partial reads");
    return 1;
}
