option casemap:none
.code
PUBLIC wit_native_fp_rounding
wit_native_fp_rounding PROC
    sub rsp, 8
    stmxcsr DWORD PTR [rsp]
    mov eax, DWORD PTR [rsp]
    shr eax, 13
    and eax, 3
    add rsp, 8
    ret
wit_native_fp_rounding ENDP
END
