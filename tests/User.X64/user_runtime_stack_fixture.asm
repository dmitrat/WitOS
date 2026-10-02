option casemap:none
EXTERN __chkstk:PROC
.const
ALIGN 16
probe_pattern LABEL BYTE
    DQ 00123456789ABCDEFh, 076543210FEDCBA98h
    DQ 00123456789ABCDF0h, 076543210FEDCBA97h
    DQ 00123456789ABCDF1h, 076543210FEDCBA96h
    DQ 00123456789ABCDF2h, 076543210FEDCBA95h
    DQ 00123456789ABCDF3h, 076543210FEDCBA94h
    DQ 00123456789ABCDF4h, 076543210FEDCBA93h
    DQ 00123456789ABCDF5h, 076543210FEDCBA92h
    DQ 00123456789ABCDF6h, 076543210FEDCBA91h
    DQ 00123456789ABCDF7h, 076543210FEDCBA90h
    DQ 00123456789ABCDF8h, 076543210FEDCBA8Fh
    DQ 00123456789ABCDF9h, 076543210FEDCBA8Eh
    DQ 00123456789ABCDFAh, 076543210FEDCBA8Dh
    DQ 00123456789ABCDFBh, 076543210FEDCBA8Ch
    DQ 00123456789ABCDFCh, 076543210FEDCBA8Bh
    DQ 00123456789ABCDFDh, 076543210FEDCBA8Ah
    DQ 00123456789ABCDFEh, 076543210FEDCBA89h
.code
PUBLIC wit_stack_check_registers
wit_stack_check_registers PROC FRAME
    push rbx
    .pushreg rbx
    push rbp
    .pushreg rbp
    push rsi
    .pushreg rsi
    push rdi
    .pushreg rdi
    push r12
    .pushreg r12
    push r13
    .pushreg r13
    push r14
    .pushreg r14
    push r15
    .pushreg r15
    sub rsp, 232
    .allocstack 232
    movdqu XMMWORD PTR [rsp + 32], xmm6
    .savexmm128 xmm6, 32
    movdqu XMMWORD PTR [rsp + 48], xmm7
    .savexmm128 xmm7, 48
    movdqu XMMWORD PTR [rsp + 64], xmm8
    .savexmm128 xmm8, 64
    movdqu XMMWORD PTR [rsp + 80], xmm9
    .savexmm128 xmm9, 80
    movdqu XMMWORD PTR [rsp + 96], xmm10
    .savexmm128 xmm10, 96
    movdqu XMMWORD PTR [rsp + 112], xmm11
    .savexmm128 xmm11, 112
    movdqu XMMWORD PTR [rsp + 128], xmm12
    .savexmm128 xmm12, 128
    movdqu XMMWORD PTR [rsp + 144], xmm13
    .savexmm128 xmm13, 144
    movdqu XMMWORD PTR [rsp + 160], xmm14
    .savexmm128 xmm14, 160
    movdqu XMMWORD PTR [rsp + 176], xmm15
    .savexmm128 xmm15, 176
    .endprolog
    mov [rsp + 192], rcx
    mov [rsp + 200], rsp
    mov rbx, 100
    mov rbp, 101
    mov rsi, 102
    mov rdi, 103
    mov r12, 104
    mov r13, 105
    mov r14, 106
    mov r15, 107
    mov rcx, 108
    mov rdx, 109
    mov r8, 110
    mov r9, 111
    movdqu xmm0, XMMWORD PTR [probe_pattern + 0]
    movdqu xmm1, XMMWORD PTR [probe_pattern + 16]
    movdqu xmm2, XMMWORD PTR [probe_pattern + 32]
    movdqu xmm3, XMMWORD PTR [probe_pattern + 48]
    movdqu xmm4, XMMWORD PTR [probe_pattern + 64]
    movdqu xmm5, XMMWORD PTR [probe_pattern + 80]
    movdqu xmm6, XMMWORD PTR [probe_pattern + 96]
    movdqu xmm7, XMMWORD PTR [probe_pattern + 112]
    movdqu xmm8, XMMWORD PTR [probe_pattern + 128]
    movdqu xmm9, XMMWORD PTR [probe_pattern + 144]
    movdqu xmm10, XMMWORD PTR [probe_pattern + 160]
    movdqu xmm11, XMMWORD PTR [probe_pattern + 176]
    movdqu xmm12, XMMWORD PTR [probe_pattern + 192]
    movdqu xmm13, XMMWORD PTR [probe_pattern + 208]
    movdqu xmm14, XMMWORD PTR [probe_pattern + 224]
    movdqu xmm15, XMMWORD PTR [probe_pattern + 240]
    mov rax, [rsp + 192]
    call __chkstk
    cmp rsp, [rsp + 200]
    jne failed
    cmp rax, [rsp + 192]
    jne failed
    cmp rbx, 100
    jne failed
    cmp rbp, 101
    jne failed
    cmp rsi, 102
    jne failed
    cmp rdi, 103
    jne failed
    cmp r12, 104
    jne failed
    cmp r13, 105
    jne failed
    cmp r14, 106
    jne failed
    cmp r15, 107
    jne failed
    cmp rcx, 108
    jne failed
    cmp rdx, 109
    jne failed
    cmp r8, 110
    jne failed
    cmp r9, 111
    jne failed
    pcmpeqb xmm0, XMMWORD PTR [probe_pattern + 0]
    pmovmskb r10d, xmm0
    cmp r10d, 0FFFFh
    jne failed
    pcmpeqb xmm1, XMMWORD PTR [probe_pattern + 16]
    pmovmskb r10d, xmm1
    cmp r10d, 0FFFFh
    jne failed
    pcmpeqb xmm2, XMMWORD PTR [probe_pattern + 32]
    pmovmskb r10d, xmm2
    cmp r10d, 0FFFFh
    jne failed
    pcmpeqb xmm3, XMMWORD PTR [probe_pattern + 48]
    pmovmskb r10d, xmm3
    cmp r10d, 0FFFFh
    jne failed
    pcmpeqb xmm4, XMMWORD PTR [probe_pattern + 64]
    pmovmskb r10d, xmm4
    cmp r10d, 0FFFFh
    jne failed
    pcmpeqb xmm5, XMMWORD PTR [probe_pattern + 80]
    pmovmskb r10d, xmm5
    cmp r10d, 0FFFFh
    jne failed
    pcmpeqb xmm6, XMMWORD PTR [probe_pattern + 96]
    pmovmskb r10d, xmm6
    cmp r10d, 0FFFFh
    jne failed
    pcmpeqb xmm7, XMMWORD PTR [probe_pattern + 112]
    pmovmskb r10d, xmm7
    cmp r10d, 0FFFFh
    jne failed
    pcmpeqb xmm8, XMMWORD PTR [probe_pattern + 128]
    pmovmskb r10d, xmm8
    cmp r10d, 0FFFFh
    jne failed
    pcmpeqb xmm9, XMMWORD PTR [probe_pattern + 144]
    pmovmskb r10d, xmm9
    cmp r10d, 0FFFFh
    jne failed
    pcmpeqb xmm10, XMMWORD PTR [probe_pattern + 160]
    pmovmskb r10d, xmm10
    cmp r10d, 0FFFFh
    jne failed
    pcmpeqb xmm11, XMMWORD PTR [probe_pattern + 176]
    pmovmskb r10d, xmm11
    cmp r10d, 0FFFFh
    jne failed
    pcmpeqb xmm12, XMMWORD PTR [probe_pattern + 192]
    pmovmskb r10d, xmm12
    cmp r10d, 0FFFFh
    jne failed
    pcmpeqb xmm13, XMMWORD PTR [probe_pattern + 208]
    pmovmskb r10d, xmm13
    cmp r10d, 0FFFFh
    jne failed
    pcmpeqb xmm14, XMMWORD PTR [probe_pattern + 224]
    pmovmskb r10d, xmm14
    cmp r10d, 0FFFFh
    jne failed
    pcmpeqb xmm15, XMMWORD PTR [probe_pattern + 240]
    pmovmskb r10d, xmm15
    cmp r10d, 0FFFFh
    jne failed
    mov eax, 1
    jmp restore
