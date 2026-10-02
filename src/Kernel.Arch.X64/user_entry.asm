option casemap:none
EXTERN wit_x64_user_syscall:PROC
EXTERN wit_x64_restore_context:PROC

.data
host_rsp QWORD 0
host_cr3 QWORD 0

.code
PUBLIC wit_arch_idle_once
PUBLIC wit_x64_idle_resume
wit_arch_idle_once PROC
    ; IF is clear at entry. The STI shadow covers HLT: no check-to-sleep race.
    sti
    hlt
wit_x64_idle_resume::
    cli
    ret
wit_arch_idle_once ENDP

PUBLIC wit_arch_set_user_tls
wit_arch_set_user_tls PROC
    ; Kernel chooses the base and selector on every return to a user thread.
    mov r8, rcx
    mov r9, rdx
    mov ax, 2Bh
    mov fs, ax
    mov ecx, 0C0000100h
    mov eax, r8d
    shr r8, 32
    mov edx, r8d
    wrmsr
    mov ax, 2Bh
    mov gs, ax
    mov ecx, 0C0000101h
    mov eax, r9d
    shr r9, 32
    mov edx, r9d
    wrmsr
    ret
wit_arch_set_user_tls ENDP

PUBLIC wit_arch_run_user
wit_arch_run_user PROC
    ; Serialized bootstrap launch; IF=0, one active user component.
    push rbx
    push rbp
    push rdi
    push rsi
    push r12
    push r13
    push r14
    push r15
    sub rsp, 520
    db 048h
    fxsave [rsp]
    mov host_rsp, rsp
    mov rax, cr3
    mov host_cr3, rax
    mov r8, rcx
    mov cr3, rdx
    mov ax, 2Bh
    mov ds, ax
    mov es, ax
    mov rsp, r8
    jmp wit_x64_restore_context
wit_arch_run_user ENDP

PUBLIC wit_arch_leave_user
wit_arch_leave_user PROC
    cli
    cld
    mov rax, host_cr3
    mov cr3, rax
    mov ax, 10h
    mov ds, ax
    mov es, ax
    mov ss, ax
    xor eax, eax
    mov fs, ax
    mov gs, ax
    mov rsp, host_rsp
    db 048h
    fxrstor [rsp]
    add rsp, 520
    pop r15
    pop r14
    pop r13
    pop r12
    pop rsi
    pop rdi
    pop rbp
    pop rbx
    ret
wit_arch_leave_user ENDP

PUBLIC wit_x64_user_syscall_entry
wit_x64_user_syscall_entry PROC
    ; DPL3 interrupt gate switched to TSS.RSP0 and cleared IF.
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
    call wit_x64_user_syscall
    mov rsp, rax
    jmp wit_x64_restore_context
wit_x64_user_syscall_entry ENDP
END
