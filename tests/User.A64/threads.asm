#include "user_abi_a64.h"
; ARM64 thread fixture, the port of tests/User.X64/threads.asm over the ABI-1 thread family of RFC 0011 v3 (plan step
; K1.2): THREAD_CREATE is the one form, a join is OBJECT_WAIT on the thread handle followed by THREAD_QUERY for the
; exit code and HANDLE_CLOSE, and closing the handle of a live thread detaches it. The raw TLS block of the running
; thread is at TPIDRRO_EL0, which EL0 reads but cannot change; its first word points to itself. Per-thread rounding
; modes in FPCR stand in for MXCSR. Registers: x20 startup block, x21 test mode, x22 TLS block, x23 and x24 handles or
; worker state, x25 the shared data page.

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

    ; Create a thread: entry, argument -> x0 status, x1 handle.
    MACRO
    CREATE $target, $argument
    adr x0, $target
    mov x1, $argument
    mov x2, #0
    bl thread_create
    MEND

    ; Join: handle -> x0 status; x1 exit code and the handle closed on success.
    MACRO
    JOIN $handle
    mov x0, $handle
    bl thread_join
    MEND

    EXPORT wit_user_start

wit_user_start PROC
    mov x20, x0
    ldr w9, [x20]
    cmp w9, #WIT_ABI_VERSION
    b.ne failed
    mrs x22, tpidrro_el0
    ldr x9, [x22, #WIT_TLS_SELF_OFFSET]
    cmp x9, x22
    b.ne failed
    ldr x10, =WIT_USER_TLS
    cmp x22, x10
    b.ne failed
    ldr x9, [x22, #WIT_TLS_ARGUMENT_OFFSET]
    cmp x9, x20
    b.ne failed
    ldr x21, [x20, #WIT_TEST_MODE_OFFSET]
    cmp x21, #WIT_THREAD_TEST_CAPACITY
    b.eq capacity_main
    cmp x21, #WIT_THREAD_TEST_FAULT
    b.hs fault_main

    ; Wrong type, foreign handles, the private identity and closing it are errors.
    ldr x9, [x20, #8]
    JOIN x9
    EXPECT WIT_STATUS_WRONG_TYPE
    ldr x9, [x20, #WIT_TEST_FOREIGN_OFFSET]
    JOIN x9
    EXPECT WIT_STATUS_BAD_HANDLE
    ldr x9, [x22, #WIT_TLS_HANDLE_OFFSET]
    JOIN x9
    EXPECT WIT_STATUS_WRONG_TYPE
    ldr x0, [x22, #WIT_TLS_HANDLE_OFFSET]
    SYSCALL WIT_CALL_HANDLE_CLOSE
    EXPECT WIT_STATUS_BUSY
    ldr x0, =WIT_USER_DATA
    mov x1, #0
    mov x2, #0
    bl thread_create
    EXPECT WIT_STATUS_BAD_ADDRESS
    adr x0, worker
    mov x1, #0
    mov x2, #4 ; bit 2 is reserved; bits 0 and 1 are START_SUSPENDED and LIBRARY_NOTIFICATIONS
    bl thread_create
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    cbnz x1, failed
    ; A component cannot remove the TLS of an active thread through memory calls.
    mov x0, x22
    mov x1, #4096
    SYSCALL WIT_CALL_MEMORY_DECOMMIT
    EXPECT WIT_STATUS_BAD_ADDRESS
    mov x9, #0x9876
    str x9, [x22, #WIT_TLS_DATA_OFFSET]

    CREATE worker, #1
    EXPECT WIT_STATUS_OK
    mov x23, x1
    CREATE worker, #2
    EXPECT WIT_STATUS_OK
    mov x24, x1
    JOIN x23
    EXPECT WIT_STATUS_OK
    cmp x1, #101
    b.ne failed
    JOIN x24
    EXPECT WIT_STATUS_OK
    cmp x1, #102
    b.ne failed
    ldr x9, [x22, #WIT_TLS_DATA_OFFSET]
    mov x10, #0x9876
    cmp x9, x10
    b.ne failed
    ; Reuse a reaped slot: fresh TLS and stack and a different handle generation.
    CREATE worker, #3
    EXPECT WIT_STATUS_OK
    mov x24, x1
    cmp x24, x23
    b.eq failed
    JOIN x23
    EXPECT WIT_STATUS_BAD_HANDLE
    SYSCALL WIT_CALL_THREAD_YIELD
    EXPECT WIT_STATUS_OK
    JOIN x24
    EXPECT WIT_STATUS_OK
    cmp x1, #103
    b.ne failed
    JOIN x24
    EXPECT WIT_STATUS_BAD_HANDLE
    ; Closing the handle of a live thread detaches it; the thread runs to its exit and is reaped there.
    CREATE worker, #4
    EXPECT WIT_STATUS_OK
    mov x24, x1
    mov x0, x24
    SYSCALL WIT_CALL_HANDLE_CLOSE
    EXPECT WIT_STATUS_OK
    JOIN x24
    EXPECT WIT_STATUS_BAD_HANDLE
    SYSCALL WIT_CALL_THREAD_YIELD
    EXPECT WIT_STATUS_OK
    mov x0, #WIT_TEST_EXIT_CODE
    b process_exit

worker
    ; No call or prologue has touched the new stack. Check the whole configured stack and the TLS payload.
    mov x23, x0
    mrs x22, tpidrro_el0
    ldr x9, [x22, #WIT_TLS_SELF_OFFSET]
    cmp x9, x22
    b.ne failed
    ldr x9, [x22, #WIT_TLS_ARGUMENT_OFFSET]
    cmp x9, x23
    b.ne failed
    ldr x9, [x22, #WIT_TLS_DATA_OFFSET]
    cbnz x9, failed
    fmov x9, d0
    cbnz x9, failed
    fmov x9, d8
    cbnz x9, failed
    mrs x9, fpcr
    cbnz x9, failed
    ldr x10, =WIT_USER_TLS - WIT_USER_STACK_BOTTOM
    sub x25, x22, x10
    ldr x10, =(WIT_USER_STACK_TOP - WIT_USER_STACK_BOTTOM) / 8
worker_zero
    ldr x9, [x25], #8
    cbnz x9, failed
    subs x10, x10, #1
    b.ne worker_zero
    str x23, [x22, #WIT_TLS_DATA_OFFSET]
    fmov d8, x23
    ldr x24, =0x123456789ABCDEF0
    add x24, x24, x23
    fmov d0, x24
    stur x24, [sp, #-16] ; below SP: no exception entry may touch the EL0 stack
    mov x26, #0
    cmp x23, #1
    b.ne worker_rounding
    mov x26, #0x00C00000 ; round toward zero in the first worker only
worker_rounding
    msr fpcr, x26
    ldr x25, =WIT_USER_DATA
    cmp x23, #3
    b.hs worker_exit
    mov x9, #1
    str x9, [x25, x23, lsl #3]
worker_loop
    mrs x9, tpidrro_el0
    cmp x9, x22
    b.ne failed
    ldr x9, [x22, #WIT_TLS_DATA_OFFSET]
    cmp x9, x23
    b.ne failed
    fmov x9, d8
    cmp x9, x23
    b.ne failed
    fmov x9, d0
    cmp x9, x24
    b.ne failed
    ldur x9, [sp, #-16]
    cmp x9, x24
    b.ne failed
    mrs x9, fpcr
    cmp x9, x26
    b.ne failed
    ldr x9, [x22, #WIT_TLS_DATA_OFFSET + 8]
    add x9, x9, #1
    str x9, [x22, #WIT_TLS_DATA_OFFSET + 8]
    cmp x23, #1
    b.ne worker_b
    ldr x9, [x25, #16]
    cbz x9, worker_loop
    mov x9, #1
    str x9, [x25, #24]
    b worker_exit
worker_b
    ldr x9, [x25, #24]
    cbz x9, worker_loop
worker_exit
    add x0, x23, #100
    b thread_exit

capacity_main
    CREATE capacity_worker, #1
    EXPECT WIT_STATUS_OK
    mov x23, x1
    CREATE capacity_worker, #2
    EXPECT WIT_STATUS_OK
    mov x24, x1
    CREATE capacity_worker, #3
    EXPECT WIT_STATUS_OK
    mov x21, x1
    CREATE capacity_worker, #4
    EXPECT WIT_STATUS_NO_MEMORY
    cbnz x1, failed
    ldr x25, =WIT_USER_DATA
    mov x9, #1
    str x9, [x25, #0x200]
    JOIN x23
    EXPECT WIT_STATUS_OK
    cmp x1, #1
    b.ne failed
    JOIN x24
    EXPECT WIT_STATUS_OK
    cmp x1, #2
    b.ne failed
    JOIN x21
    EXPECT WIT_STATUS_OK
    cmp x1, #3
    b.ne failed
    mov x0, #WIT_TEST_EXIT_CODE
    b process_exit
capacity_worker
    mov x23, x0
    ldr x25, =WIT_USER_DATA
capacity_loop
    ldr x9, [x25, #0x200]
    cbnz x9, capacity_exit
    SYSCALL WIT_CALL_THREAD_YIELD
    EXPECT WIT_STATUS_OK
    b capacity_loop
capacity_exit
    mov x0, x23
    b thread_exit

fault_main
    CREATE fault_worker, x21
    EXPECT WIT_STATUS_OK
    JOIN x1
    b failed
fault_worker
    mrs x22, tpidrro_el0
    cmp x0, #WIT_THREAD_TEST_GUARD_LOW
    b.eq thread_guard_low
    cmp x0, #WIT_THREAD_TEST_GUARD_HIGH
    b.eq thread_guard_high
    cmp x0, #WIT_THREAD_TEST_BAD_RETURN
    b.eq thread_bad_return
    cmp x0, #WIT_THREAD_TEST_PROCESS_EXIT
    b.eq thread_process_exit
    DCD 0x00000000 ; UDF #0
thread_guard_low
    ldr x9, =WIT_USER_TLS - WIT_USER_STACK_BOTTOM + 1
    sub x9, x22, x9
    mov w10, #1
    strb w10, [x9]
    b failed
thread_guard_high
    ldr x9, =WIT_USER_TLS - WIT_USER_STACK_TOP
    sub x9, x22, x9
    mov w10, #1
    strb w10, [x9]
    b failed
thread_bad_return
    ldr x9, =WIT_USER_STACK_TOP - 16 ; mapped, but owned by another thread
    mov sp, x9
    SYSCALL WIT_CALL_QUERY
    b failed
thread_process_exit
    mov x0, #WIT_TEST_EXIT_CODE
    b process_exit

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

; Wait for the thread, read its exit code and close the handle: x0 handle -> x0 status, x1 exit code. A failed wait
; returns its status and leaves the handle.
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

failed
    mov x0, #241
process_exit
    SYSCALL WIT_CALL_PROCESS_EXIT
    DCD 0x00000000 ; UDF #0
thread_exit
    SYSCALL WIT_CALL_THREAD_EXIT
    DCD 0x00000000 ; UDF #0
    LTORG
    ENDP
    END
