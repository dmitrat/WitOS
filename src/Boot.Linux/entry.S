// The Linux arm64 boot protocol's entry (plan step H6, ADR 0030; Documentation/arch/arm64/booting.rst). The image
// tool writes the protocol's header over the PE image's DOS header: its first instruction, whose bytes are also "MZ",
// is a NOP, and its second branches here. A bootloader enters at EL2 or EL1 with the MMU and the data cache off,
// interrupts masked and the device tree's physical address in x0. The entry leaves EL2 for EL1, opens the
// floating-point unit, maps the gigabytes of the image and of the device tree as normal memory and every other one as
// device memory, turns the MMU and the caches on, applies the image's base relocations where it was loaded and calls
// the boot layer's C with the tree. Nothing here reads an absolute address before the relocations: every reference is
// PC-relative (adrp/add).

    .equ SCTLR_EL1_RES1, 0x30D00800        // RES1 bits 11, 20, 22, 23, 28, 29; EE, WXN and the MMU off
    .equ SCTLR_M, 1
    .equ SCTLR_C, 1 << 2
    .equ SCTLR_SA, 1 << 3
    .equ SCTLR_I, 1 << 12
    .equ HCR_RW, 1 << 31                    // EL1 is AArch64
    .equ CPTR_EL2_RES1, 0x33FF              // no trap of the floating-point unit
    .equ SPSR_EL1H_MASKED, 0x3C5            // EL1h, DAIF masked
    .equ ICC_SRE_ENABLE, 0x9                // SRE and Enable: EL1 reaches the GICv3 CPU interface's registers
    .equ CPACR_FPEN, 3 << 20

    // Translation of the first stage: 4 KiB granule, 48-bit addresses from level 0, whose first entry is a level-1 table
    // of gigabyte blocks. It is the kernel's geometry: the kernel writes TCR before TTBR0 when it installs its own tables
    // (wit_a64_install_tables), and QEMU drops its TLB at the TCR write, so the walk between the two writes reads this
    // table as a level-0 one.
    .equ BLOCK_NORMAL, 0x701                // block, AttrIndx 0, inner shareable, AF
    .equ BLOCK_DEVICE, 0x0060000000000405   // block, AttrIndx 1, AF, UXN, PXN
    .equ MAIR_ATTRIBUTES, 0x00FF            // Attr0 normal write-back, Attr1 device-nGnRnE
    .equ TCR_FIRST, 16 | (1 << 8) | (1 << 10) | (3 << 12) | (1 << 23) // T0SZ 16, WB walks, inner shareable, EPD1
    .equ TABLE, 3

    .bss
    .p2align 12
wit_linux_level0:
    .space 4096
wit_linux_level1:
    .space 4096
    .p2align 4
wit_linux_stack:
    .space 32768
wit_linux_stack_top:

    .text
    .globl wit_linux_entry
    .globl wit_linux_switch_tables

wit_linux_entry:
    mov x19, x0                             // the device tree
    mrs x20, CurrentEL
    lsr x20, x20, #2                        // the level the bootloader entered at
    msr daifset, #0xf
    cmp x20, #2
    b.ne at_el1

    // EL2: AArch64 at EL1, the counters and the floating-point unit untrapped, no stage 2, the GICv3 CPU interface's
    // registers open to EL1 where the processor has them; then an exception return to EL1 with the MMU off.
    mov x9, #HCR_RW
    msr hcr_el2, x9
    mrs x9, cnthctl_el2
    orr x9, x9, #3                          // EL1PCTEN and EL1PCEN
    msr cnthctl_el2, x9
    msr cntvoff_el2, xzr
    mov x9, #CPTR_EL2_RES1
    msr cptr_el2, x9
    msr hstr_el2, xzr
    msr vttbr_el2, xzr
    mrs x9, pmcr_el0
    ubfx x9, x9, #11, #5                    // HPMN: every counter EL1's, no debug or monitor trap
    msr mdcr_el2, x9
    mrs x9, id_aa64pfr0_el1
    ubfx x9, x9, #24, #4                    // GIC: the system register interface
    cbz x9, 1f
    mrs x9, s3_4_c12_c9_5                   // ICC_SRE_EL2
    mov x10, #ICC_SRE_ENABLE
    orr x9, x9, x10
    msr s3_4_c12_c9_5, x9
    isb
1:
    ldr x9, =SCTLR_EL1_RES1
    msr sctlr_el1, x9
    mov x9, #SPSR_EL1H_MASKED
    msr spsr_el2, x9
    adr x9, at_el1
    msr elr_el2, x9
    eret

at_el1:
    mov x9, #CPACR_FPEN
    msr cpacr_el1, x9
    isb

    // The first stage's table: device memory everywhere but the gigabytes of the image (its first and its last byte)
    // and of the device tree.
    adrp x0, wit_linux_level1
    add x0, x0, :lo12:wit_linux_level1
    adrp x9, wit_linux_level0
    add x9, x9, :lo12:wit_linux_level0
    orr x10, x0, #TABLE
    str x10, [x9]
    ldr x1, =BLOCK_DEVICE
    mov x2, #0
