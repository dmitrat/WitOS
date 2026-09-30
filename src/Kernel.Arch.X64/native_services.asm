option casemap:none
EXTERN wit_native_close_handle:PROC
EXTERN wit_native_sleep:PROC
.code
PUBLIC CloseHandle
CloseHandle PROC
    jmp wit_native_close_handle
CloseHandle ENDP
PUBLIC Sleep
Sleep PROC
    jmp wit_native_sleep
Sleep ENDP
.const
ALIGN 8
PUBLIC __imp_CloseHandle
PUBLIC __imp_Sleep
__imp_CloseHandle DQ CloseHandle
__imp_Sleep DQ Sleep
END
