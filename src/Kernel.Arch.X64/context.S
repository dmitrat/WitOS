// The timer's and the device lines' interrupt entries and the one restore path of a saved frame.
    .intel_syntax noprefix

    .text
    .globl wit_x64_timer_entry
wit_x64_timer_entry:
    // Long-mode hardware has already saved RIP/CS/RFLAGS/RSP/SS and aligned
    // the frame. Save all GPRs, including the caller-saved registers.
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
    fxsave64 [rsp]
    mov rcx, rsp
    sub rsp, 32
    call wit_x64_timer_interrupt
    mov rsp, rax
    jmp wit_x64_restore_context

// A device line's interrupt (K3.2): the same frame as the timer's, delivered by wit_x64_device_interrupt.
    .globl wit_x64_device_entry
wit_x64_device_entry:
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
    fxsave64 [rsp]
    mov rcx, rsp
    sub rsp, 32
    call wit_x64_device_interrupt
    mov rsp, rax
    jmp wit_x64_restore_context

    .globl wit_x64_restore_context
wit_x64_restore_context:
    fxrstor64 [rsp]
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

    .globl wit_x64_fxsave
wit_x64_fxsave:
    fxsave64 [rcx]
    ret

    .globl wit_x64_read_flags
wit_x64_read_flags:
    pushfq
    pop rax
    ret
