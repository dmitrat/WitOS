option casemap:none
include user_abi.inc
; User-space x64 ABI facade only; no Windows implementation or kernel syscall.
.code
PUBLIC GetLastError
PUBLIC wit_native_error_get
GetLastError PROC
wit_native_error_get LABEL NEAR
    mov eax, DWORD PTR fs:[WIT_TLS_LAST_ERROR_OFFSET]
    ret
GetLastError ENDP
PUBLIC SetLastError
PUBLIC wit_native_error_set
SetLastError PROC
wit_native_error_set LABEL NEAR
    mov DWORD PTR fs:[WIT_TLS_LAST_ERROR_OFFSET], ecx
    ret
SetLastError ENDP
.const
ALIGN 8
PUBLIC __imp_GetLastError
PUBLIC __imp_SetLastError
__imp_GetLastError QWORD GetLastError
__imp_SetLastError QWORD SetLastError
END
