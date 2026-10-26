// The GIC's CPU interface and the EL1 virtual timer, AAPCS64: GICv3's system registers, and GICv2's SGI write with
// its barrier (H7.1).

    .text

    .globl wit_arm_gic3_cpu_enable
    .globl wit_arm_gic3_acknowledge
    .globl wit_arm_gic3_send_sgi
    .globl wit_arm_gic3_complete
    .globl wit_arm_gic2_send_sgi
    .globl wit_arm_timer_arm
    .globl wit_arm_timer_disable

// System-register interface, all priorities unmasked, group 1 enabled.
wit_arm_gic3_cpu_enable:
    mrs x0, ICC_SRE_EL1
    orr x0, x0, #1
    msr ICC_SRE_EL1, x0
    isb
    mov x0, #0xFF
    msr ICC_PMR_EL1, x0
    msr ICC_BPR1_EL1, xzr
    mov x0, #1
    msr ICC_IGRPEN1_EL1, x0
    isb
    ret

// x0 = the ICC_SGI1R_EL1 value.
wit_arm_gic3_send_sgi:
    dsb ishst
    msr ICC_SGI1R_EL1, x0
    isb
    ret

// Returns the INTID of the highest-priority pending group 1 interrupt.
wit_arm_gic3_acknowledge:
    mrs x0, ICC_IAR1_EL1
    dsb sy
    ret

// x0 = INTID.
wit_arm_gic3_complete:
    msr ICC_EOIR1_EL1, x0
    isb
    ret

// x0 = GICD_SGIR's address, w1 = its value: the target list and the SGI.
wit_arm_gic2_send_sgi:
    dsb ishst
    str w1, [x0]
    ret

// x0 = counter ticks until the next interrupt; enables the timer unmasked.
wit_arm_timer_arm:
    msr CNTV_TVAL_EL0, x0
    mov x0, #1
    msr CNTV_CTL_EL0, x0
    isb
    ret

wit_arm_timer_disable:
    msr CNTV_CTL_EL0, xzr
    isb
    ret
