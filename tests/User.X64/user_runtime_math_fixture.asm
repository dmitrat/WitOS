option casemap:none
.code
PUBLIC wit_math_fp_state
wit_math_fp_state PROC
    sub rsp, 8
    stmxcsr DWORD PTR [rsp]
    mov eax, DWORD PTR [rsp]
    add rsp, 8
    ret
wit_math_fp_state ENDP
PUBLIC wit_math_clear_fp_flags
wit_math_clear_fp_flags PROC
    sub rsp, 8
    stmxcsr DWORD PTR [rsp]
    and DWORD PTR [rsp], 0FFFFFFC0h
    ldmxcsr DWORD PTR [rsp]
    add rsp, 8
    ret
wit_math_clear_fp_flags ENDP
PUBLIC wit_math_set_rounding
wit_math_set_rounding PROC
    sub rsp, 8
    stmxcsr DWORD PTR [rsp]
    and DWORD PTR [rsp], 0FFFF9FFFh
    and ecx, 3
    shl ecx, 13
    or DWORD PTR [rsp], ecx
    ldmxcsr DWORD PTR [rsp]
    add rsp, 8
    ret
wit_math_set_rounding ENDP
END
