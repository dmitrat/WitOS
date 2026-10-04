option casemap:none

EXTERN wit_x64_timer_interrupt:PROC

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
    jmp wit_x64_restore_context
wit_x64_timer_entry ENDP

PUBLIC wit_x64_restore_context
wit_x64_restore_context PROC
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
wit_x64_restore_context ENDP

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


END
