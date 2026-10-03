; ARM64 exception vectors (VBAR_EL1), AAPCS64. Each entry saves x0 and x1, passes its index in x0 and joins the
; common path, which stores a WitA64Frame on the interrupted stack and calls wit_a64_exception. Every exception
; is fatal until the timer interrupt arrives with A1.3.

FRAME_SIZE EQU 288

    AREA |.text|, CODE, READONLY, ALIGN=11

    EXPORT wit_a64_vectors
    IMPORT wit_a64_exception

    MACRO
    VECTOR $index
    sub sp, sp, #FRAME_SIZE
    stp x0, x1, [sp]
    mov x0, #$index
    b save_frame
    ALIGN 128
    MEND

; Current EL with SP_EL0, current EL with SP_ELx, lower EL AArch64, lower EL AArch32;
; synchronous, IRQ, FIQ and SError in each group.
wit_a64_vectors
    VECTOR 0
    VECTOR 1
    VECTOR 2
    VECTOR 3
    VECTOR 4
    VECTOR 5
    VECTOR 6
    VECTOR 7
    VECTOR 8
    VECTOR 9
    VECTOR 10
    VECTOR 11
    VECTOR 12
    VECTOR 13
    VECTOR 14
    VECTOR 15

; x0 = vector index; x0 and x1 of the interrupted code are at [sp].
save_frame
    stp x2, x3, [sp, #16]
    stp x4, x5, [sp, #32]
    stp x6, x7, [sp, #48]
    stp x8, x9, [sp, #64]
    stp x10, x11, [sp, #80]
    stp x12, x13, [sp, #96]
    stp x14, x15, [sp, #112]
    stp x16, x17, [sp, #128]
    stp x18, x19, [sp, #144]
    stp x20, x21, [sp, #160]
    stp x22, x23, [sp, #176]
    stp x24, x25, [sp, #192]
    stp x26, x27, [sp, #208]
    stp x28, x29, [sp, #224]
    str x30, [sp, #240]
    add x2, sp, #FRAME_SIZE
    mrs x3, elr_el1
    stp x2, x3, [sp, #248]
    mrs x4, spsr_el1
    mrs x5, esr_el1
    stp x4, x5, [sp, #264]
    mrs x6, far_el1
    str x6, [sp, #280]
    mov x1, x0
    mov x0, sp
    bl wit_a64_exception
halt_after_exception
    wfi
    b halt_after_exception

    END
