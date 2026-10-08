#include "user_abi_a64.h"
; ARM64 event, sleep and wait fixture, the port of tests/User.X64/waits.asm: one OBJECT_WAIT for every object,
; absolute monotonic deadlines, EVENT_CREATE with rights 0 (WAIT and SIGNAL). Delays are scheduler ticks of 10 ms
; converted through CLOCK_FREQUENCY. The wait and the deadline arithmetic are subroutines so that the fixture stays
; within its one page of code. Registers: x20 startup block, x21 test mode, x22 the shared data page, x23 to x25
; handles, counters and deadlines.

    AREA |.text|, CODE, READONLY

    MACRO
    SYSCALL $number
    mov x8, #$number
    svc #0
    MEND

    MACRO
    EXPECT $status
    cmp x0, #$status
    b.ne failed
    MEND

    ; Monotonic clock: the counter or the frequency into x1.
    MACRO
    CLOCK_READ
    mov x0, #0
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_CLOCK_READ
    EXPECT WIT_STATUS_OK
    MEND

    MACRO
    CLOCK_FREQUENCY
    mov x0, #0
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_CLOCK_FREQUENCY
    EXPECT WIT_STATUS_OK
    MEND

    MACRO
    CREATE_EVENT $flags
    mov x0, $flags
    mov x1, #0 ; rights 0: WAIT and SIGNAL
    mov x2, #0
    SYSCALL WIT_CALL_EVENT_CREATE
    EXPECT WIT_STATUS_OK
    MEND

    MACRO
    SET_EVENT $handle
    mov x0, $handle
    SYSCALL WIT_CALL_EVENT_SET
    EXPECT WIT_STATUS_OK
    MEND

    ; OBJECT_WAIT on one handle; x0 is the status and x1 the winner index afterward.
    MACRO
    WAIT_EVENT $handle, $deadline
    mov x0, $handle
    mov x1, $deadline
    bl wait_object
    MEND

    MACRO
    CLOSE $handle
    mov x0, $handle
    SYSCALL WIT_CALL_HANDLE_CLOSE
    EXPECT WIT_STATUS_OK
    MEND

    ; THREAD_CREATE: x1 receives the thread handle.
    MACRO
    CREATE_THREAD $target, $argument
    adr x0, $target
    mov x1, $argument
    mov x2, #0
    bl thread_create
    EXPECT WIT_STATUS_OK
    MEND

    ; Wait for the thread, read its exit code and close the handle.
    MACRO
    JOIN $handle
    mov x0, $handle
    bl thread_join
    EXPECT WIT_STATUS_OK
    cmp x1, #WIT_TEST_EXIT_CODE
    b.ne failed
    MEND

    ; Absolute deadline the given number of ticks from now into x9.
    MACRO
    DEADLINE_TICKS $ticks
    mov x0, #$ticks
    bl deadline_ticks
    MEND

    MACRO
    SLEEP_TICKS $ticks
    DEADLINE_TICKS $ticks
    mov x0, x9
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_SLEEP_UNTIL
    EXPECT WIT_STATUS_OK
    MEND

    EXPORT wit_user_start

