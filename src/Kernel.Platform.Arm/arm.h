#ifndef WITOS_ARM_H
#define WITOS_ARM_H

#include "witos/types.h"

/* The ARM64 platform (H7.1): one kernel for every board a device tree describes — QEMU's virt, then the Raspberry Pis —
 * whose interrupt controller, timer interrupt and real-time clock come from the tree (tree.c), used only by this
 * directory. The console is still the virt board's PL011 until H7.2 takes it from the tree's stdout-path. */

#define WIT_ARM_PL011_BASE 0x09000000ULL
#define WIT_ARM_GICR_STRIDE 0x20000ULL /* the two frames of each processor's GICv3 redistributor (K7.2) */
#define WIT_ARM_GICR_SGI_OFFSET 0x10000ULL

struct WitBootInfo;

/* The flattened device tree (tree.c): the header checked and the structure and strings blocks mapped once, then read
 * item by item. Each open node keeps its #address-cells, #size-cells and ranges, so that a reg is translated through
 * its ancestors' ranges into the processor's address space. */
#define WIT_ARM_TREE_DEPTH 8U
#define WIT_ARM_TREE_BEGIN 1U
#define WIT_ARM_TREE_END 2U
#define WIT_ARM_TREE_PROPERTY 3U

typedef struct WitArmTreeNode {
    WitU32 AddressCells, SizeCells, RangesLength, HasRanges;
    const WitU8 *Ranges;
} WitArmTreeNode;

typedef struct WitArmTree {
    const WitU8 *Base;
    WitU32 Offset, Limit, Strings, StringsLimit, Depth;
    WitArmTreeNode Nodes[WIT_ARM_TREE_DEPTH + 1]; /* by depth; the root node is depth 1 */
} WitArmTree;

/* One item: a node's beginning (Name its name) or end, or a property (Name, Value and Length) of the node at Depth. */
typedef struct WitArmTreeItem {
    WitU32 Kind, Depth, Length;
    const char *Name;
    const WitU8 *Value;
} WitArmTreeItem;

int wit_arm_tree_open(const struct WitBootInfo *boot, WitArmTree *tree);
int wit_arm_tree_next(WitArmTree *tree, WitArmTreeItem *item);
/* The index-th region of a reg property of the node at the tree's depth, in the processor's address space; zero when
 * the property is shorter or an ancestor's ranges do not translate it. */
int wit_arm_tree_reg(const WitArmTree *tree, const WitArmTreeItem *item, WitU32 index, WitU64 *base, WitU64 *size);
/* Whether a string list property (compatible) holds the word. */
int wit_arm_tree_names(const WitArmTreeItem *item, const char *word);
/* Maps a page for reading once: the tree's pages and the pages a walk reads (an ECAM window). */
void wit_arm_map_once(const struct WitBootInfo *boot, WitU64 page);
WitU32 wit_arm_be32(const WitU8 *p);
WitU64 wit_arm_be64(const WitU8 *p);
int wit_arm_same(const char *a, const char *b);

/* What the tree says of the board (tree.c), read once after paging is active. */
typedef struct WitArmBoard {
    WitU32 GicVersion; /* 2 or 3; zero when the tree names no GIC the platform drives */
    WitU64 Distributor; /* GICD */
    WitU64 CpuInterface; /* GICv2's GICC */
    WitU64 Redistributors; /* GICv3's first redistributor region, the boot processor's frames first */
    WitU32 TimerInterrupt; /* the EL1 virtual timer's INTID */
    WitU64 RealTimeClock; /* a PL031's base; zero when the board has none */
    int Read;
} WitArmBoard;

const WitArmBoard *wit_arm_board(const struct WitBootInfo *boot);

/* Arm semihosting call (HLT #0xF000); QEMU serves it when started with -semihosting-config. */
WitU64 wit_arm_semihosting(WitU64 operation, const void *parameter);

/* Generic counter (CNTPCT_EL0, read after an ISB) and its frequency (CNTFRQ_EL0). */
WitU64 wit_arm_counter(void);
WitU64 wit_arm_counter_frequency(void);

/* Maps the GIC's pages and prints what the tree named; the clock does it once paging is active. */
void wit_arm_interrupts_map(const struct WitBootInfo *boot);

/* GICv3 CPU interface through its system registers (gic.S). */
void wit_arm_gic3_cpu_enable(void);
/* ICC_SGI1R_EL1: a software-generated interrupt to the processors the value names (K7.2). */
void wit_arm_gic3_send_sgi(WitU64 value);
WitU64 wit_arm_gic3_acknowledge(void);
void wit_arm_gic3_complete(WitU64 interrupt);
/* GICv2 (H7.1): a write of GICD_SGIR after the barrier that makes the sender's earlier writes visible (gic.S). */
void wit_arm_gic2_send_sgi(WitU64 sgir, WitU32 value);
/* The EL1 virtual timer (gic.S). */
void wit_arm_timer_arm(WitU64 ticks);
void wit_arm_timer_disable(void);

#endif
