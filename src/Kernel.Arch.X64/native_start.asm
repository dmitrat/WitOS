option casemap:none
include user_abi.inc
; This object is linked into the USER image, not the kernel.
.code
IFNDEF WITOS_NATIVE_TRANSPORT_ONLY
EXTERN wit_native_main:PROC
PUBLIC wit_native_start
wit_native_start PROC FRAME
    sub rsp, 40
    .allocstack 40
    .endprolog
    call wit_native_main
    add rsp, 40
    mov rcx, rax
    mov eax, WIT_CALL_EXIT
    int 80h
    ud2
wit_native_start ENDP
ENDIF

PUBLIC wit_native_claim_startup
wit_native_claim_startup PROC
    xor eax, eax
    mov edx, 1
    lock cmpxchg DWORD PTR [rcx], edx
    sete al
    movzx eax, al
    ret
wit_native_claim_startup ENDP

PUBLIC wit_native_try_lock
wit_native_try_lock PROC
    xor eax, eax
    mov edx, 1
    lock cmpxchg DWORD PTR [rcx], edx
    sete al
    movzx eax, al
    ret
wit_native_try_lock ENDP

PUBLIC wit_native_unlock
wit_native_unlock PROC
    ; Release ordering for prior stores on x64 TSO; external call is the
    ; MSVC compiler boundary. No AVX/XSAVE or segment-based TLS is involved.
    mov DWORD PTR [rcx], 0
    ret
wit_native_unlock ENDP

PUBLIC wit_native_fail_fast
wit_native_fail_fast PROC
    mov eax, WIT_CALL_EXIT
    int 80h
    ud2
wit_native_fail_fast ENDP

PUBLIC wit_native_call
wit_native_call PROC
    mov rax, rcx
    mov rcx, rdx
    mov rdx, r8
    mov r8, r9
    int 80h
    mov r10, [rsp + 40]
    test r10, r10
    je done
    mov [r10], rdx
done:
    ret
wit_native_call ENDP
END
