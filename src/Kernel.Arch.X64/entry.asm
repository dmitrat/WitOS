; Microsoft x64 ABI. Fatal exception paths never resume interrupted code.
option casemap:none

EXTERN wit_kernel_entry:PROC
EXTERN wit_x64_exception:PROC

.data?
ALIGN 16
PUBLIC wit_x64_kernel_stack
wit_x64_kernel_stack BYTE 65536 DUP (?)

.code
PUBLIC wit_platform_enter
wit_platform_enter PROC
    cli
    cld
    lea rsp, [wit_x64_kernel_stack + 65536]
    and rsp, -16
    xor ebp, ebp
    sub rsp, 32                   ; caller-owned shadow space
    call wit_kernel_entry         ; RCX still contains WitBootInfo
    ud2
wit_platform_enter ENDP

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
    ; These CPU exceptions push an error code; all other vectors need a zero.
    IF (number NE 8) AND (number NE 10) AND (number NE 11) AND (number NE 12) AND (number NE 13) AND (number NE 14) AND (number NE 17) AND (number NE 21) AND (number NE 29) AND (number NE 30)
        push 0
    ENDIF
    push number
    jmp exception_common
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
    mov rcx, rsp                  ; capture frame before realigning the stack
    mov rdx, cr2
    and rsp, -16
    sub rsp, 32
    call wit_x64_exception
    ud2
exception_common ENDP

PUBLIC wit_x64_trigger_breakpoint
wit_x64_trigger_breakpoint PROC
    int 3
    ret
wit_x64_trigger_breakpoint ENDP

PUBLIC wit_x64_trigger_divide_error
wit_x64_trigger_divide_error PROC
    xor edx, edx
    mov eax, 1
    xor ecx, ecx
    div rcx
    ret
wit_x64_trigger_divide_error ENDP

PUBLIC wit_x64_trigger_invalid_opcode
wit_x64_trigger_invalid_opcode PROC
    ud2
    ret
wit_x64_trigger_invalid_opcode ENDP

PUBLIC wit_x64_trigger_general_protection
wit_x64_trigger_general_protection PROC
    mov ax, 0FFF8h                ; beyond the kernel GDT, error code 0xFFF8
    mov ds, ax
    ret
wit_x64_trigger_general_protection ENDP

PUBLIC wit_x64_trigger_page_fault
wit_x64_trigger_page_fault PROC
    mov rax, 0000400000000000h    ; canonical address in a verified absent PML4 slot
    mov rax, qword ptr [rax]
    ret
wit_x64_trigger_page_fault ENDP

PUBLIC wit_x64_trigger_double_fault
wit_x64_trigger_double_fault PROC
    mov rsp, 1                   ; #PF cannot deliver its frame on this stack
    mov rax, 0000400000000000h
    mov rax, qword ptr [rax]
    ud2
wit_x64_trigger_double_fault ENDP

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
