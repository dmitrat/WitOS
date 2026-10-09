// User mode: the launch and the return of the bootstrap run, the user TLS bases, the idle halt and the two system
// call transports.
    .intel_syntax noprefix

    .data
    .p2align 3
host_rsp:
    .quad 0
host_cr3:
    .quad 0
// The kernel stack of the selected thread (TSS.RSP0's value) and the caller's RSP across the SYSCALL entry: one
// processor, interrupts masked by SFMASK (K5.2a).
    .globl wit_x64_syscall_kernel_rsp
wit_x64_syscall_kernel_rsp:
    .quad 0
syscall_user_rsp:
    .quad 0

    .text
    .globl wit_arch_idle_once
    .globl wit_x64_idle_resume
wit_arch_idle_once:
    // IF is clear at entry. The STI shadow covers HLT: no check-to-sleep race.
    sti
    hlt
wit_x64_idle_resume:
    cli
    ret

    .globl wit_arch_set_user_tls
wit_arch_set_user_tls:
    // Kernel chooses the base and selector on every return to a user thread.
    mov r8, rcx
    mov r9, rdx
    mov ax, 0x2B
    mov fs, ax
    mov ecx, 0xC0000100
    mov eax, r8d
    shr r8, 32
    mov edx, r8d
    wrmsr
    mov ax, 0x2B
    mov gs, ax
    mov ecx, 0xC0000101
    mov eax, r9d
    shr r9, 32
    mov edx, r9d
    wrmsr
    ret

    .globl wit_arch_run_user
wit_arch_run_user:
    // Serialized bootstrap launch; IF=0, one active user component.
    push rbx
    push rbp
    push rdi
    push rsi
    push r12
    push r13
    push r14
    push r15
    sub rsp, 520
    fxsave64 [rsp]
    mov qword ptr [rip + host_rsp], rsp
    mov rax, cr3
    mov qword ptr [rip + host_cr3], rax
    mov r8, rcx
    mov cr3, rdx
    mov ax, 0x2B
    mov ds, ax
    mov es, ax
    mov rsp, r8
    jmp wit_x64_restore_context

// rcx = a frame on the kernel stack of the thread it belongs to (K5.2c): abandons the current kernel stack and
// restores the frame as the trap return path would; DS and ES stay the user selectors of the run.
    .globl wit_arch_resume_frame
wit_arch_resume_frame:
    mov rsp, rcx
    jmp wit_x64_restore_context

    .globl wit_arch_leave_user
wit_arch_leave_user:
    cli
    cld
    mov rax, qword ptr [rip + host_cr3]
    mov cr3, rax
    mov ax, 0x10
    mov ds, ax
    mov es, ax
    mov ss, ax
    xor eax, eax
    mov fs, ax
    mov gs, ax
    mov rsp, qword ptr [rip + host_rsp]
    fxrstor64 [rsp]
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

    .globl wit_x64_user_syscall_entry
wit_x64_user_syscall_entry:
    // DPL3 interrupt gate switched to TSS.RSP0 and cleared IF.
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
    call wit_x64_user_syscall
    mov rsp, rax
    jmp wit_x64_restore_context

// SYSCALL entry (RFC 0011 section 6.1): RCX holds the return RIP, R11 the RFLAGS, RSP is still the caller's. The
// frame built here equals the interrupt gate's, so one restore path (IRETQ) serves both transports.
    .globl wit_x64_syscall_entry
wit_x64_syscall_entry:
    mov qword ptr [rip + syscall_user_rsp], rsp
    mov rsp, qword ptr [rip + wit_x64_syscall_kernel_rsp]
    push 0x2B                         // SS
    push qword ptr [rip + syscall_user_rsp]
    push r11                          // RFLAGS
    push 0x33                         // CS
    push rcx                          // RIP
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
    call wit_x64_user_syscall_sysv
    mov rsp, rax
    jmp wit_x64_restore_context
