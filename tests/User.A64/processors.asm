#include "user_abi_a64.h"
; ARM64 processor fixture, the port of tests/User.X64/processors.asm (RFC 0011 v3 section 7.9, plan step K7.1):
; PROCESSOR_QUERY in the 4-byte form of the frozen line and in the record form, the refusals of a wrong size and a
; foreign version; THREAD_AFFINITY of the current thread: the default mask, a set within the table, an empty mask and
; a mask beyond the table refused, and a duplicate without the right refused the set. The data page: the 4-byte form
; at 16, the record form at 64 (280 bytes), the mask at 400, a duplicate's output at 408, the status of a failed check
; at 1304 and the number of checks passed at 1312. Registers: x20 startup block, x22 the data page.

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

    ; THREAD_AFFINITY: handle register, flags (the mask at data page + 400) -> x0.
    MACRO
    AFFINITY $handle, $flags
    mov x0, $handle
    add x1, x22, #400
    mov x2, #$flags
    SYSCALL WIT_CALL_THREAD_AFFINITY
    MEND

    EXPORT wit_user_start
wit_user_start PROC
    mov x20, x0
    ldr x22, =WIT_USER_DATA
    ldr w9, [x20]
    cmp w9, #WIT_ABI_VERSION
    b.ne failed
    ; The 4-byte form: the current processor is group 0, number 0.
    mov w9, #-1
    str w9, [x22, #16]
    add x0, x22, #16
    mov x1, #4
    mov x2, #0
    SYSCALL WIT_CALL_PROCESSOR_QUERY
    EXPECT WIT_STATUS_OK
    cmp x1, #4
    b.ne failed
    ldr w9, [x22, #16]
    cbnz w9, failed
    ; The record form: version 1, 280 bytes; the boot processor first, online, the one online.
    mov w9, #WIT_PROCESSOR_INFO_VERSION
    str w9, [x22, #64]
    mov w9, #WIT_PROCESSOR_INFO_SIZE
    str w9, [x22, #68]
    add x0, x22, #64
    mov x1, #WIT_PROCESSOR_INFO_SIZE
    mov x2, #0
    SYSCALL WIT_CALL_PROCESSOR_QUERY
    EXPECT WIT_STATUS_OK
    cmp x1, #WIT_PROCESSOR_INFO_SIZE
    b.ne failed
    ldr w9, [x22, #64]
    cmp w9, #WIT_PROCESSOR_INFO_VERSION
    b.ne failed
    ldr w9, [x22, #72] ; Current
    cbnz w9, failed
    ldr w9, [x22, #76] ; Count
    cmp w9, #1
    b.lo failed
    ldr w9, [x22, #80] ; Online: at least the boot processor, at most the count
    cmp w9, #1
    b.lo failed
    ldr w10, [x22, #76]
    cmp w9, w10
    b.hi failed
    ldr w9, [x22, #(88 + 8)] ; the first record's flags
    cmp w9, #(WIT_PROCESSOR_ONLINE + WIT_PROCESSOR_BOOT)
    b.ne failed
    ldrh w9, [x22, #(88 + 14)] ; its number
    cbnz w9, failed
    ; A wrong size and a foreign version are refused.
    add x0, x22, #64
    mov x1, #100
    mov x2, #0
    SYSCALL WIT_CALL_PROCESSOR_QUERY
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    mov w9, #2
    str w9, [x22, #64]
    add x0, x22, #64
    mov x1, #WIT_PROCESSOR_INFO_SIZE
    mov x2, #0
    SYSCALL WIT_CALL_PROCESSOR_QUERY
    EXPECT WIT_STATUS_UNSUPPORTED
    ; Affinity: the default is the boot processor; a set of the same is accepted; an empty mask and a mask beyond
    ; the table are refused; a foreign flag is refused.
    mov x10, #-2 ; WIT_THREAD_SELF
    AFFINITY x10, WIT_THREAD_AFFINITY_GET
    EXPECT WIT_STATUS_OK
    ldr x9, [x22, #400]
    cmp x9, #1
    b.ne failed
    mov x10, #-2
    AFFINITY x10, WIT_THREAD_AFFINITY_SET
    EXPECT WIT_STATUS_OK
    str xzr, [x22, #400]
    mov x10, #-2
    AFFINITY x10, WIT_THREAD_AFFINITY_SET
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    mov x9, #0x8000000000000000
    str x9, [x22, #400]
    mov x10, #-2
    AFFINITY x10, WIT_THREAD_AFFINITY_SET
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    mov x9, #1
    str x9, [x22, #400]
    mov x10, #-2
    AFFINITY x10, 2
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    ; A thread handle without the AFFINITY right reads the mask but may not set it.
    mov x0, #-2
    add x1, x22, #408
    mov x2, #WIT_RIGHT_QUERY
    SYSCALL WIT_CALL_HANDLE_DUPLICATE
    EXPECT WIT_STATUS_OK
    str xzr, [x22, #400]
    ldr x10, [x22, #408]
    AFFINITY x10, WIT_THREAD_AFFINITY_GET
    EXPECT WIT_STATUS_OK
    ldr x9, [x22, #400]
    cmp x9, #1
    b.ne failed
    ldr x10, [x22, #408]
    AFFINITY x10, WIT_THREAD_AFFINITY_SET
    EXPECT WIT_STATUS_DENIED
    ldr x0, [x22, #408]
    SYSCALL WIT_CALL_HANDLE_CLOSE
    EXPECT WIT_STATUS_OK
    mov x0, #WIT_TEST_EXIT_CODE
    b exit_process

failed
    str x0, [x22, #1304] ; the status the failed check saw
    mov x0, #241
exit_process
    SYSCALL WIT_CALL_PROCESS_EXIT
    DCD 0x00000000 ; UDF #0
    LTORG
    ENDP
    END
