// The kernel's entry, its descriptor table load and the 256 interrupt vectors, in the Microsoft x64 convention.
// Kernel faults are fatal; validated user upcalls may resume.
    .intel_syntax noprefix

    .text
    .globl wit_arch_enter
wit_arch_enter:
    cli
    cld
    lea rsp, [rip + wit_x64_kernel_stack + 4096 + 65536]
    and rsp, -16
    xor ebp, ebp
    sub rsp, 32                       // caller-owned shadow space
    call wit_kernel_entry             // RCX still contains WitBootInfo
    ud2

    .globl wit_x64_stack_pointer
wit_x64_stack_pointer:
    mov rax, rsp
    ret

    .globl wit_x64_load_tables
wit_x64_load_tables:
    lgdt [rcx]
    push 8
    lea rax, [rip + .Lreload_cs]
    push rax
    .byte 0x48, 0xCB                  // RETFQ: reload the 64-bit code selector
.Lreload_cs:
    mov ax, 0x10
    mov ds, ax
    mov es, ax
    mov ss, ax
    xor eax, eax
    mov fs, ax
    mov gs, ax
    mov ax, 0x18
    ltr ax
    lidt [rdx]
    ret

    .altmacro
    .macro MAKE_ISR number
isr\number:
    .if \number == 32
    jmp wit_x64_timer_entry
    .elseif (\number >= 33) && (\number <= 47)
    // A PIC input of a device line (K3.2): the vector is noted for the one processor with interrupts disabled.
    mov dword ptr [rip + wit_x64_device_vector], \number
    jmp wit_x64_device_entry
    .elseif (\number == 240) || (\number == 241)
    // An inter-processor interrupt on a secondary processor (K7.2): one in flight at a time.
    mov dword ptr [rip + wit_x64_ipi_vector], \number
    jmp wit_x64_ipi_entry
    .else
    // These CPU exceptions push an error code; all other vectors need a zero.
    .if (\number != 8) && (\number != 10) && (\number != 11) && (\number != 12) && (\number != 13) && (\number != 14) && (\number != 17) && (\number != 21) && (\number != 29) && (\number != 30)
    push 0
    .endif
    push \number
    jmp exception_common
    .endif
    .endm

    .set vector, 0
    .rept 256
    MAKE_ISR %vector
    .set vector, vector + 1
    .endr

exception_common:
    cli
    cld
    test byte ptr [rsp + 24], 3
    jnz user_exception_common
    mov rcx, rsp                      // capture frame before realigning the stack
    mov rdx, cr2
    and rsp, -16
    sub rsp, 32
    call wit_x64_exception
    ud2

user_exception_common:
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
    sub rsp, 512
    fxsave64 [rsp]
    mov rcx, rsp
    mov rdx, [rsp + 632]
    mov r8, [rsp + 640]
    mov r9, cr2
    mov rax, [rsp + 648]
    mov [rsp + 632], rax
    mov rax, [rsp + 656]
    mov [rsp + 640], rax
    mov rax, [rsp + 664]
    mov [rsp + 648], rax
    mov rax, [rsp + 672]
    mov [rsp + 656], rax
    mov rax, [rsp + 680]
    mov [rsp + 664], rax
    sub rsp, 32
    call wit_x64_user_exception
    mov rsp, rax
    jmp wit_x64_restore_context

    .globl wit_arch_process_write_barrier
wit_arch_process_write_barrier:
    // All guest threads execute on the sole online logical processor. This is
    // a full data-memory fence, not instruction-cache maintenance or GC stop.
    mfence
    ret

    .macro ISR_POINTER number
    .quad isr\number
    .endm

    .data
    .p2align 3
    .globl wit_x64_isr_table
wit_x64_isr_table:
    .set vector, 0
    .rept 256
    ISR_POINTER %vector
    .set vector, vector + 1
    .endr
