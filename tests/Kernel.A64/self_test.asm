; ARM64 fault triggers of the kernel self-tests, AAPCS64: each raises one synchronous exception at EL1.

    AREA |.text|, CODE, READONLY

    EXPORT wit_a64_trigger_breakpoint
    EXPORT wit_a64_trigger_undefined
    EXPORT wit_a64_trigger_data_abort

wit_a64_trigger_breakpoint PROC
    brk #0
    ret
    ENDP

wit_a64_trigger_undefined PROC
    DCD 0x00000000 ; UDF #0, which armasm64 does not name
    ret
    ENDP

; x0 = address that the translation tables leave unmapped.
wit_a64_trigger_data_abort PROC
    ldr x0, [x0]
    ret
    ENDP

    END
