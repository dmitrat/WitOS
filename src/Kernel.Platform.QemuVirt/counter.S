// QEMU virt generic counter, AAPCS64. The ISB keeps the read from being taken early.

    .text

    .globl wit_virt_counter
    .globl wit_virt_counter_frequency

wit_virt_counter:
    isb
    mrs x0, cntpct_el0
    ret

wit_virt_counter_frequency:
    mrs x0, cntfrq_el0
    ret
