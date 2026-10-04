; QEMU virt generic counter, AAPCS64. The ISB keeps the read from being taken early.

    AREA |.text|, CODE, READONLY

    EXPORT wit_virt_counter
    EXPORT wit_virt_counter_frequency

wit_virt_counter PROC
    isb
    mrs x0, cntpct_el0
    ret
    ENDP

wit_virt_counter_frequency PROC
    mrs x0, cntfrq_el0
    ret
    ENDP

    END
