; Microsoft x64 ABI. Kernel faults are fatal; validated user upcalls may resume.
option casemap:none

EXTERN wit_kernel_entry:PROC
EXTERN wit_x64_exception:PROC
EXTERN wit_x64_user_exception:PROC
EXTERN wit_x64_restore_context:PROC
EXTERN wit_x64_timer_entry:PROC
EXTERN wit_x64_user_syscall_entry:PROC
EXTERN wit_x64_device_entry:PROC
EXTERN wit_x64_ipi_entry:PROC
EXTERN wit_x64_ipi_vector:DWORD
EXTERN wit_x64_device_vector:DWORD

EXTERN wit_x64_kernel_stack:BYTE

.code
PUBLIC wit_arch_enter
wit_arch_enter PROC
    cli
    cld
    lea rsp, [wit_x64_kernel_stack + 4096 + 65536]
    and rsp, -16
    xor ebp, ebp
    sub rsp, 32                   ; caller-owned shadow space
    call wit_kernel_entry         ; RCX still contains WitBootInfo
    ud2
wit_arch_enter ENDP

PUBLIC wit_x64_stack_pointer
wit_x64_stack_pointer PROC
    mov rax, rsp
    ret
wit_x64_stack_pointer ENDP

PUBLIC wit_x64_load_tables
wit_x64_load_tables PROC
    lgdt fword ptr [rcx]
    push 8
    lea rax, [reload_cs]
    push rax
    db 048h, 0CBh                 ; RETFQ: reload the 64-bit code selector
reload_cs:
    mov ax, 10h
    mov ds, ax
    mov es, ax
    mov ss, ax
    xor eax, eax
    mov fs, ax
    mov gs, ax
    mov ax, 18h
    ltr ax
    lidt fword ptr [rdx]
    ret
wit_x64_load_tables ENDP

MAKE_ISR MACRO number
isr&number PROC
    IF number EQ 32
        jmp wit_x64_timer_entry
    ELSEIF number EQ 128
        jmp wit_x64_user_syscall_entry
    ELSEIF (number GE 33) AND (number LE 47)
        ; A PIC input of a device line (K3.2): the vector is noted for the one processor with interrupts disabled.
        mov DWORD PTR [wit_x64_device_vector], number
        jmp wit_x64_device_entry
    ELSEIF (number EQ 240) OR (number EQ 241)
        ; An inter-processor interrupt on a secondary processor (K7.2): one in flight at a time.
        mov DWORD PTR [wit_x64_ipi_vector], number
        jmp wit_x64_ipi_entry
    ELSE
    ; These CPU exceptions push an error code; all other vectors need a zero.
    IF (number NE 8) AND (number NE 10) AND (number NE 11) AND (number NE 12) AND (number NE 13) AND (number NE 14) AND (number NE 17) AND (number NE 21) AND (number NE 29) AND (number NE 30)
        push 0
    ENDIF
    push number
    jmp exception_common
    ENDIF
isr&number ENDP
ENDM

vector = 0
REPT 256
    MAKE_ISR %vector
    vector = vector + 1
ENDM

exception_common PROC
    cli
    cld
    test BYTE PTR [rsp+24],3
    jnz user_exception_common
    mov rcx, rsp                  ; capture frame before realigning the stack
    mov rdx, cr2
    and rsp, -16
    sub rsp, 32
    call wit_x64_exception
    ud2
exception_common ENDP

user_exception_common PROC
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
    sub rsp,512
    db 048h
    fxsave [rsp]
    mov rcx,rsp
    mov rdx,[rsp+632]
    mov r8,[rsp+640]
    mov r9,cr2
    mov rax,[rsp+648]
    mov [rsp+632],rax
    mov rax,[rsp+656]
    mov [rsp+640],rax
    mov rax,[rsp+664]
    mov [rsp+648],rax
    mov rax,[rsp+672]
    mov [rsp+656],rax
    mov rax,[rsp+680]
    mov [rsp+664],rax
    sub rsp,32
    call wit_x64_user_exception
    mov rsp,rax
    jmp wit_x64_restore_context
user_exception_common ENDP

PUBLIC wit_arch_process_write_barrier
wit_arch_process_write_barrier PROC
    ; All guest threads execute on the sole online logical processor. This is
    ; a full data-memory fence, not instruction-cache maintenance or GC stop.
    mfence
    ret
wit_arch_process_write_barrier ENDP

.data
ALIGN 8
PUBLIC wit_x64_isr_table
wit_x64_isr_table LABEL QWORD
ISR_POINTER MACRO number
    QWORD isr&number
ENDM
vector = 0
REPT 256
    ISR_POINTER %vector
    vector = vector + 1
ENDM

END
