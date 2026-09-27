option casemap:none
include user_abi.inc
EXTERN wit_native_call:PROC
.code
PUBLIC wit_cpu_sse42
wit_cpu_sse42 PROC
    xor eax, eax
    mov ecx, 12345678h
    crc32 eax, ecx
    ret
wit_cpu_sse42 ENDP
PUBLIC wit_cpu_aes
wit_cpu_aes PROC FRAME
    sub rsp, 72
    .allocstack 72
    movdqu XMMWORD PTR [rsp + 48], xmm6
    .savexmm128 xmm6, 48
    .endprolog
    mov [rsp + 40], rcx
    mov eax, 1
    movq xmm0, rax
    movdqa xmm1, xmm0
    pclmulqdq xmm0, xmm1, 0
    movq rax, xmm0
    cmp rax, 1
    jne failed
    movq xmm0, QWORD PTR [rsp + 40]
    pxor xmm6, xmm6
    aesenc xmm6, xmm0
    mov ecx, WIT_CALL_THREAD_YIELD
    xor edx, edx
    xor r8d, r8d
    xor r9d, r9d
    mov QWORD PTR [rsp + 32], 0
    call wit_native_call
    test rax, rax
    jnz failed
    movq rax, xmm6
    psrldq xmm6, 8
    movq r10, xmm6
    mov r11, 6363636363636363h
    cmp r11, r10
    je finished
failed:
    xor eax, eax
finished:
    movdqu xmm6, XMMWORD PTR [rsp + 48]
    add rsp, 72
    ret
wit_cpu_aes ENDP
PUBLIC wit_cpu_avx_probe
wit_cpu_avx_probe PROC
    vpxor ymm0, ymm0, ymm0
    vzeroupper
    ret
wit_cpu_avx_probe ENDP
END
