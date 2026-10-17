#include "witos/arch.h"
#include "witos/boot.h"
#include "witos/platform.h"
#include "witos/processor.h"
#include "witos/virtual.h"
#include "virt_rv.h"

/* The flattened device tree of the virt board (plan step V1.2), walked once: the children of /cpus whose device_type
 * is "cpu", each with its reg (the hart identity, in as many cells as the parent's #address-cells says), a status other
 * than "okay" that marks it unusable and an ISA string or extension list that may name sstc, the supervisor timer
 * compare register; and the parent's timebase-frequency, the rate of the time counter. The firmware's copy of the tree
 * is padded well beyond its blocks, so only the header, the structure block and the strings block are mapped, each
 * page once; a page the kernel maps already (the tree in usable memory) is read as it is. */

#define FDT_MAGIC 0xD00DFEEDU
#define FDT_BEGIN_NODE 1U
#define FDT_END_NODE 2U
#define FDT_PROP 3U
#define FDT_NOP 4U
#define FDT_MAX_BYTES (16U << 20)
#define FDT_BLOCK_BYTES (256U << 10)
#define MAPPED_CAPACITY 192U

static WitVirtRvTree tree_info;
static WitU64 mapped[MAPPED_CAPACITY];
static WitU32 mapped_count;

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

static void map_once(const WitBootInfo *boot, WitU64 page)
{
    for (WitU32 i = 0; i < mapped_count; ++i) {
        if (mapped[i] == page) {
            return;
        }
    }
    require(mapped_count < MAPPED_CAPACITY, "Too many device tree pages mapped");
    if (wit_virtual_view(page) == 0) {
        wit_arch_map_device_page(boot, page);
    }
    mapped[mapped_count++] = page;
}

static void map_range(const WitBootInfo *boot, WitU64 base, WitU64 bytes)
{
    for (WitU64 page = base & ~4095ULL; page < base + bytes; page += 4096) {
        map_once(boot, page);
    }
}

static WitU32 be32(const WitU8 *p)
{
    return ((WitU32)p[0] << 24) | ((WitU32)p[1] << 16) | ((WitU32)p[2] << 8) | p[3];
}

static WitU64 be64(const WitU8 *p)
{
    return ((WitU64)be32(p) << 32) | be32(p + 4);
}

static int same(const char *a, const char *b)
{
    while (*a && *a == *b) {
        ++a;
        ++b;
    }
    return *a == *b;
}

/* Whether an ISA string ("rv64imafdc_zicsr_sstc_...") names the extension as one of its underscore-separated words
 * after the single-letter base. */
static int isa_names(const WitU8 *isa, WitU32 length, const char *extension)
{
    WitU32 i = 0;
    while (i < length && isa[i] && isa[i] != '_') {
        ++i; /* the base and its single-letter extensions */
    }
    while (i < length && isa[i] == '_') {
        WitU32 start = ++i, k = 0;
        while (i < length && isa[i] && isa[i] != '_') {
            ++i;
        }
        for (k = 0; start + k < i && extension[k] && isa[start + k] == extension[k]; ++k) {
        }
        if (start + k == i && extension[k] == 0) {
            return 1;
        }
    }
    return 0;
}

/* Whether a string list ("zicsr\0sstc\0...") holds the word. */
static int list_names(const WitU8 *list, WitU32 length, const char *word)
{
    for (WitU32 i = 0; i < length;) {
        WitU32 k = 0;
        while (i + k < length && list[i + k] && word[k] && list[i + k] == word[k]) {
            ++k;
        }
        if (word[k] == 0 && (i + k == length || list[i + k] == 0)) {
            return 1;
        }
        while (i < length && list[i]) {
            ++i;
        }
        ++i;
    }
    return 0;
}