2:
    orr x3, x1, x2, lsl #30
    str x3, [x0, x2, lsl #3]
    add x2, x2, #1
    cmp x2, #512
    b.lo 2b
    mov x6, #BLOCK_NORMAL
    adrp x7, __ImageBase
    add x7, x7, :lo12:__ImageBase
    lsr x2, x7, #30
    lsl x3, x2, #30
    orr x3, x3, x6
    str x3, [x0, x2, lsl #3]
    ldr w8, [x7, #0x3C]                     // e_lfanew
    add x8, x7, x8
    ldr w8, [x8, #80]                       // SizeOfImage: the optional header's word at 56
    add x8, x7, x8
    sub x8, x8, #1
    lsr x2, x8, #30
    lsl x3, x2, #30
    orr x3, x3, x6
    str x3, [x0, x2, lsl #3]
    lsr x2, x19, #30
    lsl x3, x2, #30
    orr x3, x3, x6
    str x3, [x0, x2, lsl #3]

    // Written with the data cache off: the tables' lines go from the cache before the walker reads them cacheably.
    mrs x3, ctr_el0
    ubfx x3, x3, #16, #4                    // DminLine: log2 of the words of the smallest data cache line
    mov x4, #4
    lsl x3, x4, x3
    mov x4, x9
    add x5, x9, #8192
3:
    dc ivac, x4
    add x4, x4, x3
    cmp x4, x5
    b.lo 3b
    dsb sy

    mov x1, #MAIR_ATTRIBUTES
    msr mair_el1, x1
    mrs x1, id_aa64mmfr0_el1
    and x1, x1, #0xF                        // PARange
    cmp x1, #5
    mov x2, #5
    csel x1, x1, x2, ls
    ldr x2, =TCR_FIRST
    orr x2, x2, x1, lsl #32                 // IPS
    msr tcr_el1, x2
    msr ttbr0_el1, x9
    isb
    tlbi vmalle1
    dsb ish
    isb
    mrs x1, sctlr_el1
    ldr x2, =(SCTLR_M | SCTLR_C | SCTLR_SA | SCTLR_I)
    orr x1, x1, x2
    bic x1, x1, #(1 << 1)                   // no alignment checks
    bic x1, x1, #(1 << 19)                  // WXN off: the kernel installs its own tables
    msr sctlr_el1, x1
    isb
    ic iallu
    dsb nsh
    isb

    adrp x0, wit_linux_stack_top
    add x0, x0, :lo12:wit_linux_stack_top
    mov sp, x0
    mov x29, #0
    adrp x0, __ImageBase
    add x0, x0, :lo12:__ImageBase
    bl relocate
    mov x0, x19
    mov x1, x20
    bl wit_linux_main
halt:
    wfi
    b halt

// x0 = the image's base where it was loaded. The PE optional header names the preferred base and the base relocation
// directory; every IMAGE_REL_BASED_DIR64 entry gets the difference, IMAGE_REL_BASED_ABSOLUTE pads a block, and any
// other type stops the walk, which wit_linux_main reports through wit_linux_relocations.
relocate:
    ldr w1, [x0, #0x3C]                     // e_lfanew; the image tool requires it 8-aligned
    add x1, x0, x1
    add x2, x1, #24                         // the optional header (PE32+)
    ldr x3, [x2, #24]                       // ImageBase, as linked
    sub x3, x0, x3                          // the difference
    ldr w4, [x2, #152]                      // the base relocation directory: its address
    ldr w5, [x2, #156]                      // and its bytes
    mov x14, #0                             // entries applied
    adrp x15, wit_linux_relocations
    add x15, x15, :lo12:wit_linux_relocations
    cbz w5, 9f
    add x4, x0, x4
    add x5, x4, x5
4:
    cmp x4, x5
    b.hs 9f
    ldr w6, [x4]                            // the block's page
    ldr w7, [x4, #4]                        // the block's bytes
    cmp w7, #8
    b.lo 8f
    add x8, x4, #8
    add x9, x4, x7
5:
    cmp x8, x9
    b.hs 7f
    ldrh w10, [x8], #2
    lsr w11, w10, #12
    and w10, w10, #0xFFF
    cbz w11, 5b                             // IMAGE_REL_BASED_ABSOLUTE
    cmp w11, #10                            // IMAGE_REL_BASED_DIR64
    b.ne 8f
    add x12, x0, x6
    add x12, x12, x10
    ldr x13, [x12]
    add x13, x13, x3
    str x13, [x12]
    add x14, x14, #1
    b 5b
7:
    mov x4, x9
    b 4b
8:
    mov x14, #-1                            // a block or an entry the walk does not take
9:
    str x14, [x15]
    ret

// x0 = a level-0 table to translate by from here: its stores reach the walker, the base changes and every earlier
// translation goes.
wit_linux_switch_tables:
    dsb ishst
    msr ttbr0_el1, x0
    isb
    tlbi vmalle1
    dsb ish
    isb
    ret

    .data
    .p2align 3
    .globl wit_linux_relocations
wit_linux_relocations:
    .quad 0
