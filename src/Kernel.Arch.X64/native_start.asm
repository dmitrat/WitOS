option casemap:none
include user_abi.inc
EXTERN wit_native_main:PROC
; This object is linked into the USER image, not the kernel.
.code
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

PUBLIC wit_native_claim_startup
wit_native_claim_startup PROC
    xor eax, eax
    mov edx, 1
    lock cmpxchg DWORD PTR [rcx], edx
    sete al
    movzx eax, al
    ret
wit_native_claim_startup ENDP

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
