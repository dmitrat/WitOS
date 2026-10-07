option casemap:none
include user_abi.inc

; Event, sleep and wait fixture over the ABI-1 wait of RFC 0011 v3: one OBJECT_WAIT for every object, absolute
; monotonic deadlines, EVENT_CREATE with rights 0 (WAIT and SIGNAL). Delays are counted in scheduler ticks of 10 ms
; converted through CLOCK_FREQUENCY, never in a tick count of the clock itself. The wait and the deadline arithmetic
; are subroutines so that the fixture stays within its one page of code.

EXPECT MACRO value
    cmp rax, value
    jne failed
ENDM
CALL0 MACRO operation
    mov eax, operation
    int 80h
ENDM
; Monotonic clock: counter or frequency into rdx.
CLOCK_READ MACRO
    xor ecx, ecx
    xor edx, edx
    xor r8d, r8d
    CALL0 WIT_CALL_CLOCK_READ
    EXPECT WIT_STATUS_OK
ENDM
CLOCK_FREQUENCY MACRO
    xor ecx, ecx
    xor edx, edx
    xor r8d, r8d
    CALL0 WIT_CALL_CLOCK_FREQUENCY
    EXPECT WIT_STATUS_OK
ENDM
CREATE_EVENT MACRO flags
    mov rcx, flags
    xor edx, edx ; rights 0: WAIT and SIGNAL
    xor r8d, r8d
    CALL0 WIT_CALL_EVENT_CREATE
    EXPECT WIT_STATUS_OK
ENDM
SET_EVENT MACRO handle
    mov rcx, handle
    CALL0 WIT_CALL_EVENT_SET
    EXPECT WIT_STATUS_OK
ENDM
; OBJECT_WAIT on one handle; rax is the status and rdx the winner index afterward.
WAIT_EVENT MACRO handle, deadline
    mov rcx, handle
    mov rdx, deadline
    call wait_object
ENDM
CLOSE MACRO handle
    mov rcx, handle
    CALL0 WIT_CALL_HANDLE_CLOSE
    EXPECT WIT_STATUS_OK
ENDM
; THREAD_CREATE: rdx receives the thread handle.
CREATE_THREAD MACRO target, argument
    lea rcx, target
    mov rdx, argument
    xor r8d, r8d
    call thread_create
    EXPECT WIT_STATUS_OK
ENDM
; Wait for the thread, read its exit code and close the handle.
JOIN MACRO handle
    mov rcx, handle
    call thread_join
    EXPECT WIT_STATUS_OK
    cmp rdx, WIT_TEST_EXIT_CODE
    jne failed
ENDM
; Absolute deadline the given number of ticks from now into r9.
DEADLINE_TICKS MACRO ticks
    mov ecx, ticks
    call deadline_ticks
ENDM
SLEEP_TICKS MACRO ticks
    DEADLINE_TICKS ticks
    mov rcx, r9
    xor edx, edx
    xor r8d, r8d
    CALL0 WIT_CALL_SLEEP_UNTIL
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
    xor edx, edx
    xor r8d, r8d
    CALL0 WIT_CALL_EVENT_CREATE
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    test rdx, rdx
    jne failed
    xor ecx, ecx
    mov edx, 16 ; rights outside WAIT and SIGNAL
    xor r8d, r8d
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
    CLOCK_FREQUENCY
    test rdx, rdx ; the monotonic clock reports its frequency
    je failed
    mov ecx, WIT_CLOCK_UTC ; UTC arrives with plan step K6
    xor edx, edx
    xor r8d, r8d
    CALL0 WIT_CALL_CLOCK_READ
    EXPECT WIT_STATUS_UNSUPPORTED
    mov ecx, 2 ; no third clock
    xor edx, edx
    xor r8d, r8d
    CALL0 WIT_CALL_CLOCK_READ
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    CLOCK_READ
    mov r12, rdx
    DEADLINE_TICKS 2
    mov r13, r9
    mov rcx, r13
    xor edx, edx
    xor r8d, r8d
    CALL0 WIT_CALL_SLEEP_UNTIL
    EXPECT WIT_STATUS_OK
    CLOCK_READ
    cmp rdx, r13
    jb failed
    mov rcx, r12 ; past deadlines complete immediately
    xor edx, edx
    xor r8d, r8d
    CALL0 WIT_CALL_SLEEP_UNTIL
    EXPECT WIT_STATUS_OK
    mov rcx, 8000000000000000h ; beyond WIT_MONOTONIC_MAX and not infinite
    xor edx, edx
    xor r8d, r8d
    CALL0 WIT_CALL_SLEEP_UNTIL
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    CREATE_EVENT 0
    mov r12, rdx
    DEADLINE_TICKS 2
    mov r13, r9
    WAIT_EVENT r12, r13
    EXPECT WIT_STATUS_TIMED_OUT
    test rdx, rdx
    jne failed
    CLOCK_READ
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
    xor edx, edx
    xor r8d, r8d
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
    DEADLINE_TICKS 2
    mov [rbx + 90h], r9
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
    xor edx, edx
    xor r8d, r8d
    CALL0 WIT_CALL_SLEEP_UNTIL
    EXPECT WIT_STATUS_OK
    SET_EVENT QWORD PTR [rbx + 80h]
    jmp thread_passed