const WitVirtRvTree *wit_virt_rv_tree(const WitBootInfo *boot)
{
    const WitU8 *tree = (const WitU8 *)boot->DeviceTree;
    if (tree_info.Read) {
        return &tree_info;
    }
    tree_info.Read = 1;
    if (!boot->DeviceTree || (boot->DeviceTree & 7)) {
        return &tree_info;
    }
    map_once(boot, boot->DeviceTree & ~4095ULL);
    if (be32(tree) != FDT_MAGIC) {
        return &tree_info;
    }
    const WitU32 total = be32(tree + 4), structure = be32(tree + 8), strings = be32(tree + 12);
    const WitU32 version = be32(tree + 20), strings_size = be32(tree + 32), structure_size = be32(tree + 36);
    if (version < 17 ||
        total > FDT_MAX_BYTES ||
        structure >= total ||
        strings >= total ||
        structure_size > total - structure ||
        strings_size > total - strings ||
        structure_size > FDT_BLOCK_BYTES ||
        strings_size > FDT_BLOCK_BYTES) {
        return &tree_info;
    }
    map_range(boot, boot->DeviceTree + structure, structure_size);
    map_range(boot, boot->DeviceTree + strings, strings_size);
    const WitU32 limit = structure + structure_size, strings_limit = strings + strings_size;
    WitU32 offset = structure, depth = 0, address_cells = 1;
    int in_cpus = 0, is_cpu = 0, has_reg = 0, disabled = 0, sstc = 0;
    WitU64 reg = 0;
    while (offset + 4 <= limit) {
        const WitU32 token = be32(tree + offset);
        offset += 4;
        if (token == FDT_BEGIN_NODE) {
            WitU32 length = 0;
            while (offset + length < limit && tree[offset + length]) {
                ++length;
            }
            const char *name = (const char *)tree + offset;
            offset = (offset + length + 1 + 3) & ~3U;
            ++depth;
            if (depth == 2) { /* the root node is depth 1 */
                in_cpus = same(name, "cpus");
                address_cells = 1;
            } else if (depth == 3 && in_cpus) {
                is_cpu = 0;
                has_reg = 0;
                disabled = 0;
                sstc = 0;
                reg = 0;
            }
        } else if (token == FDT_END_NODE) {
            if (depth == 3 && in_cpus && is_cpu && has_reg && tree_info.HartCount < WIT_PROCESSOR_CAPACITY) {
                WitVirtRvHart *hart = &tree_info.Harts[tree_info.HartCount++];
                hart->Id = reg;
                hart->Enabled = !disabled;
                hart->Sstc = sstc;
            }
            if (depth == 2) {
                in_cpus = 0;
            }
            if (depth) {
                --depth;
            }
        } else if (token == FDT_PROP) {
            if (offset + 8 > limit) {
                return &tree_info;
            }
            const WitU32 length = be32(tree + offset), name_offset = be32(tree + offset + 4);
            offset += 8;
            if (offset + length > limit || strings + name_offset >= strings_limit) {
                return &tree_info;
            }
            const char *name = (const char *)tree + strings + name_offset;
            const WitU8 *value = tree + offset;
            offset = (offset + length + 3) & ~3U;
            if (!in_cpus) {
                continue;
            }
            if (depth == 2 && same(name, "#address-cells") && length >= 4) {
                address_cells = be32(value);
            } else if (depth == 2 && same(name, "timebase-frequency") && length >= 4) {
                tree_info.TimebaseFrequency = be32(value);
            } else if (depth == 3 && same(name, "device_type") && length >= 4) {
                is_cpu = same((const char *)value, "cpu");
            } else if (depth == 3 && same(name, "reg") && length >= address_cells * 4 && address_cells <= 2) {
                reg = address_cells == 2 ? be64(value) : be32(value);
                has_reg = 1;
            } else if (depth == 3 && same(name, "status") && length >= 5) {
                disabled = !same((const char *)value, "okay");
            } else if (depth == 3 && same(name, "riscv,isa") && length >= 5) {
                sstc |= isa_names(value, length, "sstc");
            } else if (depth == 3 && same(name, "riscv,isa-extensions") && length >= 1) {
                sstc |= list_names(value, length, "sstc");
            }
        } else if (token == FDT_NOP) {
        } else {
            break; /* FDT_END or an unknown token. */
        }
    }
    return &tree_info;
}

/* The processors of the virt board (plan step K7.1's role): the harts of /cpus in the tree's order. */
WitU32 wit_platform_processors(const WitBootInfo *boot, WitProcessorDescriptor *out, WitU32 capacity)
{
    const WitVirtRvTree *tree = wit_virt_rv_tree(boot);
    WitU32 count = 0;
    for (; count < tree->HartCount && count < capacity; ++count) {
        out[count].HardwareId = tree->Harts[count].Id;
        out[count].Enabled = tree->Harts[count].Enabled;
        out[count].Reserved = 0;
    }
    return count;
}
