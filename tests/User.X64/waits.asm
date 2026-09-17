option casemap:none
include user_abi.inc

EXPECT MACRO value
    cmp rax, value
    jne failed
ENDM
CALL0 MACRO operation
    mov eax, operation
    int 80h
ENDM
CREATE_EVENT MACRO flags
    mov rcx, flags
    CALL0 WIT_CALL_EVENT_CREATE
    EXPECT WIT_STATUS_OK
ENDM
SET_EVENT MACRO handle
    mov rcx, handle
    CALL0 WIT_CALL_EVENT_SET
    EXPECT WIT_STATUS_OK
ENDM
WAIT_EVENT MACRO handle, deadline
    mov rcx, handle
    mov rdx, deadline
    CALL0 WIT_CALL_EVENT_WAIT
ENDM
CLOSE MACRO handle
    mov rcx, handle
    CALL0 WIT_CALL_CLOSE
    EXPECT WIT_STATUS_OK
ENDM
CREATE_THREAD MACRO target, argument
    lea rcx, target
    mov rdx, argument
    xor r8d, r8d
    CALL0 WIT_CALL_THREAD_CREATE
    EXPECT WIT_STATUS_OK
ENDM
JOIN MACRO handle
    mov rcx, handle
    CALL0 WIT_CALL_THREAD_JOIN
    EXPECT WIT_STATUS_OK
    cmp rdx, WIT_TEST_EXIT_CODE
    jne failed
ENDM
SLEEP_TICKS MACRO ticks
    CALL0 WIT_CALL_CLOCK_READ
    EXPECT WIT_STATUS_OK
    lea rcx, [rdx + ticks]
    CALL0 WIT_CALL_THREAD_SLEEP
    EXPECT WIT_STATUS_OK
ENDM

.code
PUBLIC wit_user_start
wit_user_start PROC
    mov r15, rcx
    mov rbx, WIT_USER_DATA
    cmp DWORD PTR [r15], WIT_ABI_VERSION
    jne failed
    mov r14, [r15 + WIT_TEST_MODE_OFFSET]
    cmp r14, WIT_WAIT_TEST_CLOCK
    je clock_test
    cmp r14, WIT_WAIT_TEST_AUTO
    je wake_test
    cmp r14, WIT_WAIT_TEST_MANUAL
    je wake_test
    cmp r14, WIT_WAIT_TEST_CLOSE
    je wake_test
    cmp r14, WIT_WAIT_TEST_HANDOFF
    je handoff_test
    cmp r14, WIT_WAIT_TEST_DEADLINE
    je deadline_test
    cmp r14, WIT_WAIT_TEST_EXIT
    je wake_test
    cmp r14, WIT_WAIT_TEST_BUDGET
    je budget_test
    cmp r14, WIT_WAIT_TEST_RIGHTS
    je rights_test
    cmp r14, WIT_WAIT_TEST_ACTIVE_TIMEOUT
    je active_timeout_test
    cmp r14, WIT_WAIT_TEST_JOIN_CHAIN
    je chain_test
    cmp r14, WIT_WAIT_TEST_SIGNAL_STATE
    jne failed

    mov rcx, 100000000h ; reject unknown high flag bits
    CALL0 WIT_CALL_EVENT_CREATE
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    test rdx, rdx
    jne failed
    CREATE_EVENT 0
    mov r12, rdx
    WAIT_EVENT r12, 0
    EXPECT WIT_STATUS_TIMED_OUT
    SET_EVENT r12
    SET_EVENT r12 ; auto signals coalesce while unclaimed
    WAIT_EVENT r12, 0
    EXPECT WIT_STATUS_OK
    WAIT_EVENT r12, 0
    EXPECT WIT_STATUS_TIMED_OUT
    CLOSE r12
    WAIT_EVENT r12, 0
    EXPECT WIT_STATUS_BAD_HANDLE
    CREATE_EVENT WIT_EVENT_MANUAL_RESET + WIT_EVENT_INITIAL_SIGNALED
    mov r13, rdx
    cmp r13, r12
    je failed
    WAIT_EVENT r13, 0
    EXPECT WIT_STATUS_OK
    WAIT_EVENT r13, 0
    EXPECT WIT_STATUS_OK
    mov rcx, r13
    CALL0 WIT_CALL_EVENT_RESET
    EXPECT WIT_STATUS_OK
    WAIT_EVENT r13, 0
    EXPECT WIT_STATUS_TIMED_OUT
    SET_EVENT r13
    WAIT_EVENT r13, 0
    EXPECT WIT_STATUS_OK
    CLOSE r13
    jmp passed

