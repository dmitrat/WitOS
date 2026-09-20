option casemap:none
include user_abi.inc

EXPECT MACRO value
    cmp rax, value
    jne failed
ENDM
CREATE MACRO target, argument
    lea rcx, target
    mov rdx, argument
    xor r8d, r8d
    mov eax, WIT_CALL_THREAD_CREATE
    int 80h
ENDM
JOIN MACRO handle
    mov rcx, handle
    mov eax, WIT_CALL_THREAD_JOIN
    int 80h
ENDM

.code
PUBLIC wit_user_start
wit_user_start PROC
    mov r15, rcx
    cmp DWORD PTR [r15], WIT_ABI_VERSION
    jne failed
    mov rbx, QWORD PTR fs:[WIT_TLS_SELF_OFFSET]
    mov rax, WIT_USER_TLS
    cmp rbx, rax
    jne failed
    cmp QWORD PTR fs:[WIT_TLS_ARGUMENT_OFFSET], r15
    jne failed
    mov r14, [r15 + WIT_TEST_MODE_OFFSET]
    cmp r14, WIT_THREAD_TEST_CYCLE
    je cycle_main
    cmp r14, WIT_THREAD_TEST_CAPACITY
    je capacity_main
    cmp r14, WIT_THREAD_TEST_FAULT
    jae fault_main

    ; Wrong type, foreign/stale handles, self-join and live close are errors.
    JOIN QWORD PTR [r15 + 8]
    EXPECT WIT_STATUS_WRONG_TYPE
    JOIN QWORD PTR [r15 + WIT_TEST_FOREIGN_OFFSET]
    EXPECT WIT_STATUS_BAD_HANDLE
    JOIN QWORD PTR fs:[WIT_TLS_HANDLE_OFFSET]
    EXPECT WIT_STATUS_DEADLOCK
    mov rcx, QWORD PTR fs:[WIT_TLS_HANDLE_OFFSET]
    mov eax, WIT_CALL_CLOSE
    int 80h
    EXPECT WIT_STATUS_BUSY
    mov rcx, WIT_USER_DATA
    xor edx, edx
    xor r8d, r8d
    mov eax, WIT_CALL_THREAD_CREATE
    int 80h
    EXPECT WIT_STATUS_BAD_ADDRESS
    lea rcx, worker
    mov r8d, 2 ; only bit 0 (detached) is supported
    mov eax, WIT_CALL_THREAD_CREATE
    int 80h
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    test rdx, rdx
    jne failed
    ; A component cannot remove an active thread's TLS through memory syscalls.
    mov rcx, rbx
    mov edx, 4096
    mov eax, WIT_CALL_MEMORY_DECOMMIT
    int 80h
    EXPECT WIT_STATUS_BAD_ADDRESS
    mov QWORD PTR fs:[WIT_TLS_DATA_OFFSET], 9876h

    CREATE worker, 1
    EXPECT WIT_STATUS_OK
    mov r12, rdx
    CREATE worker, 2
    EXPECT WIT_STATUS_OK
    mov r13, rdx
    JOIN r12
    EXPECT WIT_STATUS_OK
    cmp rdx, 101
    jne failed
    JOIN r13
    EXPECT WIT_STATUS_OK
    cmp rdx, 102
    jne failed
    cmp QWORD PTR fs:[WIT_TLS_DATA_OFFSET], 9876h
    jne failed
    ; Reuse a joined slot: fresh TLS/stack and a different handle generation.
    CREATE worker, 3
    EXPECT WIT_STATUS_OK
    mov r13, rdx
    cmp r13, r12
    je failed
    JOIN r12
    EXPECT WIT_STATUS_BAD_HANDLE
    mov eax, WIT_CALL_THREAD_YIELD
    int 80h
    EXPECT WIT_STATUS_OK
    JOIN r13
    EXPECT WIT_STATUS_OK
    cmp rdx, 103
    jne failed
    JOIN r13
    EXPECT WIT_STATUS_BAD_HANDLE
    CREATE worker, 4
    EXPECT WIT_STATUS_OK
    mov r13, rdx
close_finished:
    mov eax, WIT_CALL_THREAD_YIELD
    int 80h
    EXPECT WIT_STATUS_OK
    mov rcx, r13
    mov eax, WIT_CALL_CLOSE
    int 80h
    cmp rax, WIT_STATUS_BUSY
    je close_finished
    EXPECT WIT_STATUS_OK
    JOIN r13
    EXPECT WIT_STATUS_BAD_HANDLE
    mov ecx, WIT_TEST_EXIT_CODE
    jmp process_exit

worker:
    ; No calls/prologue have touched the new stack. Check all 16 KiB and TLS payload.
    mov r12, rcx
    mov rbx, QWORD PTR fs:[WIT_TLS_SELF_OFFSET]
    cmp QWORD PTR fs:[WIT_TLS_ARGUMENT_OFFSET], r12
    jne failed
    cmp QWORD PTR fs:[WIT_TLS_DATA_OFFSET], 0
    jne failed
    movq rax, xmm0
    test rax, rax
    jne failed
    movq rax, xmm6
    test rax, rax
    jne failed
    lea rdi, [rbx - 6000h]
    mov ecx, 2048
    xor eax, eax
    repe scasq
    jne failed
    mov QWORD PTR fs:[WIT_TLS_DATA_OFFSET], r12
    movq xmm6, r12
    mov r13, 123456789ABCDEF0h
    add r13, r12
    movq xmm0, r13
    mov [rsp - 16], r13
    mov eax, 1F80h
    cmp r12, 1
    jne worker_rounding
    mov eax, 3F80h
