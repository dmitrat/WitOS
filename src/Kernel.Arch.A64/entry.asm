; ARM64 bring-up instructions, AAPCS64. Interrupts stay masked until A1 installs exception vectors.

    AREA |.text|, CODE, READONLY

    EXPORT wit_a64_call_on_stack
    EXPORT wit_a64_mask_interrupts
    EXPORT wit_a64_wait
    EXPORT wit_a64_exception_level

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

    END
