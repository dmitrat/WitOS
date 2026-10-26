#include "witos/arch.h"
#include "witos/boot.h"
#include "witos/platform.h"
#include "witos/virtual.h"
#include "arm.h"

/* The flattened device tree of an ARM64 board (H7.1), read after paging is active. The firmware's copy of the tree is
 * padded well beyond its blocks, so only the header, the structure block and the strings block are mapped, each page
 * once; a page the kernel maps already is read as it is. A walk keeps each open node's #address-cells, #size-cells and
 * ranges, so that a reg below a bus (the Pi's /soc) is translated into the processor's address space. */

#define FDT_MAGIC 0xD00DFEEDU
#define FDT_BEGIN_NODE 1U
#define FDT_END_NODE 2U
#define FDT_PROP 3U
#define FDT_NOP 4U
#define FDT_MAX_BYTES (16U << 20) /* a sanity bound on the padded total size */
#define FDT_BLOCK_BYTES (256U << 10) /* the structure or strings block of a board's tree */
#define MAPPED_CAPACITY 192U
#define DEFAULT_ADDRESS_CELLS 2U /* the binding's defaults where a node names none */
#define DEFAULT_SIZE_CELLS 1U
#define PPI_FIRST 16U
#define SPI_FIRST 32U
#define VIRTUAL_TIMER_PPI 27U /* the architected EL1 virtual timer's INTID where the tree names no timer */
#define VIRTUAL_TIMER_ENTRY 2U /* arm,armv8-timer's interrupts: secure, non-secure physical, virtual, hypervisor */
#define GIC_INTERRUPT_CELLS 3U

static WitU64 mapped[MAPPED_CAPACITY];
static WitU32 mapped_count;
static WitArmBoard board;

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

void wit_arm_map_once(const WitBootInfo *boot, WitU64 page)
{
    for (WitU32 i = 0; i < mapped_count; ++i) {
        if (mapped[i] == page) {
            return;
        }
    }
    require(mapped_count < MAPPED_CAPACITY, "Too many device pages mapped during enumeration");
    if (wit_virtual_view(page) == 0) {
        wit_arch_map_device_page(boot, page);
    }
    mapped[mapped_count++] = page;
}

static void map_range(const WitBootInfo *boot, WitU64 base, WitU64 bytes)
{
    for (WitU64 page = base & ~4095ULL; page < base + bytes; page += 4096) {
        wit_arm_map_once(boot, page);
    }
}

WitU32 wit_arm_be32(const WitU8 *p)
{
    return ((WitU32)p[0] << 24) | ((WitU32)p[1] << 16) | ((WitU32)p[2] << 8) | p[3];
}

WitU64 wit_arm_be64(const WitU8 *p)
{
    return ((WitU64)wit_arm_be32(p) << 32) | wit_arm_be32(p + 4);
}

int wit_arm_same(const char *a, const char *b)
{
    while (*a && *a == *b) {
        ++a;
        ++b;
    }
    return *a == *b;
}

/* Opens the tree at an address: with the boot contract once paging is active, mapping its pages; without one in a boot
 * stage, where the tree is reachable as it is (H7.2: the console before the first line). */
static int open_tree(const WitBootInfo *boot, WitU64 address, WitArmTree *tree)
{
    const WitU8 *base = (const WitU8 *)address;
    for (WitU32 i = 0; i < sizeof(*tree); ++i) {
        ((WitU8 *)tree)[i] = 0;
    }
    if (!address || (address & 7)) {
        return 0;
    }
    if (boot) {
        wit_arm_map_once(boot, address & ~4095ULL);
    }
    if (wit_arm_be32(base) != FDT_MAGIC) {
        return 0;
    }
    const WitU32 total = wit_arm_be32(base + 4), structure = wit_arm_be32(base + 8), strings = wit_arm_be32(base + 12);
    const WitU32 version = wit_arm_be32(base + 20), strings_size = wit_arm_be32(base + 32);
    const WitU32 structure_size = wit_arm_be32(base + 36);
    if (version < 17 ||
        total > FDT_MAX_BYTES ||
        structure >= total ||
        strings >= total ||
        structure_size > total - structure ||
        strings_size > total - strings ||
        structure_size > FDT_BLOCK_BYTES ||
        strings_size > FDT_BLOCK_BYTES) {
        return 0;
    }
    if (boot) {
        map_range(boot, address + structure, structure_size);
        map_range(boot, address + strings, strings_size);
    }
    tree->Base = base;
    tree->Offset = structure;
    tree->Limit = structure + structure_size;
    tree->Strings = strings;
    tree->StringsLimit = strings + strings_size;
    return 1;
}

