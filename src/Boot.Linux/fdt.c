#include "witos/platform.h"
#include "boot_linux.h"

/* The device tree a Linux arm64 bootloader passes (plan step H6; the Devicetree Specification v0.4, §5), walked once
 * for what the boot contract needs: the root's #address-cells and #size-cells, the memory nodes' reg, the memory
 * reservation block, the children of /reserved-memory, and /chosen's initial ramdisk and entropy seed. The platform
 * walks the tree again for its devices; the common kernel never reads it. */

#define FDT_MAGIC 0xD00DFEEDU
#define FDT_BEGIN_NODE 1U
#define FDT_END_NODE 2U
#define FDT_PROP 3U
#define FDT_NOP 4U
#define FDT_END 9U
#define FDT_MAX_BYTES (16U << 20)
#define FDT_DEPTH 16U

static WitU32 be32(const WitU8 *p)
{
    return ((WitU32)p[0] << 24) | ((WitU32)p[1] << 16) | ((WitU32)p[2] << 8) | p[3];
}

static WitU64 be64(const WitU8 *p)
{
    return ((WitU64)be32(p) << 32) | be32(p + 4);
}

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

static int same(const char *a, const char *b)
{
    while (*a && *a == *b) {
        ++a;
        ++b;
    }
    return *a == *b;
}

/* Whether a node's name is the word or the word followed by a unit address ("memory@40000000"). */
static int named(const char *name, const char *word)
{
    while (*word && *name == *word) {
        ++name;
        ++word;
    }
    return *word == 0 && (*name == 0 || *name == '@');
}

/* A number of one or two cells. */
static WitU64 cells(const WitU8 *p, WitU32 count)
{
    return count == 2 ? be64(p) : be32(p);
}

static void add_range(WitLinuxRange *ranges, WitU32 *count, WitU64 base, WitU64 length, const char *what)
{
    if (length == 0) {
        return;
    }
    require(*count < WIT_LINUX_RANGES, what);
    require(base <= ~0ULL - length, "Device tree range overflows");
    ranges[*count].Base = base;
    ranges[*count].Length = length;
    ++*count;
}

/* A reg property: (address, size) pairs in the parent's cells. */
static void add_reg(WitLinuxRange *ranges, WitU32 *count, const WitU8 *value, WitU32 length, WitU32 address_cells,
    WitU32 size_cells, const char *what)
{
    const WitU32 entry = (address_cells + size_cells) * 4;
    require(address_cells >= 1 && address_cells <= 2 && size_cells >= 1 && size_cells <= 2 && length % entry == 0,
        "Unsupported device tree reg");
    for (WitU32 at = 0; at < length; at += entry) {
        add_range(
            ranges, count, cells(value + at, address_cells), cells(value + at + address_cells * 4, size_cells), what);
    }
}

void wit_linux_read_tree(const WitU8 *tree, WitLinuxTree *out)
{
    WitU32 address_cells[FDT_DEPTH], size_cells[FDT_DEPTH];
    WitU32 kind[FDT_DEPTH]; /* 1 memory, 2 chosen, 3 reserved-memory, 4 a child of reserved-memory */
    WitU32 depth = 0;
    require(tree && be32(tree) == FDT_MAGIC, "No device tree");
    const WitU32 total = be32(tree + 4);
    const WitU32 structure = be32(tree + 8);
    const WitU32 strings = be32(tree + 12);
    const WitU32 reservations = be32(tree + 16);
    const WitU32 version = be32(tree + 20);
    const WitU32 strings_size = be32(tree + 32);
    const WitU32 structure_size = be32(tree + 36);
    require(version >= 17 &&
            total >= 40 &&
            total <= FDT_MAX_BYTES &&
            structure < total &&
            structure_size <= total - structure &&
            strings < total &&
            strings_size <= total - strings &&
            reservations < total &&
            (reservations & 7) == 0,
        "Invalid device tree header");
    out->Bytes = total;
    out->MemoryCount = 0;
    out->ReservedCount = 0;
    out->InitrdStart = 0;
    out->InitrdEnd = 0;
    out->Seed = 0;
    out->SeedBytes = 0;

    /* The memory reservation block: (address, size) pairs of 64 bits, ended by a zero pair. */
    for (WitU32 at = reservations;; at += 16) {
        require(at <= total - 16, "Invalid device tree reservations");
        const WitU64 base = be64(tree + at);
        const WitU64 length = be64(tree + at + 8);
        if (base == 0 && length == 0) {
            break;
        }
        add_range(out->Reserved, &out->ReservedCount, base, length, "Too many device tree reservations");
    }

    WitU32 at = structure;
    const WitU32 end = structure + structure_size;
    while (at + 4 <= end) {
        const WitU32 token = be32(tree + at);
        at += 4;
        if (token == FDT_BEGIN_NODE) {
            const char *name = (const char *)(tree + at);
            WitU32 length = 0;
            while (at + length < end && tree[at + length]) {
                ++length;
            }
            require(at + length < end && depth < FDT_DEPTH, "Invalid device tree node");
            at = (at + length + 1 + 3) & ~3U;
            /* A node starts with the defaults its parent's children take; its own cells come with its properties. */
            address_cells[depth] = 2;
            size_cells[depth] = 1;
            kind[depth] = 0;
            if (depth == 1) {
                kind[1] = named(name, "memory")      ? 1
                    : named(name, "chosen")          ? 2
                    : named(name, "reserved-memory") ? 3
                                                     : 0;
            } else if (depth == 2 && kind[1] == 3) {
                kind[2] = 4;
            }
            ++depth;
        } else if (token == FDT_END_NODE) {
            require(depth > 0, "Invalid device tree nesting");
            --depth;
        } else if (token == FDT_PROP) {
            require(at + 8 <= end && depth > 0, "Invalid device tree property");
            const WitU32 length = be32(tree + at);
            const WitU32 name_offset = be32(tree + at + 4);
            at += 8;
            require(length <= end - at && name_offset < strings_size, "Invalid device tree property");
            const char *name = (const char *)(tree + strings + name_offset);
            const WitU8 *value = tree + at;
            const WitU32 node = depth - 1;
            /* A node's #address-cells and #size-cells describe its children's reg. */
            if (same(name, "#address-cells") && length == 4) {
                address_cells[node] = be32(value);
            } else if (same(name, "#size-cells") && length == 4) {
                size_cells[node] = be32(value);
            } else if (same(name, "reg") && node >= 1 && kind[node] == 1) {
                add_reg(out->Memory, &out->MemoryCount, value, length, address_cells[node - 1], size_cells[node - 1],
                    "Too many memory ranges");
            } else if (same(name, "reg") && node >= 1 && kind[node] == 4) {
                add_reg(out->Reserved, &out->ReservedCount, value, length, address_cells[node - 1],
                    size_cells[node - 1], "Too many reserved memory ranges");
            } else if (kind[node] == 2 && same(name, "linux,initrd-start") && (length == 4 || length == 8)) {
                out->InitrdStart = length == 8 ? be64(value) : be32(value);
            } else if (kind[node] == 2 && same(name, "linux,initrd-end") && (length == 4 || length == 8)) {
                out->InitrdEnd = length == 8 ? be64(value) : be32(value);
            } else if (kind[node] == 2 && same(name, "rng-seed")) {
                out->Seed = value;
                out->SeedBytes = length;
            }
            at = (at + length + 3) & ~3U;
        } else if (token == FDT_NOP) {
            continue;
        } else {
            require(token == FDT_END && depth == 0, "Invalid device tree structure");
            return;
        }
    }
    wit_panic("Device tree structure without its end");
}
