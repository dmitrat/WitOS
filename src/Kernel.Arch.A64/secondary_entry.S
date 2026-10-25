// Secondary processors of ARM64 (plan steps K7.2, H7.2): their entries with the MMU off, at EL2 or EL1, the way out of
// EL2 that the Linux boot protocol's entry shares, the PSCI calls through either conduit, and the reads of the boot
// processor's translation registers a started processor copies.
    .text
    .globl wit_a64_leave_el2
    .globl wit_a64_psci_hvc
    .globl wit_a64_psci_smc
    .globl wit_a64_send_event
    .globl wit_a64_secondary_entry
    .globl wit_a64_secondary_spin_entry
    .globl wit_a64_translation_control
    .globl wit_a64_memory_attributes
    .globl wit_a64_translation_base_high

    .equ SCTLR_EL1_RES1, 0x30D00800         // RES1 bits 11, 20, 22, 23, 28, 29; EE, WXN and the MMU off
    .equ HCR_RW, 1 << 31                    // EL1 is AArch64
    .equ CPTR_EL2_RES1, 0x33FF              // no trap of the floating-point unit
    .equ SPSR_EL1H_MASKED, 0x3C5            // EL1h, DAIF masked
    .equ ICC_SRE_ENABLE, 0x9                // SRE and Enable: EL1 reaches the GICv3 CPU interface's registers
    .equ CURRENT_EL2, 2 << 2
    .equ AFFINITY_MASK, 0xFF00FFFFFF        // Aff3 and Aff2:Aff1:Aff0 of MPIDR_EL1
    .equ PROCESSOR_CAPACITY, 8              // WIT_PROCESSOR_CAPACITY

// Leaves EL2 for EL1 when the processor runs at EL2 (H6, H7.2): AArch64 at EL1, the counters and the floating-point
// unit untrapped, no stage 2, no trap of the debug and monitor registers or of CP15, the GICv3 CPU interface's
// registers open to EL1 where the processor has them, SCTLR_EL1's RES1 bits with the MMU off; then it returns to x30
// at EL1h with DAIF masked and SP_EL1 unset. At EL1 it returns at once. It clobbers x9 and x10 and addresses nothing
// absolute, so a boot stage calls it before its relocations.
wit_a64_leave_el2:
    mrs x9, CurrentEL
    cmp x9, #CURRENT_EL2
    b.ne 2f
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
    msr elr_el2, x30
    eret
2:
    ret

// x0 = a PSCI function, x1-x3 = its arguments -> x0 its result, through HVC or SMC (witos/a64_board.h).
wit_a64_psci_hvc:
    hvc #0
    ret

wit_a64_psci_smc:
    smc #0
    ret

wit_a64_send_event:
    dsb sy
    sev
    ret

// PSCI's entry: x0 = the kernel's number of the processor (the CPU_ON context).
wit_a64_secondary_entry:
    msr daifset, #0xf
    mov x19, x0
    bl wit_a64_leave_el2
    b secondary_common

// A spin table's entry (H7.2): the processor's number is the index whose prepared identity is its MPIDR's affinity;
// one that finds none waits for events for good.
wit_a64_secondary_spin_entry:
    msr daifset, #0xf
    bl wit_a64_leave_el2
    mrs x1, mpidr_el1
    ldr x2, =AFFINITY_MASK
    and x1, x1, x2
    ldr x3, =wit_a64_secondary_ids
    mov x19, #1
3:
    cmp x19, #PROCESSOR_CAPACITY
    b.hs 4f
    ldr x4, [x3, x19, lsl #3]
    cmp x4, x1
    b.eq secondary_common
    add x19, x19, #1
    b 3b
4:
    wfe
    b 4b

// A started processor at EL1, x19 its number: it copies the boot processor's MAIR, TCR and both table bases, turns
// translation on with the boot processor's SCTLR (the kernel is identity mapped, so the next instruction is where it
// was), opens FP/SIMD access as the boot processor did (the vectors save the q registers), installs the vectors, takes
// its own stack and enters C; the main function never returns.
secondary_common:
    ldr x1, =wit_a64_boot_mair
    ldr x1, [x1]
    msr mair_el1, x1
    ldr x1, =wit_a64_boot_tcr
    ldr x1, [x1]
    msr tcr_el1, x1
    ldr x1, =wit_a64_boot_ttbr0
    ldr x1, [x1]
    msr ttbr0_el1, x1
    ldr x1, =wit_a64_boot_ttbr1
    ldr x1, [x1]
    msr ttbr1_el1, x1
    isb
    tlbi vmalle1
    dsb ish
    isb
    ldr x1, =wit_a64_boot_sctlr
    ldr x1, [x1]
    msr sctlr_el1, x1
    isb
    ldr x1, =wit_a64_boot_cpacr
    ldr x1, [x1]
    msr cpacr_el1, x1
    isb
    ldr x1, =wit_a64_vectors
    msr vbar_el1, x1
    isb
    msr tpidrro_el0, xzr
    msr tpidr_el0, xzr
    ldr x1, =wit_a64_secondary_stack_tops
    ldr x1, [x1, x19, lsl #3]
    mov sp, x1
    mov x29, #0
    mov x30, #0
    mov x0, x19
    bl wit_a64_secondary_main
secondary_halt:
    wfi
    b secondary_halt

wit_a64_translation_control:
    mrs x0, tcr_el1
    ret

wit_a64_memory_attributes:
    mrs x0, mair_el1
    ret

wit_a64_translation_base_high:
    mrs x0, ttbr1_el1
    ret
    .ltorg
