#include "user_abi_a64.h"
; ARM64 thread form fixture, the port of tests/User.X64/threads2.asm (RFC 0011 v3 section 7.3, plan step K5.2a). The
; transport is SVC #0 as before. THREAD_SET_TLS is UNSUPPORTED here: TPIDR_EL0 is the thread's own register, written
; at EL0 and preserved by the kernel across its returns, and the version 2 request's TLS base lands in TPIDRRO_EL0.
; A stack of the fixture's own is reserved and committed; a worker starts on it with the version 2 request, checks
; its argument, stack pointer and TLS bases, writes TPIDR_EL0 and sees it preserved across a yield, and exits naming
; the reservation; the creator joins it and finds the reservation released. Broken requests are refused whole. The
; data page: the request at 16, the wait request at 128, the thread info at 256, the reservation base at 3072, the
; status of a failed check at 1304 and the number of checks passed at 1312; the exit request at 3080, its word at 3112
; and the event handle at 3120 (S2.1).

    AREA |.text|, CODE, READONLY

    MACRO
    SYSCALL $number
    mov x8, #$number
    svc #0
    MEND

    MACRO
    EXPECT $status
    ldr x9, [x22, #1312]
    add x9, x9, #1
    str x9, [x22, #1312]
    cmp x0, #$status
    b.ne failed
    MEND

    ; THREAD_CREATE with the version 2 request: entry register, stack pointer register, TLS base register, version
    ; immediate, flags immediate -> x0, x1 handle.
    MACRO
    CREATE2 $entry, $stack, $tls, $version, $flags
    mov x4, $entry
    mov x5, $stack
    mov x6, $tls
    mov w7, #$version
    mov w9, #$flags
    str w9, [x22, #1320]
    bl thread_create2
    MEND

    EXPORT wit_user_start
wit_user_start PROC
    mov x20, x0
    ldr x22, =WIT_USER_DATA
    ldr w9, [x20]
    cmp w9, #WIT_ABI_VERSION
    b.ne failed
    ; THREAD_SET_TLS is not this ISA's: TPIDR_EL0 belongs to EL0.
    add x0, x22, #2048
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_THREAD_SET_TLS
    EXPECT WIT_STATUS_UNSUPPORTED
    ; A stack of our own: a reservation of 64 KiB, committed writable.
    mov x0, #65536
    mov x1, #4096
    mov x2, #0
    SYSCALL WIT_CALL_MEMORY_RESERVE
    EXPECT WIT_STATUS_OK
    mov x23, x1
    str x23, [x22, #3072]
    mov x0, x23
    mov x1, #65536
    mov x2, #(WIT_MEMORY_READ + WIT_MEMORY_WRITE)
    SYSCALL WIT_CALL_MEMORY_COMMIT
    EXPECT WIT_STATUS_OK
    ; Refusals: a stack pointer outside any reservation, a foreign version, a flag, a TLS base outside user space,
    ; a stack pointer off alignment.
    adr x10, worker
    mov x11, x22 ; the data page is a fixed mapping, not a reservation
    add x12, x22, #2048
    CREATE2 x10, x11, x12, 2, 0
    EXPECT WIT_STATUS_BAD_ADDRESS
    mov x11, #65536
    add x11, x23, x11
    CREATE2 x10, x11, x12, 3, 0
    EXPECT WIT_STATUS_UNSUPPORTED
    CREATE2 x10, x11, x12, 2, 2
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    ldr x13, =0xFFFF800000000000
    CREATE2 x10, x11, x13, 2, 0
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    sub x13, x11, #8
    CREATE2 x10, x13, x12, 2, 0
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    ; A version 1 thread (this one, on the kernel's stack) may not name a reservation at its exit.
    mov x0, #7
    mov x1, x22
    mov x2, #0
    SYSCALL WIT_CALL_THREAD_EXIT
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    ; An event and a word for the worker's exit request (S2.1): the kernel zeroes the word and sets the event once the
    ; worker no longer runs.
    mov x0, #0
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_EVENT_CREATE
    EXPECT WIT_STATUS_OK
    str x1, [x22, #3120]
    mov x9, #1
    str x9, [x22, #3112]
    ; The worker on our stack with our TLS base; its argument is 0x77.
    CREATE2 x10, x11, x12, 2, 0
    EXPECT WIT_STATUS_OK
    mov x24, x1
    mov x0, x24
    bl thread_join
    EXPECT WIT_STATUS_OK
    cmp x1, #42
    b.ne failed
    ; The exit request was served: the word is zero and the event is set.
    ldr w9, [x22, #3112]
    cbnz w9, failed
    ldr x0, [x22, #3120]
    bl object_wait
    EXPECT WIT_STATUS_OK
    ldr x0, [x22, #3120]
    SYSCALL WIT_CALL_HANDLE_CLOSE
    EXPECT WIT_STATUS_OK
    ; The worker's exit released the reservation.
    mov x0, x23
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_MEMORY_RELEASE
    EXPECT WIT_STATUS_NOT_RESERVED
    mov x0, #WIT_TEST_EXIT_CODE
    b exit_process

; The worker: argument in x0, the stack from the reservation, TPIDRRO_EL0 at the data page + 2048.
worker
    ldr x22, =WIT_USER_DATA
    cmp x0, #0x77
    b.ne worker_failed
    ldr x9, [x22, #3072]
    mov x10, #65536
    add x9, x9, x10
    mov x10, sp
    cmp x10, x9 ; the stack pointer is exactly the one requested
    b.ne worker_failed
    str x9, [sp, #-16]! ; the stack takes writes
    ldr x9, [sp], #16
    mrs x9, tpidrro_el0
    add x10, x22, #2048
    cmp x9, x10
    b.ne worker_failed
    ; TPIDR_EL0 is ours: written here, preserved by the kernel across a yield.
    ldr x9, =0x5678
    msr tpidr_el0, x9
    mov x0, #0
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_THREAD_YIELD
    cbnz x0, worker_continue ; another thread ran, or not; either way our register comes back
worker_continue
    mrs x9, tpidr_el0
    ldr x10, =0x5678
    cmp x9, x10
    b.ne worker_failed
    ; THREAD_EXIT naming something that is not a reservation returns instead of exiting.
    mov x0, #7
    mov x1, x22
    mov x2, #0
    SYSCALL WIT_CALL_THREAD_EXIT
    cmp x0, #WIT_STATUS_NOT_RESERVED
    b.ne worker_failed
    ; Exit requests refused whole (S2.1): a foreign version, a word outside user space, an unknown event.
    mov w9, #2
    str w9, [x22, #3080]
    mov w9, #24
    str w9, [x22, #3084]
    add x9, x22, #3112
    str x9, [x22, #3088]
    ldr x9, [x22, #3120]
    str x9, [x22, #3096]
    mov x0, #42
    ldr x1, [x22, #3072]
    add x2, x22, #3080
    SYSCALL WIT_CALL_THREAD_EXIT
    cmp x0, #WIT_STATUS_UNSUPPORTED
    b.ne worker_failed
    mov w9, #WIT_THREAD_EXIT_VERSION
    str w9, [x22, #3080]
    ldr x9, =0xFFFF800000000000
    str x9, [x22, #3088]
    mov x0, #42
    ldr x1, [x22, #3072]
    add x2, x22, #3080
    SYSCALL WIT_CALL_THREAD_EXIT
    cmp x0, #WIT_STATUS_BAD_ADDRESS
    b.ne worker_failed
    add x9, x22, #3112
    str x9, [x22, #3088]
    ldr x9, =0x12345
    str x9, [x22, #3096]
    mov x0, #42
    ldr x1, [x22, #3072]
    add x2, x22, #3080
    SYSCALL WIT_CALL_THREAD_EXIT
    cmp x0, #WIT_STATUS_BAD_HANDLE
    b.ne worker_failed
    ldr x9, [x22, #3120]
    str x9, [x22, #3096]
    ldr x9, [x22, #3112]
    cmp x9, #1 ; nothing of a refused request was served
    b.ne worker_failed
    mov x0, #42
    ldr x1, [x22, #3072] ; the kernel releases the stack's reservation once this thread no longer runs on it
    add x2, x22, #3080 ; and zeroes the word and sets the event
    SYSCALL WIT_CALL_THREAD_EXIT
    DCD 0x00000000
worker_failed
    mov x0, #9
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_THREAD_EXIT
    DCD 0x00000000

; THREAD_CREATE with the version 2 request at data page + 16: x4 entry, x5 stack pointer, x6 TLS base, w7 version;
; the flags come from 1320. Returns x0, x1 the handle.
thread_create2
    add x9, x22, #16 ; x10 to x13 belong to the caller's arguments; x14 is the scratch here
    str w7, [x9]
    mov w14, #48
    str w14, [x9, #4]
    str x4, [x9, #8]
    mov x14, #0x77
    str x14, [x9, #16]
    str x5, [x9, #24]
    str x6, [x9, #32]
    ldr w14, [x22, #1320]
    str w14, [x9, #40]
    str wzr, [x9, #44]
    mov x0, x9
    mov x1, #48
    mov x2, #0
    SYSCALL WIT_CALL_THREAD_CREATE
    ret

; Wait for one object with no deadline: x0 handle -> x0 status.
object_wait
    str x0, [x22, #(128 + 32)]
    add x9, x22, #128
    mov w10, #WIT_WAIT_OBJECTS_VERSION
    str w10, [x9]
    mov w10, #32
    str w10, [x9, #4]
    add x10, x9, #32
    str x10, [x9, #8]
    mov w10, #1
    str w10, [x9, #16]
    str wzr, [x9, #20]
    mov x10, #-1
    str x10, [x9, #24]
    mov x0, x9
    mov x1, #32
    mov x2, #0
    SYSCALL WIT_CALL_OBJECT_WAIT
    ret

; Wait for the thread, read its exit code and close the handle: x0 handle -> x0 status, x1 exit code.
thread_join
    mov x19, x30
    str x0, [x22, #(128 + 32)]
    add x9, x22, #128
    mov w10, #WIT_WAIT_OBJECTS_VERSION
    str w10, [x9]
    mov w10, #32
    str w10, [x9, #4]
    add x10, x9, #32
    str x10, [x9, #8]
    mov w10, #1
    str w10, [x9, #16]
    str wzr, [x9, #20]
    mov x10, #-1
    str x10, [x9, #24]
    mov x0, x9
    mov x1, #32
    mov x2, #0
    SYSCALL WIT_CALL_OBJECT_WAIT
    cbnz x0, thread_join_done
    mov w10, #WIT_THREAD_INFO_VERSION
    str w10, [x22, #256]
    mov w10, #WIT_THREAD_INFO_SIZE
    str w10, [x22, #260]
    ldr x0, [x22, #(128 + 32)]
    add x1, x22, #256
    mov x2, #WIT_THREAD_INFO_SIZE
    SYSCALL WIT_CALL_THREAD_QUERY
    cbnz x0, thread_join_done
    ldr x0, [x22, #(128 + 32)]
    SYSCALL WIT_CALL_HANDLE_CLOSE
    ldr x1, [x22, #(256 + 72)] ; ExitCode
thread_join_done
    mov x30, x19
    ret

failed
    str x0, [x22, #1304] ; the status the failed check saw
    mov x0, #241
exit_process
    SYSCALL WIT_CALL_PROCESS_EXIT
    DCD 0x00000000 ; UDF #0
    LTORG
    ENDP
    END