active_timeout_test:
    CREATE_EVENT 0
    mov [rbx + 80h], rdx
    CREATE_THREAD spin_worker, 0
    mov r13, rdx
    DEADLINE_TICKS 2
    mov r12, r9
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
    CALL0 WIT_CALL_THREAD_CREATE
    add rsp, 56
    ret

; Wait for the thread, read its exit code and close the handle: rcx handle -> rax status, rdx exit code.
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
    CALL0 WIT_CALL_OBJECT_WAIT
    test rax, rax
    jne thread_join_done
    mov DWORD PTR [rsp + 40], WIT_THREAD_INFO_VERSION
    mov DWORD PTR [rsp + 44], WIT_THREAD_INFO_SIZE
    mov rcx, [rsp + 32]
    lea rdx, [rsp + 40]
    mov r8d, WIT_THREAD_INFO_SIZE
    CALL0 WIT_CALL_THREAD_QUERY
    test rax, rax
    jne thread_join_done
    mov rcx, [rsp + 32]
    CALL0 WIT_CALL_HANDLE_CLOSE
    mov rdx, [rsp + 40 + 72] ; ExitCode
thread_join_done:
    add rsp, 152
    ret

; OBJECT_WAIT on one handle: rcx handle, rdx deadline; rax status, rdx winner. The request and its handle array live
; on the caller's own stack, so concurrent waiters never share them. Clobbers r8 and r9.
wait_object:
    sub rsp, 56
    mov [rsp + 40], rcx
    lea r9, [rsp + 40]
    mov DWORD PTR [rsp], WIT_WAIT_OBJECTS_VERSION
    mov DWORD PTR [rsp + 4], 32
    mov [rsp + 8], r9
    mov DWORD PTR [rsp + 16], 1
    mov DWORD PTR [rsp + 20], 0
    mov [rsp + 24], rdx
    mov rcx, rsp
    mov edx, 32
    xor r8d, r8d
    CALL0 WIT_CALL_OBJECT_WAIT
    add rsp, 56
    ret

; Absolute monotonic deadline rcx ticks of 10 ms from now, into r9. Clobbers rax, rcx, rdx, r8 and r10.
deadline_ticks:
    mov r10, rcx
    CLOCK_FREQUENCY
    mov rax, rdx
    mov ecx, 100
    xor edx, edx
    div rcx
    imul rax, r10
    mov r9, rax
    CLOCK_READ
    add r9, rdx
    ret

failed:
    mov ecx, 241
    jmp exit_process
passed:
    mov ecx, WIT_TEST_EXIT_CODE
exit_process:
    CALL0 WIT_CALL_PROCESS_EXIT
    ud2
thread_passed:
    mov ecx, WIT_TEST_EXIT_CODE
    CALL0 WIT_CALL_THREAD_EXIT
    ud2
wit_user_start ENDP
END