wit_user_start PROC
    mov x20, x0
    ldr x22, =WIT_USER_DATA
    ldr w9, [x20]
    cmp w9, #WIT_ABI_VERSION
    b.ne failed
    ldr x21, [x20, #WIT_TEST_MODE_OFFSET]
    cmp x21, #WIT_WAIT_TEST_CLOCK
    b.eq clock_test
    cmp x21, #WIT_WAIT_TEST_AUTO
    b.eq wake_test
    cmp x21, #WIT_WAIT_TEST_MANUAL
    b.eq wake_test
    cmp x21, #WIT_WAIT_TEST_CLOSE
    b.eq wake_test
    cmp x21, #WIT_WAIT_TEST_HANDOFF
    b.eq handoff_test
    cmp x21, #WIT_WAIT_TEST_DEADLINE
    b.eq deadline_test
    cmp x21, #WIT_WAIT_TEST_EXIT
    b.eq wake_test
    cmp x21, #WIT_WAIT_TEST_BUDGET
    b.eq budget_test
    cmp x21, #WIT_WAIT_TEST_RIGHTS
    b.eq rights_test
    cmp x21, #WIT_WAIT_TEST_ACTIVE_TIMEOUT
    b.eq active_timeout_test
    cmp x21, #WIT_WAIT_TEST_JOIN_CHAIN
    b.eq chain_test
    cmp x21, #WIT_WAIT_TEST_SIGNAL_STATE
    b.ne failed

    ldr x0, =0x100000000 ; reject unknown high flag bits
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_EVENT_CREATE
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    cbnz x1, failed
    mov x0, #0
    mov x1, #16 ; rights outside WAIT and SIGNAL
    mov x2, #0
    SYSCALL WIT_CALL_EVENT_CREATE
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    cbnz x1, failed
    CREATE_EVENT #0
    mov x23, x1
    WAIT_EVENT x23, #0
    EXPECT WIT_STATUS_TIMED_OUT
    SET_EVENT x23
    SET_EVENT x23 ; auto signals coalesce while unclaimed
    WAIT_EVENT x23, #0
    EXPECT WIT_STATUS_OK
    WAIT_EVENT x23, #0
    EXPECT WIT_STATUS_TIMED_OUT
    CLOSE x23
    WAIT_EVENT x23, #0
    EXPECT WIT_STATUS_BAD_HANDLE
    CREATE_EVENT #WIT_EVENT_MANUAL_RESET + WIT_EVENT_INITIAL_SIGNALED
    mov x24, x1
    cmp x24, x23
    b.eq failed
    WAIT_EVENT x24, #0
    EXPECT WIT_STATUS_OK
    WAIT_EVENT x24, #0
    EXPECT WIT_STATUS_OK
    mov x0, x24
    SYSCALL WIT_CALL_EVENT_RESET
    EXPECT WIT_STATUS_OK
    WAIT_EVENT x24, #0
    EXPECT WIT_STATUS_TIMED_OUT
    SET_EVENT x24
    WAIT_EVENT x24, #0
    EXPECT WIT_STATUS_OK
    CLOSE x24
    b passed

clock_test
    CLOCK_FREQUENCY
    cbz x1, failed ; the monotonic clock reports its frequency
    mov x0, #WIT_CLOCK_UTC ; UTC (K6): nanoseconds since 1970, past 2026-01-01 on a board whose clock is set
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_CLOCK_READ
    EXPECT WIT_STATUS_OK
    ldr x10, =1767225600000000000
    cmp x1, x10
    b.lo failed
    mov x0, #2 ; no third clock
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_CLOCK_READ
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    CLOCK_READ
    mov x25, x1
    DEADLINE_TICKS 2
    mov x24, x9
    mov x0, x24
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_SLEEP_UNTIL
    EXPECT WIT_STATUS_OK
    CLOCK_READ
    cmp x1, x24
    b.lo failed
    mov x0, x25 ; past deadlines complete immediately
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_SLEEP_UNTIL
    EXPECT WIT_STATUS_OK
    mov x0, #0x8000000000000000 ; beyond WIT_MONOTONIC_MAX and not infinite
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_SLEEP_UNTIL
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    CREATE_EVENT #0
    mov x23, x1
    DEADLINE_TICKS 2
    mov x24, x9
    WAIT_EVENT x23, x24
    EXPECT WIT_STATUS_TIMED_OUT
    cbnz x1, failed
    CLOCK_READ
    cmp x1, x24
    b.lo failed
    CLOSE x23
    b passed

wake_test
    mov x0, #0
    cmp x21, #WIT_WAIT_TEST_MANUAL
    b.ne make_wake_event
    mov x0, #WIT_EVENT_MANUAL_RESET
make_wake_event
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_EVENT_CREATE
    EXPECT WIT_STATUS_OK
    str x1, [x22, #0x80]
    CREATE_THREAD wait_worker, #1
    str x1, [x22, #0x40]
    CREATE_THREAD wait_worker, #2
    str x1, [x22, #0x48]
    CREATE_THREAD wait_worker, #3
    str x1, [x22, #0x50]
    SLEEP_TICKS 2
    cmp x21, #WIT_WAIT_TEST_EXIT
    b.eq passed
    cmp x21, #WIT_WAIT_TEST_CLOSE
    b.eq close_waking
    ldr x9, [x22, #0x80]
    SET_EVENT x9
    cmp x21, #WIT_WAIT_TEST_MANUAL
    b.eq join_workers
    mov x23, #1
auto_progress
    SYSCALL WIT_CALL_THREAD_YIELD
    EXPECT WIT_STATUS_OK
    ldr x9, [x22]
    cmp x9, x23
    b.lo auto_progress
    b.ne failed ; one auto signal must release exactly one worker
    cmp x23, #3
    b.eq join_workers
    add x23, x23, #1
    ldr x9, [x22, #0x80]
    SET_EVENT x9
    b auto_progress
close_waking
    ldr x9, [x22, #0x80]
    CLOSE x9
    CREATE_EVENT #WIT_EVENT_INITIAL_SIGNALED
    mov x24, x1
    ldr x9, [x22, #0x80]
    cmp x24, x9
    b.eq failed
    WAIT_EVENT x24, #0
    EXPECT WIT_STATUS_OK
    CLOSE x24
join_workers
    ldr x9, [x22, #0x40]
    JOIN x9
    ldr x9, [x22, #0x48]
    JOIN x9
    ldr x9, [x22, #0x50]
    JOIN x9
    ldr x9, [x22]
    cmp x9, #3
    b.ne failed
    cmp x21, #WIT_WAIT_TEST_CLOSE
    b.eq passed
    ldr x9, [x22, #0x80]
    CLOSE x9
    b passed

wait_worker
    mov x23, x0
    ldr x22, =WIT_USER_DATA
    ldr x20, =WIT_USER_INFO
    mrs x9, tpidrro_el0
    str x23, [x9, #WIT_TLS_DATA_OFFSET]
    fmov d8, x23
    ldr x24, [x22, #0x80]
    WAIT_EVENT x24, #-1
    ldr x21, [x20, #WIT_TEST_MODE_OFFSET]
    cmp x21, #WIT_WAIT_TEST_CLOSE
    b.ne ordinary_wake
    EXPECT WIT_STATUS_CLOSED
    b check_wait_state
ordinary_wake
    EXPECT WIT_STATUS_OK
check_wait_state
    cbnz x1, failed
    mrs x9, tpidrro_el0
    ldr x9, [x9, #WIT_TLS_DATA_OFFSET]
    cmp x9, x23
    b.ne failed
    fmov x9, d8
    cmp x9, x23
    b.ne failed
count_wake ; the shared counter also survives preemption between the exclusive load and store
    ldaxr x9, [x22]
    add x9, x9, #1
    stlxr w10, x9, [x22]
    cbnz w10, count_wake
    b thread_passed

handoff_test
    CREATE_EVENT #0
    str x1, [x22, #0x80]
    CREATE_EVENT #0
    str x1, [x22, #0x88]
    CREATE_THREAD handoff_worker, #0
    mov x24, x1
    mov x23, #32
handoff_parent_loop
    ldr x9, [x22, #0x80]
    SET_EVENT x9
    ldr x9, [x22, #0x88]
    WAIT_EVENT x9, #-1
    EXPECT WIT_STATUS_OK
    subs x23, x23, #1
    b.ne handoff_parent_loop
    JOIN x24
    ldr x9, [x22, #0x80]
    CLOSE x9
    ldr x9, [x22, #0x88]
    CLOSE x9
    b passed
handoff_worker
    ldr x22, =WIT_USER_DATA
    mov x23, #32
handoff_child_loop
    ldr x9, [x22, #0x80]
    WAIT_EVENT x9, #-1
    EXPECT WIT_STATUS_OK
    ldr x9, [x22, #0x88]
    SET_EVENT x9
    subs x23, x23, #1
    b.ne handoff_child_loop
    b thread_passed

deadline_test
    CREATE_EVENT #0
    str x1, [x22, #0x80]
    DEADLINE_TICKS 2
    str x9, [x22, #0x90]
    CREATE_THREAD deadline_signaler, #0
    mov x24, x1
    ldr x9, [x22, #0x80]
    ldr x10, [x22, #0x90]
    WAIT_EVENT x9, x10
    EXPECT WIT_STATUS_TIMED_OUT
    JOIN x24
    ldr x9, [x22, #0x80]
    WAIT_EVENT x9, #0
    EXPECT WIT_STATUS_OK ; the timeout did not consume the later signal
    ldr x9, [x22, #0x80]
    CLOSE x9
    b passed
deadline_signaler
    ldr x22, =WIT_USER_DATA
    ldr x0, [x22, #0x90]
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_SLEEP_UNTIL
    EXPECT WIT_STATUS_OK
    ldr x9, [x22, #0x80]
    SET_EVENT x9
    b thread_passed

active_timeout_test
    CREATE_EVENT #0
    str x1, [x22, #0x80]
    CREATE_THREAD spin_worker, #0
    mov x24, x1
    DEADLINE_TICKS 2
    mov x23, x9
    ldr x9, [x22, #0x80]
    WAIT_EVENT x9, x23
    EXPECT WIT_STATUS_TIMED_OUT
    mov x9, #1
    str x9, [x22, #0x98]
    JOIN x24
    ldr x9, [x22, #0x80]
    CLOSE x9
    b passed
spin_worker
    ldr x22, =WIT_USER_DATA
spin_loop
    yield
    ldr x9, [x22, #0x98]
    cbz x9, spin_loop
    b thread_passed

chain_test
    CREATE_EVENT #0
    str x1, [x22, #0x80]
    CREATE_THREAD wait_worker, #1
    mov x24, x1
    CREATE_THREAD delayed_signaler, #0
    mov x25, x1
    JOIN x24
    JOIN x25
    ldr x9, [x22, #0x80]
    CLOSE x9
    b passed
delayed_signaler
    ldr x22, =WIT_USER_DATA
    SLEEP_TICKS 2
    ldr x9, [x22, #0x80]
    SET_EVENT x9
    b thread_passed

budget_test
    CREATE_EVENT #0
    WAIT_EVENT x1, #-1
    b failed

rights_test
    ldr x9, [x20, #8]
    WAIT_EVENT x9, #0
    EXPECT WIT_STATUS_WRONG_TYPE
    ldr x0, [x20, #WIT_TEST_RO_OFFSET]
    SYSCALL WIT_CALL_EVENT_SET
    EXPECT WIT_STATUS_DENIED
    ldr x0, [x20, #WIT_TEST_RO_OFFSET]
    SYSCALL WIT_CALL_EVENT_RESET
    EXPECT WIT_STATUS_DENIED
    ldr x9, [x20, #WIT_TEST_RO_OFFSET]
    WAIT_EVENT x9, #0
    EXPECT WIT_STATUS_TIMED_OUT
    ldr x9, [x20, #WIT_TEST_SELF_OFFSET]
    WAIT_EVENT x9, #0
    EXPECT WIT_STATUS_DENIED
    ldr x0, [x20, #WIT_TEST_FOREIGN_OFFSET]
    SYSCALL WIT_CALL_EVENT_SET
    EXPECT WIT_STATUS_BAD_HANDLE
    ldr x9, [x20, #WIT_TEST_FOREIGN_OFFSET]
    WAIT_EVENT x9, #0
    EXPECT WIT_STATUS_BAD_HANDLE
    ldr x9, [x20, #WIT_TEST_RO_OFFSET]
    CLOSE x9
    ldr x9, [x20, #WIT_TEST_RO_OFFSET]
    WAIT_EVENT x9, #0
    EXPECT WIT_STATUS_BAD_HANDLE
    b passed

; THREAD_CREATE with a request on the stack: x0 entry, x1 argument, x2 flags -> x0 status, x1 handle.
thread_create
    sub sp, sp, #48
    mov w9, #WIT_THREAD_CREATE_VERSION
    str w9, [sp]
    mov w9, #48
    str w9, [sp, #4]
    str x0, [sp, #8]
    str x1, [sp, #16]
    str xzr, [sp, #24]
    str xzr, [sp, #32]
    str w2, [sp, #40]
    str wzr, [sp, #44]
    mov x0, sp
    mov x1, #48
    mov x2, #0
    SYSCALL WIT_CALL_THREAD_CREATE
    add sp, sp, #48
    ret

; Wait for the thread, read its exit code and close the handle: x0 handle -> x0 status, x1 exit code.
thread_join
    sub sp, sp, #144
    str x0, [sp, #32]
    add x9, sp, #32
    mov w10, #WIT_WAIT_OBJECTS_VERSION
    str w10, [sp]
    mov w10, #32
    str w10, [sp, #4]
    str x9, [sp, #8]
    mov w10, #1
    str w10, [sp, #16]
    str wzr, [sp, #20]
    mov x10, #-1
    str x10, [sp, #24]
    mov x0, sp
    mov x1, #32
    mov x2, #0
    SYSCALL WIT_CALL_OBJECT_WAIT
    cbnz x0, thread_join_done
    mov w10, #WIT_THREAD_INFO_VERSION
    str w10, [sp, #40]
    mov w10, #WIT_THREAD_INFO_SIZE
    str w10, [sp, #44]
    ldr x0, [sp, #32]
    add x1, sp, #40
    mov x2, #WIT_THREAD_INFO_SIZE
    SYSCALL WIT_CALL_THREAD_QUERY
    cbnz x0, thread_join_done
    ldr x0, [sp, #32]
    SYSCALL WIT_CALL_HANDLE_CLOSE
    ldr x1, [sp, #40 + 72] ; ExitCode
thread_join_done
    add sp, sp, #144
    ret

; OBJECT_WAIT on one handle: x0 handle, x1 deadline; x0 status, x1 winner. The request and its handle array live
; on the caller's own stack, so concurrent waiters never share them. Clobbers x2, x8 and x11 to x13.
wait_object
    sub sp, sp, #48
    str x0, [sp, #40]
    add x11, sp, #40
    mov w13, #WIT_WAIT_OBJECTS_VERSION
    str w13, [sp]
    mov w13, #32
    str w13, [sp, #4]
    str x11, [sp, #8]
    mov w13, #1
    str w13, [sp, #16]
    str wzr, [sp, #20]
    str x1, [sp, #24]
    mov x0, sp
    mov x1, #32
    mov x2, #0
    SYSCALL WIT_CALL_OBJECT_WAIT
    add sp, sp, #48
    ret

; Absolute monotonic deadline x0 ticks of 10 ms from now, into x9. Clobbers x0 to x2, x8 and x10 to x12.
deadline_ticks
    mov x12, x30
    mov x11, x0
    CLOCK_FREQUENCY
    mov x10, #100
    udiv x9, x1, x10
    mul x9, x9, x11
    CLOCK_READ
    add x9, x9, x1
    mov x30, x12
    ret

failed
    mov x0, #241
    b exit_process
passed
    mov x0, #WIT_TEST_EXIT_CODE
exit_process
    SYSCALL WIT_CALL_PROCESS_EXIT
    DCD 0x00000000 ; UDF #0
thread_passed
    mov x0, #WIT_TEST_EXIT_CODE
    SYSCALL WIT_CALL_THREAD_EXIT
    DCD 0x00000000 ; UDF #0
    LTORG
    ENDP
    END
