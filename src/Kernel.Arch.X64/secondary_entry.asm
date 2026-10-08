option casemap:none
EXTERN wit_x64_secondary_main:PROC
EXTERN wit_x64_ipi_interrupt:PROC
EXTERN wit_x64_restore_context:PROC
EXTERN wit_x64_tables:PROC

; Secondary processors of x64 (plan step K7.2): the 64-bit entry the trampoline jumps to, and the entry of the two
; inter-processor interrupt vectors.

.data
secondary_gdt QWORD 0
secondary_idt QWORD 0

.code
; rcx = the kernel's number of the processor; rsp = its kernel stack top; the trampoline's GDT is current. Loads the
; kernel's GDT and IDT (no task register: the processor stays at CPL0), reloads the selectors and enters C.
PUBLIC wit_x64_secondary_entry
wit_x64_secondary_entry PROC
    mov r12, rcx
    lea rcx, secondary_gdt
    lea rdx, secondary_idt
    sub rsp, 32
    call wit_x64_tables
    add rsp, 32
    mov rax, secondary_gdt
    lgdt fword ptr [rax]
    mov rax, secondary_idt
    lidt fword ptr [rax]
    mov ax, 10h
    mov ds, ax
    mov es, ax
    mov ss, ax
    xor eax, eax
    mov fs, ax
    mov gs, ax
    push 8
    lea rax, reloaded
    push rax
    db 048h
    retf
reloaded:
    mov rcx, r12
    sub rsp, 32
    call wit_x64_secondary_main
    ud2
wit_x64_secondary_entry ENDP

; Vectors 0xF0 and 0xF1 on a secondary processor: the same frame as the timer's, served by wit_x64_ipi_interrupt.
PUBLIC wit_x64_ipi_entry
wit_x64_ipi_entry PROC
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
    call wit_x64_ipi_interrupt
    mov rsp, rax
    jmp wit_x64_restore_context
wit_x64_ipi_entry ENDP
END
