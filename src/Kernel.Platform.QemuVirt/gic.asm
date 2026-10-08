; QEMU virt GICv3 CPU interface and EL1 virtual timer, AAPCS64.

    AREA |.text|, CODE, READONLY

    EXPORT wit_virt_gic_cpu_enable
    EXPORT wit_virt_gic_acknowledge
    EXPORT wit_virt_gic_send_sgi
    EXPORT wit_virt_gic_complete
    EXPORT wit_virt_timer_arm
    EXPORT wit_virt_timer_disable

; System-register interface, all priorities unmasked, group 1 enabled.
wit_virt_gic_cpu_enable PROC
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
    ENDP

; Returns the INTID of the highest-priority pending group 1 interrupt.
wit_virt_gic_send_sgi PROC
    dsb ishst
    msr ICC_SGI1R_EL1, x0
    isb
    ret
    ENDP

wit_virt_gic_acknowledge PROC
    mrs x0, ICC_IAR1_EL1
    dsb sy
    ret
    ENDP

; x0 = INTID.
wit_virt_gic_complete PROC
    msr ICC_EOIR1_EL1, x0
    isb
    ret
    ENDP

; x0 = counter ticks until the next interrupt; enables the timer unmasked.
wit_virt_timer_arm PROC
    msr CNTV_TVAL_EL0, x0
    mov x0, #1
    msr CNTV_CTL_EL0, x0
    isb
    ret
    ENDP

wit_virt_timer_disable PROC
    msr CNTV_CTL_EL0, xzr
    isb
    ret
    ENDP

    END