clock_test:
    CALL0 WIT_CALL_CLOCK_FREQUENCY
    EXPECT WIT_STATUS_OK
    cmp rdx, WIT_CLOCK_FREQUENCY
    jne failed
    CALL0 WIT_CALL_CLOCK_READ
    EXPECT WIT_STATUS_OK
    mov rbp, rdx
    lea r13, [rdx + 2]
    mov rcx, r13
    CALL0 WIT_CALL_THREAD_SLEEP
    EXPECT WIT_STATUS_OK
    CALL0 WIT_CALL_CLOCK_READ
    EXPECT WIT_STATUS_OK
    cmp rdx, r13
    jb failed
    mov rcx, rbp ; past deadlines complete immediately
    CALL0 WIT_CALL_THREAD_SLEEP
    EXPECT WIT_STATUS_OK
    mov rcx, WIT_WAIT_INFINITE
    CALL0 WIT_CALL_THREAD_SLEEP
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    CREATE_EVENT 0
    mov r12, rdx
    CALL0 WIT_CALL_CLOCK_READ
    lea r13, [rdx + 2]
    WAIT_EVENT r12, r13
    EXPECT WIT_STATUS_TIMED_OUT
    test rdx, rdx
    jne failed
    CALL0 WIT_CALL_CLOCK_READ
    cmp rdx, r13
    jb failed
    CLOSE r12
    jmp passed

wake_test:
    xor ecx, ecx
    cmp r14, WIT_WAIT_TEST_MANUAL
    jne make_wake_event
    mov ecx, WIT_EVENT_MANUAL_RESET
make_wake_event:
    CALL0 WIT_CALL_EVENT_CREATE
    EXPECT WIT_STATUS_OK
    mov [rbx + 80h], rdx
    CREATE_THREAD wait_worker, 1
    mov [rbx + 40h], rdx
    CREATE_THREAD wait_worker, 2
    mov [rbx + 48h], rdx
    CREATE_THREAD wait_worker, 3
    mov [rbx + 50h], rdx
    SLEEP_TICKS 2
    cmp r14, WIT_WAIT_TEST_EXIT
    je passed
    cmp r14, WIT_WAIT_TEST_CLOSE
    je close_waking
    SET_EVENT QWORD PTR [rbx + 80h]
    cmp r14, WIT_WAIT_TEST_MANUAL
    je join_workers
    mov r12, 1
auto_progress:
    CALL0 WIT_CALL_THREAD_YIELD
    EXPECT WIT_STATUS_OK
    cmp [rbx], r12
    jb auto_progress
    jne failed ; one auto signal must release exactly one worker
    cmp r12, 3
    je join_workers
    inc r12
    SET_EVENT QWORD PTR [rbx + 80h]
    jmp auto_progress
close_waking:
    CLOSE QWORD PTR [rbx + 80h]
    CREATE_EVENT WIT_EVENT_INITIAL_SIGNALED
    mov r13, rdx
    cmp r13, [rbx + 80h]
    je failed
    WAIT_EVENT r13, 0
    EXPECT WIT_STATUS_OK
    CLOSE r13
join_workers:
    JOIN QWORD PTR [rbx + 40h]
    JOIN QWORD PTR [rbx + 48h]
    JOIN QWORD PTR [rbx + 50h]
    cmp QWORD PTR [rbx], 3
    jne failed
    cmp r14, WIT_WAIT_TEST_CLOSE
    je passed
    CLOSE QWORD PTR [rbx + 80h]
    jmp passed

wait_worker:
    mov r12, rcx
    mov rbx, WIT_USER_DATA
    mov r15, WIT_USER_INFO
    mov QWORD PTR fs:[WIT_TLS_DATA_OFFSET], r12
    movq xmm6, r12
    mov r13, [rbx + 80h]
    WAIT_EVENT r13, WIT_WAIT_INFINITE
    mov r14, [r15 + WIT_TEST_MODE_OFFSET]
    cmp r14, WIT_WAIT_TEST_CLOSE
    jne ordinary_wake
    EXPECT WIT_STATUS_CLOSED
    jmp check_wait_state
ordinary_wake:
    EXPECT WIT_STATUS_OK
check_wait_state:
    test rdx, rdx
    jne failed
    cmp QWORD PTR fs:[WIT_TLS_DATA_OFFSET], r12
    jne failed
    movq rax, xmm6
    cmp rax, r12
    jne failed
    lock inc QWORD PTR [rbx] ; shared user counter also survives preemption
    jmp thread_passed

handoff_test:
    CREATE_EVENT 0
    mov [rbx + 80h], rdx
    CREATE_EVENT 0
    mov [rbx + 88h], rdx
    CREATE_THREAD handoff_worker, 0
    mov r13, rdx
    mov r12d, 32
handoff_parent_loop:
    SET_EVENT QWORD PTR [rbx + 80h]
    WAIT_EVENT QWORD PTR [rbx + 88h], WIT_WAIT_INFINITE
    EXPECT WIT_STATUS_OK
    dec r12
    jne handoff_parent_loop
    JOIN r13
    CLOSE QWORD PTR [rbx + 80h]
    CLOSE QWORD PTR [rbx + 88h]
    jmp passed
