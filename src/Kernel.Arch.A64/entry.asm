; ARM64 bring-up instructions, AAPCS64. Interrupts stay masked until the timer arrives with A1.3.

    AREA |.text|, CODE, READONLY

    EXPORT wit_a64_call_on_stack
    EXPORT wit_a64_mask_interrupts
    EXPORT wit_a64_wait
    EXPORT wit_a64_exception_level
    EXPORT wit_a64_stack_pointer
    EXPORT wit_a64_set_vectors
    EXPORT wit_a64_vectors_base

; x0 = argument, x1 = stack top, x2 = function that never returns.
wit_a64_call_on_stack PROC
    msr daifset, #0xf
    mov sp, x1
    mov x29, #0
    mov x30, #0
    blr x2
halt_forever
    wfi
    b halt_forever
    ENDP

wit_a64_mask_interrupts PROC
    msr daifset, #0xf
    ret
    ENDP

wit_a64_wait PROC
    wfi
    ret
    ENDP

wit_a64_exception_level PROC
    mrs x0, CurrentEL
    ubfx x0, x0, #2, #2
    ret
    ENDP

wit_a64_stack_pointer PROC
    mov x0, sp
    ret
    ENDP

; x0 = vector table, 2 KiB aligned.
wit_a64_set_vectors PROC
    msr vbar_el1, x0
    isb
    ret
    ENDP

wit_a64_vectors_base PROC
    mrs x0, vbar_el1
    ret
    ENDP

    END