failed:
    xor eax, eax
restore:
    movdqu xmm6, XMMWORD PTR [rsp + 32]
    movdqu xmm7, XMMWORD PTR [rsp + 48]
    movdqu xmm8, XMMWORD PTR [rsp + 64]
    movdqu xmm9, XMMWORD PTR [rsp + 80]
    movdqu xmm10, XMMWORD PTR [rsp + 96]
    movdqu xmm11, XMMWORD PTR [rsp + 112]
    movdqu xmm12, XMMWORD PTR [rsp + 128]
    movdqu xmm13, XMMWORD PTR [rsp + 144]
    movdqu xmm14, XMMWORD PTR [rsp + 160]
    movdqu xmm15, XMMWORD PTR [rsp + 176]
    add rsp, 232
    pop r15
    pop r14
    pop r13
    pop r12
    pop rdi
    pop rsi
    pop rbp
    pop rbx
    ret
wit_stack_check_registers ENDP
PUBLIC wit_stack_probe_to
wit_stack_probe_to PROC FRAME
    sub rsp, 40
    .allocstack 40
    .endprolog
    mov rax, rsp
    sub rax, rcx
    call __chkstk
    add rsp, 40
    ret
wit_stack_probe_to ENDP
PUBLIC wit_stack_probe_huge
wit_stack_probe_huge PROC FRAME
    sub rsp, 40
    .allocstack 40
    .endprolog
    mov rax, -1
    call __chkstk
    add rsp, 40
    ret
wit_stack_probe_huge ENDP
END