int wit_arm_tree_open(const WitBootInfo *boot, WitArmTree *tree)
{
    return open_tree(boot, boot->DeviceTree, tree);
}

int wit_arm_tree_open_early(WitU64 address, WitArmTree *tree)
{
    return open_tree(0, address, tree);
}

int wit_arm_tree_next(WitArmTree *tree, WitArmTreeItem *item)
{
    const WitU8 *base = tree->Base;
    while (base && tree->Offset + 4 <= tree->Limit) {
        const WitU32 token = wit_arm_be32(base + tree->Offset);
        tree->Offset += 4;
        if (token == FDT_BEGIN_NODE) {
            WitU32 length = 0;
            while (tree->Offset + length < tree->Limit && base[tree->Offset + length]) {
                ++length;
            }
            item->Kind = WIT_ARM_TREE_BEGIN;
            item->Name = (const char *)base + tree->Offset;
            item->Value = 0;
            item->Length = 0;
            tree->Offset = (tree->Offset + length + 1 + 3) & ~3U;
            item->Depth = ++tree->Depth;
            if (tree->Depth <= WIT_ARM_TREE_DEPTH) {
                tree->Nodes[tree->Depth] = (WitArmTreeNode){DEFAULT_ADDRESS_CELLS, DEFAULT_SIZE_CELLS, 0, 0, 0};
            }
            return 1;
        }
        if (token == FDT_END_NODE) {
            item->Kind = WIT_ARM_TREE_END;
            item->Depth = tree->Depth;
            item->Name = 0;
            item->Value = 0;
            item->Length = 0;
            if (tree->Depth) {
                --tree->Depth;
            }
            return 1;
        }
        if (token == FDT_PROP) {
            if (tree->Offset + 8 > tree->Limit) {
                break;
            }
            const WitU32 length = wit_arm_be32(base + tree->Offset), name = wit_arm_be32(base + tree->Offset + 4);
            tree->Offset += 8;
            if (tree->Offset + length > tree->Limit || tree->Strings + name >= tree->StringsLimit) {
                break;
            }
            item->Kind = WIT_ARM_TREE_PROPERTY;
            item->Depth = tree->Depth;
            item->Name = (const char *)base + tree->Strings + name;
            item->Value = base + tree->Offset;
            item->Length = length;
            tree->Offset = (tree->Offset + length + 3) & ~3U;
            if (tree->Depth <= WIT_ARM_TREE_DEPTH) {
                WitArmTreeNode *node = &tree->Nodes[tree->Depth];
                if (wit_arm_same(item->Name, "#address-cells") && length >= 4) {
                    node->AddressCells = wit_arm_be32(item->Value);
                } else if (wit_arm_same(item->Name, "#size-cells") && length >= 4) {
                    node->SizeCells = wit_arm_be32(item->Value);
                } else if (wit_arm_same(item->Name, "ranges")) {
                    node->Ranges = item->Value;
                    node->RangesLength = length;
                    node->HasRanges = 1;
                }
            }
            return 1;
        }
        if (token != FDT_NOP) {
            break; /* FDT_END or an unknown token. */
        }
    }
    tree->Base = 0;
    return 0;
}

static WitU64 cells(const WitU8 *p, WitU32 count)
{
    return count == 2 ? wit_arm_be64(p) : count == 1 ? wit_arm_be32(p) : 0;
}

/* An address of node depth's children into the address space of its parent through its ranges: each entry is a child
 * address, a parent address and a size; empty ranges are the identity, and a node without ranges translates nothing. */
