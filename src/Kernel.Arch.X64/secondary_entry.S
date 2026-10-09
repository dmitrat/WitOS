// Secondary processors of x64 (plan step K7.2): the 64-bit entry the trampoline jumps to, and the entry of the two
// inter-processor interrupt vectors.
    .intel_syntax noprefix

    .data
    .p2align 3
secondary_gdt:
    .quad 0
secondary_idt:
    .quad 0

    .text
// rcx = the kernel's number of the processor; rsp = its kernel stack top; the trampoline's GDT is current. Loads the
// kernel's GDT and IDT (no task register: the processor stays at CPL0), reloads the selectors and enters C.
    .globl wit_x64_secondary_entry
wit_x64_secondary_entry:
    mov r12, rcx
    lea rcx, [rip + secondary_gdt]
    lea rdx, [rip + secondary_idt]
    sub rsp, 32
    call wit_x64_tables
    add rsp, 32
    mov rax, qword ptr [rip + secondary_gdt]
    lgdt [rax]
    mov rax, qword ptr [rip + secondary_idt]
    lidt [rax]
    mov ax, 0x10
    mov ds, ax
    mov es, ax
    mov ss, ax
    xor eax, eax
    mov fs, ax
    mov gs, ax
    push 8
    lea rax, [rip + .Lreloaded]
    push rax
    .byte 0x48, 0xCB                  // RETFQ: reload the 64-bit code selector
.Lreloaded:
    mov rcx, r12
    sub rsp, 32
    call wit_x64_secondary_main
    ud2

// Vectors 0xF0 and 0xF1 on a secondary processor: the same frame as the timer's, served by wit_x64_ipi_interrupt.
    .globl wit_x64_ipi_entry
wit_x64_ipi_entry:
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
    call wit_x64_ipi_interrupt
    mov rsp, rax
    jmp wit_x64_restore_context
