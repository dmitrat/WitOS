// QEMU virt GICv3 CPU interface and EL1 virtual timer, AAPCS64.

    .text

    .globl wit_virt_gic_cpu_enable
    .globl wit_virt_gic_acknowledge
    .globl wit_virt_gic_send_sgi
    .globl wit_virt_gic_complete
    .globl wit_virt_timer_arm
    .globl wit_virt_timer_disable

// System-register interface, all priorities unmasked, group 1 enabled.
wit_virt_gic_cpu_enable:
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

// Returns the INTID of the highest-priority pending group 1 interrupt.
wit_virt_gic_send_sgi:
    dsb ishst
    msr ICC_SGI1R_EL1, x0
    isb
    ret

wit_virt_gic_acknowledge:
    mrs x0, ICC_IAR1_EL1
    dsb sy
    ret

// x0 = INTID.
wit_virt_gic_complete:
    msr ICC_EOIR1_EL1, x0
    isb
    ret

// x0 = counter ticks until the next interrupt; enables the timer unmasked.
wit_virt_timer_arm:
    msr CNTV_TVAL_EL0, x0
    mov x0, #1
    msr CNTV_CTL_EL0, x0
    isb
    ret

wit_virt_timer_disable:
    msr CNTV_CTL_EL0, xzr
    isb
    ret