static int translate(const WitArmTree *tree, WitU32 depth, WitU64 *address)
{
    const WitArmTreeNode *node = &tree->Nodes[depth], *parent = &tree->Nodes[depth - 1];
    if (!node->HasRanges) {
        return 0;
    }
    if (node->RangesLength == 0) {
        return 1;
    }
    const WitU32 child = node->AddressCells, up = parent->AddressCells, size = node->SizeCells;
    const WitU32 entry = (child + up + size) * 4;
    if (child == 0 || child > 2 || up == 0 || up > 2 || size == 0 || size > 2) {
        return 0;
    }
    for (WitU32 at = 0; at + entry <= node->RangesLength; at += entry) {
        const WitU8 *e = node->Ranges + at;
        const WitU64 from = cells(e, child), to = cells(e + child * 4, up), bytes = cells(e + (child + up) * 4, size);
        if (*address >= from && *address - from < bytes) {
            *address = to + (*address - from);
            return 1;
        }
    }
    return 0;
}

int wit_arm_tree_reg(const WitArmTree *tree, const WitArmTreeItem *item, WitU32 index, WitU64 *base, WitU64 *size)
{
    const WitU32 depth = item->Depth;
    if (depth < 2 || depth > WIT_ARM_TREE_DEPTH) {
        return 0;
    }
    const WitArmTreeNode *parent = &tree->Nodes[depth - 1];
    const WitU32 address_cells = parent->AddressCells, size_cells = parent->SizeCells;
    const WitU32 entry = (address_cells + size_cells) * 4;
    if (address_cells == 0 || address_cells > 2 || size_cells > 2 || (index + 1) * entry > item->Length) {
        return 0;
    }
    WitU64 address = cells(item->Value + index * entry, address_cells);
    *size = cells(item->Value + index * entry + address_cells * 4, size_cells);
    /* The root's children (depth 2) address the processor's space; deeper nodes pass through each bus above them. */
    for (WitU32 bus = depth - 1; bus >= 2; --bus) {
        if (!translate(tree, bus, &address)) {
            return 0;
        }
    }
    *base = address;
    return 1;
}

/* Whether a path's component names a node: the whole name, or the name before its unit address when the component has
 * none ("serial" for "serial@7e201000"), as the binding allows where it is unambiguous. */
static int component_names(const char *component, WitU32 length, const char *name)
{
    WitU32 i = 0, unit = 1;
    for (WitU32 k = 0; k < length; ++k) {
        unit &= component[k] != '@';
    }
    for (; i < length && name[i] && name[i] == component[i]; ++i) {
    }
    return i == length && (name[i] == 0 || (unit && name[i] == '@'));
}

WitU32 wit_arm_tree_seek(WitArmTree *tree, const char *path, WitU32 length)
{
    WitArmTreeItem item;
    WitU32 after[WIT_ARM_TREE_DEPTH + 1]; /* the path's offset past the component each open node matched */
    WitU32 matched = 0; /* the deepest open node whose ancestors all match the path */
    if (length == 0 || path[0] != '/') {
        return 0;
    }
    while (length > 1 && path[length - 1] == '/') {
        --length;
    }
    while (wit_arm_tree_next(tree, &item)) {
        if (item.Kind == WIT_ARM_TREE_BEGIN && item.Depth == 1) {
            matched = 1;
            after[1] = 1;
            if (length == 1) {
                return 1;
            }
        } else if (item.Kind == WIT_ARM_TREE_BEGIN && item.Depth <= WIT_ARM_TREE_DEPTH && matched == item.Depth - 1) {
            const WitU32 start = after[matched];
            WitU32 end = start;
            while (end < length && path[end] != '/') {
                ++end;
            }
            if (end > start && component_names(path + start, end - start, item.Name)) {
                matched = item.Depth;
                after[matched] = end + 1;
                if (end == length) {
                    return matched;
                }
            }
        } else if (item.Kind == WIT_ARM_TREE_END && matched >= item.Depth) {
            matched = item.Depth - 1;
        }
    }
    return 0;
}

int wit_arm_tree_property(WitArmTree *tree, WitU32 depth, const char *name, WitArmTreeItem *item)
{
    while (wit_arm_tree_next(tree, item)) {
        if (item->Kind != WIT_ARM_TREE_PROPERTY || item->Depth != depth) {
            return 0; /* a child or the node's end: a node's properties come first */
        }
        if (wit_arm_same(item->Name, name)) {
            return 1;
        }
    }
    return 0;
}

