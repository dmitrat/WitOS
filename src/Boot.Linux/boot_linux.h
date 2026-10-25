#ifndef WITOS_BOOT_LINUX_H
#define WITOS_BOOT_LINUX_H

#include "witos/boot.h"

/* The boot layer of the Linux arm64 boot protocol (plan step H6, ADR 0030): what its entry, its device tree reader
 * and its contract builder share. */

/* The initial ramdisk the image tool writes (RuntimeInitrd in WitOS.Dev): a page of header, then the root task's flat
 * image and the boot package, each at a page boundary. Little endian. */
#define WIT_INITRD_MAGIC 0x0031445253544957ULL /* "WITSRD1\0" */
#define WIT_INITRD_VERSION 1U
#define WIT_INITRD_HEADER_BYTES 4096U

typedef struct WitInitrdHeader {
    WitU64 Magic;
    WitU32 Version;
    WitU32 HeaderBytes;
    WitU64 RootOffset, RootBytes;
    WitU64 PackageOffset, PackageBytes;
} WitInitrdHeader;

#define WIT_LINUX_RANGES 32U

typedef struct WitLinuxRange {
    WitU64 Base, Length;
} WitLinuxRange;

/* What the boot layer takes from the device tree: the memory, the ranges it reserves, the initial ramdisk and the
 * entropy seed of /chosen. */
typedef struct WitLinuxTree {
    WitU64 Bytes;
    WitLinuxRange Memory[WIT_LINUX_RANGES];
    WitU32 MemoryCount;
    WitLinuxRange Reserved[WIT_LINUX_RANGES];
    WitU32 ReservedCount;
    WitU64 InitrdStart, InitrdEnd;
    const WitU8 *Seed;
    WitU32 SeedBytes;
} WitLinuxTree;

void wit_linux_read_tree(const WitU8 *tree, WitLinuxTree *out);
void wit_linux_switch_tables(const WitU64 *level0);
void wit_boot_describe_image(WitBootInfo *boot); /* the UEFI loader's reader of the image's own PE headers */

#endif