worker_rounding:
    mov [rsp - 24], eax
    ldmxcsr DWORD PTR [rsp - 24]
    mov rdi, WIT_USER_DATA
    cmp r12, 3
    jae worker_exit
    mov QWORD PTR [rdi + r12 * 8], 1
worker_loop:
    cmp QWORD PTR fs:[WIT_TLS_SELF_OFFSET], rbx
    jne failed
    cmp QWORD PTR fs:[WIT_TLS_DATA_OFFSET], r12
    jne failed
    movq rax, xmm6
    cmp rax, r12
    jne failed
    movq rax, xmm0
    cmp rax, r13
    jne failed
    cmp [rsp - 16], r13
    jne failed
    stmxcsr DWORD PTR [rsp - 32]
    mov eax, [rsp - 24]
    cmp [rsp - 32], eax
    jne failed
    inc QWORD PTR fs:[WIT_TLS_DATA_OFFSET + 8]
    cmp r12, 1
    jne worker_b
    cmp QWORD PTR [rdi + 16], 0
    je worker_loop
    mov QWORD PTR [rdi + 24], 1
    jmp worker_exit
worker_b:
    cmp QWORD PTR [rdi + 24], 0
    je worker_loop
worker_exit:
    lea rcx, [r12 + 100]
    jmp thread_exit

cycle_main:
    CREATE cycle_worker, QWORD PTR fs:[WIT_TLS_HANDLE_OFFSET]
    EXPECT WIT_STATUS_OK
    JOIN rdx
    cmp rax, WIT_STATUS_DEADLOCK
    je cycle_parent_exit
    EXPECT WIT_STATUS_OK
    cmp rdx, 43
    jne failed
    mov ecx, 43
    jmp thread_exit
cycle_parent_exit:
    mov ecx, 42
    jmp thread_exit
cycle_worker:
    JOIN rcx
    cmp rax, WIT_STATUS_DEADLOCK
    je cycle_worker_exit
    EXPECT WIT_STATUS_OK
    cmp rdx, 42
    jne failed
cycle_worker_exit:
    mov ecx, 43
    jmp thread_exit

capacity_main:
    CREATE capacity_worker, 1
    EXPECT WIT_STATUS_OK
    mov r12, rdx
    CREATE capacity_worker, 2
    EXPECT WIT_STATUS_OK
    mov r13, rdx
    CREATE capacity_worker, 3
    EXPECT WIT_STATUS_OK
    mov r14, rdx
    CREATE capacity_worker, 4
    EXPECT WIT_STATUS_NO_MEMORY
    test rdx, rdx
    jne failed
    mov rbx, WIT_USER_DATA
    mov QWORD PTR [rbx + 200h], 1
    JOIN r12
    EXPECT WIT_STATUS_OK
    cmp rdx, 1
    jne failed
    JOIN r13
    EXPECT WIT_STATUS_OK
    cmp rdx, 2
    jne failed
    JOIN r14
    EXPECT WIT_STATUS_OK
    cmp rdx, 3
    jne failed
    mov ecx, WIT_TEST_EXIT_CODE
    jmp process_exit
capacity_worker:
    mov r12, rcx
    mov rbx, WIT_USER_DATA
capacity_loop:
    cmp QWORD PTR [rbx + 200h], 0
    jne capacity_exit
    mov eax, WIT_CALL_THREAD_YIELD
    int 80h
    EXPECT WIT_STATUS_OK
    jmp capacity_loop
capacity_exit:
    mov rcx, r12
    jmp thread_exit

fault_main:
    CREATE fault_worker, r14
    EXPECT WIT_STATUS_OK
    JOIN rdx
    jmp failed
fault_worker:
    mov rbx, QWORD PTR fs:[WIT_TLS_SELF_OFFSET]
    cmp rcx, WIT_THREAD_TEST_GUARD_LOW
    je thread_guard_low
    cmp rcx, WIT_THREAD_TEST_GUARD_HIGH
    je thread_guard_high
    cmp rcx, WIT_THREAD_TEST_BAD_RETURN
    je thread_bad_return
    cmp rcx, WIT_THREAD_TEST_PROCESS_EXIT
    je thread_process_exit
    ud2
thread_guard_low:
    mov BYTE PTR [rbx - 6001h], 1
    jmp failed
thread_guard_high:
    mov BYTE PTR [rbx - 2000h], 1
    jmp failed
thread_bad_return:
    mov rsp, WIT_USER_STACK_TOP - 40 ; mapped, but belongs to a different thread
    mov eax, WIT_CALL_QUERY
    int 80h
    jmp failed
thread_process_exit:
    mov ecx, WIT_TEST_EXIT_CODE
    jmp process_exit

failed:
    mov ecx, 241
process_exit:
    mov eax, WIT_CALL_EXIT
    int 80h
    ud2
thread_exit:
    mov eax, WIT_CALL_THREAD_EXIT
    int 80h
    ud2
wit_user_start ENDP
END
