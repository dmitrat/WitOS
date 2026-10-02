option casemap:none

; Test-only x64 code linked into WITOS_SELFTEST kernels: CPU fault triggers and the preemption workers.

EXTERN wit_worker_iterations:QWORD
EXTERN wit_worker_slices:QWORD
EXTERN wit_worker_done:QWORD
EXTERN wit_worker_errors:QWORD
EXTERN wit_worker_mxcsr:DWORD

.code
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
