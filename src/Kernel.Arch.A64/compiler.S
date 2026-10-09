// The kernel's compiler helpers, AAPCS64 (plan step T3.2). clang calls memcpy and memset for structure copies
// and initialization, and __chkstk for frames larger than a page. They are assembly so that no C body can be
// lowered back into a call to itself.

    .text

    .globl __chkstk
    .globl memcpy
    .globl memset

// x15 = allocation size in 16-byte units. Touches every page of the allocation below SP, one 4 KiB step at a
// time and then the final partial span, so a guard page faults before the caller moves SP past it. Preserves
// every register except x16, x17 and the flags and does not change SP.
__chkstk:
    lsl x16, x15, #4
    mov x17, sp
probe_page:
    cmp x16, #1, lsl #12
    b.lo probe_tail
    sub x17, x17, #1, lsl #12
    ldr xzr, [x17]
    sub x16, x16, #1, lsl #12
    b probe_page
probe_tail:
    cbz x16, probe_done
    sub x17, x17, x16
    ldr xzr, [x17]
probe_done:
    ret

// x0 = destination, x1 = source, x2 = count; returns the destination. The ranges must not overlap.
memcpy:
    mov x3, x0
    cbz x2, copy_done
copy_byte:
    ldrb w4, [x1], #1
    strb w4, [x3], #1
    subs x2, x2, #1
    b.ne copy_byte
copy_done:
    ret

// x0 = destination, w1 = byte value, x2 = count; returns the destination.
memset:
    mov x3, x0
    cbz x2, set_done
set_byte:
    strb w1, [x3], #1
    subs x2, x2, #1
    b.ne set_byte
set_done:
    ret
