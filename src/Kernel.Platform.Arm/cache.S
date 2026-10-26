// The data cache's clean and invalidation of a buffer a device reads and writes from memory (H7.3: the VideoCore's
// mailbox message), AAPCS64.

    .text

    .globl wit_arm_clean_invalidate

// x0 = address, x1 = bytes: every data cache line the range touches, cleaned and invalidated to the point of coherency.
wit_arm_clean_invalidate:
    mrs x3, ctr_el0
    ubfx x3, x3, #16, #4                    // DminLine: log2 of the smallest line in words
    mov x2, #4
    lsl x2, x2, x3
    add x1, x0, x1
    sub x3, x2, #1
    bic x0, x0, x3
1:
    dc civac, x0
    add x0, x0, x2
    cmp x0, x1
    b.lo 1b
    dsb sy
    ret
