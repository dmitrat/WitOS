option casemap:none
EXTERN __security_cookie:QWORD
EXTERN wit_native_security_state:DWORD
EXTERN wit_native_security_failure:PROC
.code
PUBLIC __security_check_cookie
__security_check_cookie PROC
    ; Special compiler ABI: success must preserve return/argument registers.
    cmp DWORD PTR [wit_native_security_state], 2
    jne bad_cookie
    cmp rcx, QWORD PTR [__security_cookie]
    jne bad_cookie
    rol rcx, 16
    test cx, 0FFFFh
    jne restore_bad_cookie
    ret
restore_bad_cookie:
    ror rcx, 16
bad_cookie:
    jmp wit_native_security_failure
__security_check_cookie ENDP
PUBLIC __report_gsfailure
__report_gsfailure PROC
    jmp wit_native_security_failure
__report_gsfailure ENDP
END