int wit_arm_tree_names(const WitArmTreeItem *item, const char *word)
{
    const WitU8 *list = item->Value;
    for (WitU32 i = 0; i < item->Length;) {
        WitU32 k = 0;
        while (i + k < item->Length && list[i + k] && word[k] && list[i + k] == word[k]) {
            ++k;
        }
        if (word[k] == 0 && (i + k == item->Length || list[i + k] == 0)) {
            return 1;
        }
        while (i < item->Length && list[i]) {
            ++i;
        }
        ++i;
    }
    return 0;
}

/* What a node of the walk may turn out to be, decided at its end, since its compatible may follow its reg. */
#define KIND_GIC3 1U
#define KIND_GIC2 2U
#define KIND_TIMER 3U
#define KIND_PL031 4U

typedef struct BoardNode {
    WitU32 Kind, Regions, InterruptsLength;
    WitU64 Base[2];
    const WitU8 *Interrupts;
} BoardNode;

/* The board's devices the platform drives, from one walk: the first GIC by its compatible (arm,gic-v3; a GICv2 by any
 * of the bindings QEMU's and the Pi's trees use), the virtual timer's interrupt of arm,armv8-timer, the first PL031. */
const WitArmBoard *wit_arm_board(const WitBootInfo *boot)
{
    WitArmTree tree;
    WitArmTreeItem item;
    BoardNode nodes[WIT_ARM_TREE_DEPTH + 1];
    if (board.Read) {
        return &board;
    }
    board.Read = 1;
    board.TimerInterrupt = VIRTUAL_TIMER_PPI;
    if (!wit_arm_tree_open(boot, &tree)) {
        return &board;
    }
    while (wit_arm_tree_next(&tree, &item)) {
        if (item.Depth > WIT_ARM_TREE_DEPTH) {
            continue;
        }
        BoardNode *node = &nodes[item.Depth];
        if (item.Kind == WIT_ARM_TREE_BEGIN) {
            *node = (BoardNode){0};
        } else if (item.Kind == WIT_ARM_TREE_PROPERTY) {
            if (wit_arm_same(item.Name, "compatible")) {
                if (wit_arm_tree_names(&item, "arm,gic-v3")) {
                    node->Kind = KIND_GIC3;
                } else if (wit_arm_tree_names(&item, "arm,gic-400") ||
                    wit_arm_tree_names(&item, "arm,cortex-a15-gic") ||
                    wit_arm_tree_names(&item, "arm,cortex-a7-gic")) {
                    node->Kind = KIND_GIC2;
                } else if (wit_arm_tree_names(&item, "arm,armv8-timer")) {
                    node->Kind = KIND_TIMER;
                } else if (wit_arm_tree_names(&item, "arm,pl031")) {
                    node->Kind = KIND_PL031;
                }
            } else if (wit_arm_same(item.Name, "reg")) {
                WitU64 size = 0;
                for (node->Regions = 0; node->Regions < 2; ++node->Regions) {
                    if (!wit_arm_tree_reg(&tree, &item, node->Regions, &node->Base[node->Regions], &size)) {
                        break;
                    }
                }
            } else if (wit_arm_same(item.Name, "interrupts")) {
                node->Interrupts = item.Value;
                node->InterruptsLength = item.Length;
            }
        } else if (item.Kind == WIT_ARM_TREE_END) {
            if ((node->Kind == KIND_GIC3 || node->Kind == KIND_GIC2) && node->Regions == 2 && !board.GicVersion) {
                board.GicVersion = node->Kind == KIND_GIC3 ? 3 : 2;
                board.Distributor = node->Base[0];
                if (node->Kind == KIND_GIC3) {
                    board.Redistributors = node->Base[1];
                } else {
                    board.CpuInterface = node->Base[1];
                }
            } else if (node->Kind == KIND_TIMER &&
                node->InterruptsLength >= (VIRTUAL_TIMER_ENTRY + 1) * GIC_INTERRUPT_CELLS * 4) {
                const WitU8 *entry = node->Interrupts + VIRTUAL_TIMER_ENTRY * GIC_INTERRUPT_CELLS * 4;
                const WitU32 type = wit_arm_be32(entry), number = wit_arm_be32(entry + 4);
                board.TimerInterrupt = (type == 1 ? PPI_FIRST : SPI_FIRST) + number;
            } else if (node->Kind == KIND_PL031 && node->Regions >= 1 && !board.RealTimeClock) {
                board.RealTimeClock = node->Base[0];
            }
        }
    }
    return &board;
}
