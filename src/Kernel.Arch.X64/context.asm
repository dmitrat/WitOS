option casemap:none

EXTERN wit_x64_timer_interrupt:PROC
EXTERN wit_worker_iterations:QWORD
EXTERN wit_worker_slices:QWORD
EXTERN wit_worker_done:QWORD
EXTERN wit_worker_errors:QWORD
EXTERN wit_worker_mxcsr:DWORD

.code
PUBLIC wit_x64_timer_entry
wit_x64_timer_entry PROC
    ; Long-mode hardware has already saved RIP/CS/RFLAGS/RSP/SS and aligned
    ; the frame. Save all GPRs, including the caller-saved registers.
    push rax
    push rbx
    push rcx
    push rdx
    push rbp
    push rdi
    push rsi
    push r8
    push r9
    push r10
    push r11
    push r12
    push r13
    push r14
    push r15
    cld
    sub rsp, 512
    db 048h
    fxsave [rsp]
    mov rcx, rsp
    sub rsp, 32
    call wit_x64_timer_interrupt
    mov rsp, rax
    db 048h
    fxrstor [rsp]
    add rsp, 512
    pop r15
    pop r14
    pop r13
    pop r12
    pop r11
    pop r10
    pop r9
    pop r8
    pop rsi
    pop rdi
    pop rbp
    pop rdx
    pop rcx
    pop rbx
    pop rax
    iretq
wit_x64_timer_entry ENDP

PUBLIC wit_x64_fxsave
wit_x64_fxsave PROC
    db 048h
    fxsave [rcx]
    ret
wit_x64_fxsave ENDP

PUBLIC wit_x64_read_flags
wit_x64_read_flags PROC
    pushfq
    pop rax
    ret
wit_x64_read_flags ENDP

PUBLIC wit_x64_worker
wit_x64_worker PROC
    ; This diagnostic thread never voluntarily yields or returns. Each
    ; worker uses different GPR, XMM0, XMM6 and MXCSR sentinels.
    mov rbx, rcx
    lea rdi, wit_worker_iterations
    lea rsi, wit_worker_slices
    lea rbp, wit_worker_done
    lea rdx, wit_worker_errors
    mov r12, 055AA001100220033h
    add r12, rbx
    mov r13, 0AA55003300220011h
    add r13, rbx
    movq xmm6, r12
    movq xmm0, r13
    lea rax, rounding_modes
    ldmxcsr DWORD PTR [rax + rbx * 4]
worker_loop:
    mov r10, 055AA001100220033h
    add r10, rbx
    cmp r12, r10
    jne worker_corrupt
    mov r10, 0AA55003300220011h
    add r10, rbx
    cmp r13, r10
    jne worker_corrupt
    movq rax, xmm6
    cmp rax, r12
    jne worker_corrupt
    movq rax, xmm0
    cmp rax, r13
    jne worker_corrupt
    lea r8, wit_worker_mxcsr
    stmxcsr DWORD PTR [r8 + rbx * 4]
    mov r10d, DWORD PTR [r8 + rbx * 4]
    lea r9, rounding_modes
    cmp r10d, DWORD PTR [r9 + rbx * 4]
    jne worker_corrupt
    inc QWORD PTR [rdi + rbx * 8]
    cmp QWORD PTR [rsi + rbx * 8], 3
    jb worker_loop
    jmp worker_finished
worker_corrupt:
    mov QWORD PTR [rdx + rbx * 8], 1
worker_finished:
    mov QWORD PTR [rbp + rbx * 8], 1
worker_wait:
    hlt
    jmp worker_wait
wit_x64_worker ENDP

.const
rounding_modes DWORD 03F80h, 05F80h
END
