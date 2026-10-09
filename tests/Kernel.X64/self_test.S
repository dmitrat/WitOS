// Test-only x64 code linked into WITOS_SELFTEST kernels: CPU fault triggers and the preemption workers.
    .intel_syntax noprefix

    .text
    .globl wit_x64_trigger_breakpoint
wit_x64_trigger_breakpoint:
    int3
    ret

    .globl wit_x64_trigger_divide_error
wit_x64_trigger_divide_error:
    xor edx, edx
    mov eax, 1
    xor ecx, ecx
    div rcx
    ret

    .globl wit_x64_trigger_invalid_opcode
wit_x64_trigger_invalid_opcode:
    ud2
    ret

    .globl wit_x64_trigger_general_protection
wit_x64_trigger_general_protection:
    mov ax, 0xFFF8                    // beyond the kernel GDT, error code 0xFFF8
    mov ds, ax
    ret

    .globl wit_x64_trigger_page_fault
wit_x64_trigger_page_fault:
    movabs rax, 0x0000400000000000    // canonical address in a verified absent PML4 slot
    mov rax, qword ptr [rax]
    ret

    .globl wit_x64_trigger_double_fault
wit_x64_trigger_double_fault:
    mov rsp, 1                        // #PF cannot deliver its frame on this stack
    movabs rax, 0x0000400000000000
    mov rax, qword ptr [rax]
    ud2

    .globl wit_x64_worker
wit_x64_worker:
    // This diagnostic thread never voluntarily yields or returns. Each
    // worker uses different GPR, XMM0, XMM6 and MXCSR sentinels.
    mov rbx, rcx
    lea rdi, [rip + wit_worker_iterations]
    lea rsi, [rip + wit_worker_slices]
    lea rbp, [rip + wit_worker_done]
    lea rdx, [rip + wit_worker_errors]
    movabs r12, 0x55AA001100220033
    add r12, rbx
    movabs r13, 0xAA55003300220011
    add r13, rbx
    movq xmm6, r12
    movq xmm0, r13
    lea rax, [rip + .Lrounding_modes]
    ldmxcsr dword ptr [rax + rbx * 4]
.Lworker_loop:
    movabs r10, 0x55AA001100220033
    add r10, rbx
    cmp r12, r10
    jne .Lworker_corrupt
    movabs r10, 0xAA55003300220011
    add r10, rbx
    cmp r13, r10
    jne .Lworker_corrupt
    movq rax, xmm6
    cmp rax, r12
    jne .Lworker_corrupt
    movq rax, xmm0
    cmp rax, r13
    jne .Lworker_corrupt
    lea r8, [rip + wit_worker_mxcsr]
    stmxcsr dword ptr [r8 + rbx * 4]
    mov r10d, dword ptr [r8 + rbx * 4]
    lea r9, [rip + .Lrounding_modes]
    cmp r10d, dword ptr [r9 + rbx * 4]
    jne .Lworker_corrupt
    inc qword ptr [rdi + rbx * 8]
    cmp qword ptr [rsi + rbx * 8], 3
    jb .Lworker_loop
    jmp .Lworker_finished
.Lworker_corrupt:
    mov qword ptr [rdx + rbx * 8], 1
.Lworker_finished:
    mov qword ptr [rbp + rbx * 8], 1
.Lworker_wait:
    hlt
    jmp .Lworker_wait

    .section .rdata, "dr"
    .p2align 2
.Lrounding_modes:
    .long 0x3F80, 0x5F80
