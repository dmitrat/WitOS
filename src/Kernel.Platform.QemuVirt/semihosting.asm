; Arm semihosting on AArch64: operation in w0, parameter block in x1, result in x0.

    AREA |.text|, CODE, READONLY

    EXPORT wit_virt_semihosting

wit_virt_semihosting PROC
    hlt #0xf000
    ret
    ENDP

    END
