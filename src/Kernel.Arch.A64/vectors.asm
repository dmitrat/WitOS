; ARM64 exception vectors (VBAR_EL1), AAPCS64. Each entry saves x0 and x1, passes its index in x0 and joins the
; common path, which stores a WitA64Frame on the interrupted stack (general, special and SIMD registers) and calls
; wit_a64_exception. The handler returns the frame to resume, possibly on another kernel thread's stack; the
; frame's own stack pointer is its address plus FRAME_SIZE.

FRAME_SIZE EQU 816

    AREA |.text|, CODE, READONLY, ALIGN=11

    EXPORT wit_a64_vectors
    EXPORT wit_a64_resume_frame
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
    mrs x7, fpcr
    stp x6, x7, [sp, #280]
    mrs x8, fpsr
    str x8, [sp, #296]
    stp q0, q1, [sp, #304]
    stp q2, q3, [sp, #336]
    stp q4, q5, [sp, #368]
    stp q6, q7, [sp, #400]
    stp q8, q9, [sp, #432]
    stp q10, q11, [sp, #464]
    stp q12, q13, [sp, #496]
    stp q14, q15, [sp, #528]
    stp q16, q17, [sp, #560]
    stp q18, q19, [sp, #592]
    stp q20, q21, [sp, #624]
    stp q22, q23, [sp, #656]
    stp q24, q25, [sp, #688]
    stp q26, q27, [sp, #720]
    stp q28, q29, [sp, #752]
    stp q30, q31, [sp, #784]
    mov x1, x0
    mov x0, sp
    bl wit_a64_exception

; x0 = frame to resume; it is never the caller's own frame once a stack switch happened.
wit_a64_resume_frame
    mov sp, x0
    ldp q0, q1, [sp, #304]
    ldp q2, q3, [sp, #336]
    ldp q4, q5, [sp, #368]
    ldp q6, q7, [sp, #400]
    ldp q8, q9, [sp, #432]
    ldp q10, q11, [sp, #464]
    ldp q12, q13, [sp, #496]
    ldp q14, q15, [sp, #528]
    ldp q16, q17, [sp, #560]
    ldp q18, q19, [sp, #592]
    ldp q20, q21, [sp, #624]
    ldp q22, q23, [sp, #656]
    ldp q24, q25, [sp, #688]
    ldp q26, q27, [sp, #720]
    ldp q28, q29, [sp, #752]
    ldp q30, q31, [sp, #784]
    ldr x2, [sp, #288]
    msr fpcr, x2
    ldr x2, [sp, #296]
    msr fpsr, x2
    ldp x2, x3, [sp, #256]
    msr elr_el1, x2
    msr spsr_el1, x3
    ldp x2, x3, [sp, #16]
    ldp x4, x5, [sp, #32]
    ldp x6, x7, [sp, #48]
    ldp x8, x9, [sp, #64]
    ldp x10, x11, [sp, #80]
    ldp x12, x13, [sp, #96]
    ldp x14, x15, [sp, #112]
    ldp x16, x17, [sp, #128]
    ldp x18, x19, [sp, #144]
    ldp x20, x21, [sp, #160]
    ldp x22, x23, [sp, #176]
    ldp x24, x25, [sp, #192]
    ldp x26, x27, [sp, #208]
    ldp x28, x29, [sp, #224]
    ldr x30, [sp, #240]
    ldp x0, x1, [sp]
    add sp, sp, #FRAME_SIZE
    eret

    END