handoff_worker:
    mov rbx, WIT_USER_DATA
    mov r12d, 32
handoff_child_loop:
    WAIT_EVENT QWORD PTR [rbx + 80h], WIT_WAIT_INFINITE
    EXPECT WIT_STATUS_OK
    SET_EVENT QWORD PTR [rbx + 88h]
    dec r12
    jne handoff_child_loop
    jmp thread_passed

deadline_test:
    CREATE_EVENT 0
    mov [rbx + 80h], rdx
    CALL0 WIT_CALL_CLOCK_READ
    lea rax, [rdx + 2]
    mov [rbx + 90h], rax
    CREATE_THREAD deadline_signaler, 0
    mov r13, rdx
    WAIT_EVENT QWORD PTR [rbx + 80h], QWORD PTR [rbx + 90h]
    EXPECT WIT_STATUS_TIMED_OUT
    JOIN r13
    WAIT_EVENT QWORD PTR [rbx + 80h], 0
    EXPECT WIT_STATUS_OK ; timeout did not consume the later signal
    CLOSE QWORD PTR [rbx + 80h]
    jmp passed
deadline_signaler:
    mov rbx, WIT_USER_DATA
    mov rcx, [rbx + 90h]
    CALL0 WIT_CALL_THREAD_SLEEP
    EXPECT WIT_STATUS_OK
    SET_EVENT QWORD PTR [rbx + 80h]
    jmp thread_passed

active_timeout_test:
    CREATE_EVENT 0
    mov [rbx + 80h], rdx
    CREATE_THREAD spin_worker, 0
    mov r13, rdx
    CALL0 WIT_CALL_CLOCK_READ
    lea r12, [rdx + 2]
    WAIT_EVENT QWORD PTR [rbx + 80h], r12
    EXPECT WIT_STATUS_TIMED_OUT
    mov QWORD PTR [rbx + 98h], 1
    JOIN r13
    CLOSE QWORD PTR [rbx + 80h]
    jmp passed
spin_worker:
    mov rbx, WIT_USER_DATA
spin_loop:
    pause
    cmp QWORD PTR [rbx + 98h], 0
    je spin_loop
    jmp thread_passed

chain_test:
    CREATE_EVENT 0
    mov [rbx + 80h], rdx
    CREATE_THREAD wait_worker, 1
    mov r13, rdx
    CREATE_THREAD delayed_signaler, 0
    mov r14, rdx
    JOIN r13
    JOIN r14
    CLOSE QWORD PTR [rbx + 80h]
    jmp passed
delayed_signaler:
    mov rbx, WIT_USER_DATA
    SLEEP_TICKS 2
    SET_EVENT QWORD PTR [rbx + 80h]
    jmp thread_passed

budget_test:
    CREATE_EVENT 0
    WAIT_EVENT rdx, WIT_WAIT_INFINITE
    jmp failed

rights_test:
    WAIT_EVENT QWORD PTR [r15 + 8], 0
    EXPECT WIT_STATUS_WRONG_TYPE
    mov rcx, [r15 + WIT_TEST_RO_OFFSET]
    CALL0 WIT_CALL_EVENT_SET
    EXPECT WIT_STATUS_DENIED
    mov rcx, [r15 + WIT_TEST_RO_OFFSET]
    CALL0 WIT_CALL_EVENT_RESET
    EXPECT WIT_STATUS_DENIED
    WAIT_EVENT QWORD PTR [r15 + WIT_TEST_RO_OFFSET], 0
    EXPECT WIT_STATUS_TIMED_OUT
    WAIT_EVENT QWORD PTR [r15 + WIT_TEST_SELF_OFFSET], 0
    EXPECT WIT_STATUS_DENIED
    mov rcx, [r15 + WIT_TEST_FOREIGN_OFFSET]
    CALL0 WIT_CALL_EVENT_SET
    EXPECT WIT_STATUS_BAD_HANDLE
    WAIT_EVENT QWORD PTR [r15 + WIT_TEST_FOREIGN_OFFSET], 0
    EXPECT WIT_STATUS_BAD_HANDLE
    CLOSE QWORD PTR [r15 + WIT_TEST_RO_OFFSET]
    WAIT_EVENT QWORD PTR [r15 + WIT_TEST_RO_OFFSET], 0
    EXPECT WIT_STATUS_BAD_HANDLE
    jmp passed

failed:
    mov ecx, 241
    jmp exit_process
passed:
    mov ecx, WIT_TEST_EXIT_CODE
exit_process:
    CALL0 WIT_CALL_EXIT
    ud2
thread_passed:
    mov ecx, WIT_TEST_EXIT_CODE
    CALL0 WIT_CALL_THREAD_EXIT
    ud2
wit_user_start ENDP
END
