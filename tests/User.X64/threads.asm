option casemap:none
include user_abi.inc

; Thread fixture over the ABI-1 thread family of RFC 0011 v3 (plan step K1.2): THREAD_CREATE is the one form, a join
; is OBJECT_WAIT on the thread handle followed by THREAD_QUERY for the exit code and HANDLE_CLOSE, and closing the
; handle of a live thread detaches it. The creation and the join are subroutines so that the fixture stays within
; its one page of code.

EXPECT MACRO value
    cmp rax, value
    jne failed
ENDM
; Create a thread: entry, argument -> rax status, rdx handle.
CREATE MACRO target, argument
    lea rcx, target
    mov rdx, argument
    xor r8d, r8d
    call thread_create
ENDM
; Join: handle -> rax status; rdx exit code and the handle closed on success.
JOIN MACRO handle
    mov rcx, handle
    call thread_join
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
    cmp r14, WIT_THREAD_TEST_CAPACITY
    je capacity_main
    cmp r14, WIT_THREAD_TEST_FAULT
    jae fault_main

    ; Wrong type, foreign handles, the private identity and closing it are errors.
    JOIN QWORD PTR [r15 + 8]
    EXPECT WIT_STATUS_WRONG_TYPE
    JOIN QWORD PTR [r15 + WIT_TEST_FOREIGN_OFFSET]
    EXPECT WIT_STATUS_BAD_HANDLE
    JOIN QWORD PTR fs:[WIT_TLS_HANDLE_OFFSET]
    EXPECT WIT_STATUS_WRONG_TYPE
    mov rcx, QWORD PTR fs:[WIT_TLS_HANDLE_OFFSET]
    mov eax, WIT_CALL_HANDLE_CLOSE
    int 80h
    EXPECT WIT_STATUS_BUSY
    mov rcx, WIT_USER_DATA
    xor edx, edx
    xor r8d, r8d
    call thread_create
    EXPECT WIT_STATUS_BAD_ADDRESS
    lea rcx, worker
    xor edx, edx
    mov r8d, 4 ; bit 2 is reserved; bits 0 and 1 are START_SUSPENDED and LIBRARY_NOTIFICATIONS
    call thread_create
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
    ; Reuse a reaped slot: fresh TLS/stack and a different handle generation.
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
    ; Closing the handle of a live thread detaches it; the thread runs to its exit and is reaped there.
    CREATE worker, 4
    EXPECT WIT_STATUS_OK
    mov r13, rdx
    mov rcx, r13
    mov eax, WIT_CALL_HANDLE_CLOSE
    int 80h
    EXPECT WIT_STATUS_OK
    JOIN r13
    EXPECT WIT_STATUS_BAD_HANDLE
    mov eax, WIT_CALL_THREAD_YIELD
    int 80h
    EXPECT WIT_STATUS_OK
    mov ecx, WIT_TEST_EXIT_CODE
    jmp process_exit

worker:
    ; No calls/prologue have touched the new stack. Check the entire configured stack and TLS payload.
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
    lea rdi, [rbx - (WIT_USER_TLS - WIT_USER_STACK_BOTTOM)]
    mov ecx, (WIT_USER_STACK_TOP - WIT_USER_STACK_BOTTOM) / 8
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
    mov BYTE PTR [rbx - (WIT_USER_TLS - WIT_USER_STACK_BOTTOM) - 1], 1
    jmp failed
thread_guard_high:
    mov BYTE PTR [rbx - (WIT_USER_TLS - WIT_USER_STACK_TOP)], 1
    jmp failed
thread_bad_return:
    mov rsp, WIT_USER_STACK_TOP - 40 ; mapped, but belongs to a different thread
    mov eax, WIT_CALL_QUERY
    int 80h
    jmp failed
thread_process_exit:
    mov ecx, WIT_TEST_EXIT_CODE
    jmp process_exit

; THREAD_CREATE with a request on the stack: rcx entry, rdx argument, r8 flags -> rax status, rdx handle.
thread_create:
    sub rsp, 56
    mov DWORD PTR [rsp], WIT_THREAD_CREATE_VERSION
    mov DWORD PTR [rsp + 4], 48
    mov [rsp + 8], rcx
    mov [rsp + 16], rdx
    xor eax, eax
    mov [rsp + 24], rax
    mov [rsp + 32], rax
    mov [rsp + 40], r8d
    mov DWORD PTR [rsp + 44], 0
    mov rcx, rsp
    mov edx, 48
    xor r8d, r8d
    mov eax, WIT_CALL_THREAD_CREATE
    int 80h
    add rsp, 56
    ret

; Wait for the thread, read its exit code and close the handle: rcx handle -> rax status, rdx exit code. A failed
; wait returns its status and leaves the handle.
thread_join:
    sub rsp, 152
    mov [rsp + 32], rcx
    lea rax, [rsp + 32]
    mov DWORD PTR [rsp], WIT_WAIT_OBJECTS_VERSION
    mov DWORD PTR [rsp + 4], 32
    mov [rsp + 8], rax
    mov DWORD PTR [rsp + 16], 1
    mov DWORD PTR [rsp + 20], 0
    mov rax, WIT_WAIT_INFINITE
    mov [rsp + 24], rax
    mov rcx, rsp
    mov edx, 32
    xor r8d, r8d
    mov eax, WIT_CALL_OBJECT_WAIT
    int 80h
    test rax, rax
    jne thread_join_done
    mov DWORD PTR [rsp + 40], WIT_THREAD_INFO_VERSION
    mov DWORD PTR [rsp + 44], WIT_THREAD_INFO_SIZE
    mov rcx, [rsp + 32]
    lea rdx, [rsp + 40]
    mov r8d, WIT_THREAD_INFO_SIZE
    mov eax, WIT_CALL_THREAD_QUERY
    int 80h
    test rax, rax
    jne thread_join_done
    mov rcx, [rsp + 32]
    mov eax, WIT_CALL_HANDLE_CLOSE
    int 80h
    mov rdx, [rsp + 40 + 72] ; ExitCode
thread_join_done:
    add rsp, 152
    ret

failed:
    mov ecx, 241
process_exit:
    mov eax, WIT_CALL_PROCESS_EXIT
    int 80h
    ud2
thread_exit:
    mov eax, WIT_CALL_THREAD_EXIT
    int 80h
    ud2
wit_user_start ENDP
END
