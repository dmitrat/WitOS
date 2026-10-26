// The generic counter, AAPCS64. The ISB keeps the read from being taken early.

    .text

    .globl wit_arm_counter
    .globl wit_arm_counter_frequency

wit_arm_counter:
    isb
    mrs x0, cntpct_el0
    ret

wit_arm_counter_frequency:
    mrs x0, cntfrq_el0
    ret
