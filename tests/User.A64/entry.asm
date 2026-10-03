#include "user_abi_a64.h"
; ARM64 user isolation fixture, the port of tests/User.X64/entry.asm. The C preprocessor supplies the ABI and
; protocol constants. System calls: number in x8, arguments in x0-x2, SVC #0, status in x0 and value in x1.
; Registers: x19 sentinel, x20 startup block, x21 test mode, x22 working address. The sentinel is also kept in
; both halves of v8 and in TPIDR_EL0, which every return to EL0 must preserve.

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

    EXPORT wit_user_start

wit_user_start PROC
    mov x20, x0
    ldr w9, [x20]
    cmp w9, #WIT_ABI_VERSION
    b.ne failed
    ldr w9, [x20, #4]
    cmp w9, #WIT_ABI_STARTUP_SIZE
    b.ne failed
    ; A new thread starts with zero SIMD state and thread register.
    fmov x9, d0
    cbnz x9, failed
    mov x9, v0.d[1]
    cbnz x9, failed
    mrs x9, tpidr_el0
    cbnz x9, failed
    ldr x19, =0x55AA001100220033
    fmov d8, x19
    mov v8.d[1], x19
    msr tpidr_el0, x19
    ldr x0, =WIT_USER_DATA
    bl expect_zero

    SYSCALL WIT_CALL_QUERY
    EXPECT WIT_STATUS_OK
    cmp x1, #WIT_ABI_VERSION
    b.ne failed

    ldr x21, [x20, #WIT_TEST_MODE_OFFSET]
    cmp x21, #WIT_TEST_KERNEL_READ
    b.eq kernel_read
    cmp x21, #WIT_TEST_KERNEL_WRITE
    b.eq kernel_write
    cmp x21, #WIT_TEST_PRIVILEGED_CLI
    b.eq privileged_mask
    cmp x21, #WIT_TEST_PRIVILEGED_PORT
    b.eq privileged_register
    cmp x21, #WIT_TEST_EXECUTE_DATA
    b.eq execute_data
    cmp x21, #WIT_TEST_GUARD_LOW
    b.eq guard_low
    cmp x21, #WIT_TEST_GUARD_HIGH
    b.eq guard_high
    cmp x21, #WIT_TEST_WRITE_CODE
    b.eq write_code
    cmp x21, #WIT_TEST_WRITE_INFO
    b.eq write_info
    cmp x21, #WIT_TEST_PEER_READ
    b.eq peer_read
    cmp x21, #WIT_TEST_SPIN
    b.eq spin_forever
    cmp x21, #WIT_TEST_NULL_READ
    b.eq null_read
    cmp x21, #WIT_TEST_INVALID_OPCODE
    b.eq invalid_opcode
    cmp x21, #WIT_TEST_BAD_RETURN
    b.eq bad_return
    cmp x21, #WIT_TEST_PREEMPTION_STATE
    b.eq preemption_state
    cmp x21, #WIT_TEST_MEMORY_LIFECYCLE
    b.hs memory_start
    cmp x21, #WIT_TEST_NORMAL
    b.ne failed

    SYSCALL 0xFFFF
    EXPECT WIT_STATUS_UNSUPPORTED
    ldr x0, [x20, #WIT_TEST_RO_OFFSET]
    bl try_write
    EXPECT WIT_STATUS_DENIED
    ldr x0, [x20, #WIT_TEST_SELF_OFFSET]
    bl try_write
    EXPECT WIT_STATUS_WRONG_TYPE
    ldr x0, [x20, #WIT_TEST_FOREIGN_OFFSET]
    bl try_write
    EXPECT WIT_STATUS_BAD_HANDLE
    mov x0, #0
    bl try_write
    EXPECT WIT_STATUS_BAD_HANDLE

    ldr x0, [x20, #8]
    adr x1, message
    mov x2, #WIT_ABI_MAX_WRITE + 1
    SYSCALL WIT_CALL_WRITE
    EXPECT WIT_STATUS_TOO_LARGE

    ldr x0, [x20, #8]
    mov x1, #-2
    mov x2, #8
    SYSCALL WIT_CALL_WRITE
    EXPECT WIT_STATUS_BAD_ADDRESS

    ldr x0, [x20, #8]
    ldr x1, [x20, #WIT_TEST_KERNEL_OFFSET]
    mov x2, #8
    SYSCALL WIT_CALL_WRITE
    EXPECT WIT_STATUS_BAD_ADDRESS

    ldr x0, [x20, #8]
    ldr x1, =WIT_USER_DATA_END - 2
    mov x2, #8
    SYSCALL WIT_CALL_WRITE
    EXPECT WIT_STATUS_BAD_ADDRESS

    ldr x0, [x20, #8]
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_WRITE
    EXPECT WIT_STATUS_OK
    cbnz x1, failed

    ; A valid readable code buffer and a valid buffer spanning two data pages.
    ldr x0, [x20, #8]
    bl try_write
    EXPECT WIT_STATUS_OK
    cmp x1, #message_end - message
    b.ne failed
    ldr x22, =WIT_USER_DATA + 4092
    ldr w9, =0x736F7263
    str w9, [x22]
    ldr w9, =0x0A677073
    str w9, [x22, #4]
    ldr x0, [x20, #8]
    mov x1, x22
    mov x2, #8
    SYSCALL WIT_CALL_WRITE
    EXPECT WIT_STATUS_OK
    cmp x1, #8
    b.ne failed

    ldr x0, [x20, #8]
    SYSCALL WIT_CALL_CLOSE
    EXPECT WIT_STATUS_OK
    ldr x0, [x20, #8]
    bl try_write
    EXPECT WIT_STATUS_BAD_HANDLE
    ldr x0, [x20, #8]
    SYSCALL WIT_CALL_CLOSE
    EXPECT WIT_STATUS_BAD_HANDLE

    bl expect_state
    ldr x22, =WIT_USER_DATA
    ldr x9, [x20, #WIT_TEST_INSTANCE_OFFSET]
    str x9, [x22]
    mov x0, #WIT_TEST_EXIT_CODE
    b exit_component

; Memory tests use the same SVC boundary as future runtime callers.
memory_start
    ldr x0, =0x800000000 ; 32 GiB reservation
    mov x1, #0x200000
    SYSCALL WIT_CALL_MEMORY_RESERVE
    EXPECT WIT_STATUS_OK
    mov x22, x1
    ldr x9, =WIT_USER_MEMORY_BASE
    cmp x22, x9
    b.ne failed
    cmp x21, #WIT_TEST_MEMORY_RESERVED
    b.eq memory_read_fault

    ; Force a partially completed commit to exhaust the frame quota of the component.
    ; The call must report failure, leave no accessible prefix and allow retry.
    mov x0, x22
    mov x1, #WIT_USER_PAGE_CAPACITY * 4096
    mov x2, #3
    SYSCALL WIT_CALL_MEMORY_COMMIT
    EXPECT WIT_STATUS_NO_MEMORY
    cbnz x1, failed
    bl memory_bad_buffer

    mov x0, x22
    mov x1, #8192
    mov x2, #WIT_MEMORY_READ + WIT_MEMORY_WRITE
    SYSCALL WIT_CALL_MEMORY_COMMIT
    EXPECT WIT_STATUS_OK
    mov x0, x22
    bl expect_zero
    ldr x9, =0x12345678
    str x9, [x22]
    ldr x9, =0x76543210
    str x9, [x22, #8184]
    cmp x21, #WIT_TEST_MEMORY_NX
    b.eq memory_execute_fault

    ; Warm writable translations before removing permissions.
    mov x0, x22
    mov x1, #8192
    mov x2, #WIT_MEMORY_READ
    SYSCALL WIT_CALL_MEMORY_PROTECT
    EXPECT WIT_STATUS_OK
    cmp x21, #WIT_TEST_MEMORY_READONLY
    b.eq memory_write_fault
    bl expect_marks

    mov x0, x22
    mov x1, #8192
    mov x2, #0
    SYSCALL WIT_CALL_MEMORY_PROTECT
    EXPECT WIT_STATUS_OK
    cmp x21, #WIT_TEST_MEMORY_NOACCESS
    b.eq memory_read_fault
    bl memory_bad_buffer
    ; Idempotent commit must preserve no access and the content.
    mov x0, x22
    mov x1, #8192
    mov x2, #3
    SYSCALL WIT_CALL_MEMORY_COMMIT
    EXPECT WIT_STATUS_OK
    bl memory_bad_buffer
    mov x0, x22
    mov x1, #8192
    mov x2, #3
    SYSCALL WIT_CALL_MEMORY_PROTECT
    EXPECT WIT_STATUS_OK
    bl expect_marks
    ; A range containing a hole must fail without changing its mapped prefix.
    mov x0, x22
    mov x1, #12288
    mov x2, #0
    SYSCALL WIT_CALL_MEMORY_PROTECT
    EXPECT WIT_STATUS_NOT_COMMITTED
    mov x9, #0x1111
    str x9, [x22]

    mov x0, x22
    mov x1, #8192
    SYSCALL WIT_CALL_MEMORY_DECOMMIT
    EXPECT WIT_STATUS_OK
    cmp x21, #WIT_TEST_MEMORY_DECOMMITTED
    b.eq memory_read_fault
    bl memory_bad_buffer
    ; Allocate physical memory with no access, then expose zero-filled contents.
    mov x0, x22
    mov x1, #8192
    mov x2, #0
    SYSCALL WIT_CALL_MEMORY_COMMIT
    EXPECT WIT_STATUS_OK
    bl memory_bad_buffer
    mov x0, x22
    mov x1, #8192
    mov x2, #3
    SYSCALL WIT_CALL_MEMORY_PROTECT
    EXPECT WIT_STATUS_OK
    mov x0, x22
    bl expect_zero
    mov x9, #0x2222
    str x9, [x22]
    mov x0, x22
    SYSCALL WIT_CALL_MEMORY_RELEASE
    EXPECT WIT_STATUS_OK
    cmp x21, #WIT_TEST_MEMORY_RELEASED
    b.eq memory_read_fault
    bl memory_bad_buffer
    mov x0, x22
    mov x1, #4096
    mov x2, #3
    SYSCALL WIT_CALL_MEMORY_COMMIT
    EXPECT WIT_STATUS_NOT_RESERVED
    mov x0, x22
    SYSCALL WIT_CALL_MEMORY_RELEASE
    EXPECT WIT_STATUS_NOT_RESERVED

    mov x0, #8192
    mov x1, #4096
    SYSCALL WIT_CALL_MEMORY_RESERVE
    EXPECT WIT_STATUS_OK
    cmp x1, x22
    b.ne failed
    mov x0, x22
    mov x1, #8192
    mov x2, #3
    SYSCALL WIT_CALL_MEMORY_COMMIT
    EXPECT WIT_STATUS_OK
    mov x0, x22
    bl expect_zero
    ; Dynamic user buffers must work, including a page boundary.
    ldr w9, =0x746D656D
    str w9, [x22, #4092]
    ldr w9, =0x0A747365
    str w9, [x22, #4096]
    ldr x0, [x20, #8]
    add x1, x22, #4092
    mov x2, #8
    SYSCALL WIT_CALL_WRITE
    EXPECT WIT_STATUS_OK
    cmp x1, #8
    b.ne failed

    ldr x0, =WIT_USER_CODE
    mov x1, #4096
    mov x2, #3
    SYSCALL WIT_CALL_MEMORY_PROTECT
    EXPECT WIT_STATUS_BAD_ADDRESS
    add x0, x22, #1
    mov x1, #4096
    SYSCALL WIT_CALL_MEMORY_DECOMMIT
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    mov x0, x22
    mov x1, #4096
    mov x2, #5 ; executable dynamic memory is not in this ABI
    SYSCALL WIT_CALL_MEMORY_PROTECT
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    mov x0, x22
    mov x1, #-4096
    mov x2, #3
    SYSCALL WIT_CALL_MEMORY_COMMIT
    EXPECT WIT_STATUS_BAD_ADDRESS
    mov x0, #4096
    mov x1, #12288
    SYSCALL WIT_CALL_MEMORY_RESERVE
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    cbnz x1, failed
    mov x0, x22
    SYSCALL WIT_CALL_MEMORY_RELEASE
    EXPECT WIT_STATUS_OK
    bl expect_state
    mov x0, #WIT_TEST_EXIT_CODE
    b exit_component

memory_bad_buffer
    ldr x0, [x20, #8]
    mov x1, x22
    mov x2, #8
    SYSCALL WIT_CALL_WRITE
    EXPECT WIT_STATUS_BAD_ADDRESS
    cbnz x1, failed
    ret
memory_read_fault
    ldr x9, [x22]
    b failed
memory_write_fault
    str xzr, [x22]
    b failed
memory_execute_fault
    ldr w9, =0xD65F03C0 ; RET
    str w9, [x22]
    blr x22
    b failed

; The marks written to the first and last word of the two committed pages.
expect_marks
    ldr x9, [x22]
    ldr x10, =0x12345678
    cmp x9, x10
    b.ne failed
    ldr x9, [x22, #8184]
    ldr x10, =0x76543210
    cmp x9, x10
    b.ne failed
    ret

; x0 = first of two pages that must read as zero.
expect_zero
    mov x10, #1024
zero_loop
    ldr x9, [x0], #8
    cbnz x9, failed
    subs x10, x10, #1
    b.ne zero_loop
    ret

; The sentinel survived in x19, both halves of v8 and TPIDR_EL0.
expect_state
    ldr x9, =0x55AA001100220033
    cmp x19, x9
    b.ne failed
    fmov x9, d8
    cmp x9, x19
    b.ne failed
    mov x9, v8.d[1]
    cmp x9, x19
    b.ne failed
    mrs x9, tpidr_el0
    cmp x9, x19
    b.ne failed
    ret

try_write
    adr x1, message
    mov x2, #message_end - message
    SYSCALL WIT_CALL_WRITE
    ret

kernel_read
    ldr x9, [x20, #WIT_TEST_KERNEL_OFFSET]
    ldr x9, [x9]
    b failed
kernel_write
    ldr x9, [x20, #WIT_TEST_KERNEL_OFFSET]
    str xzr, [x9]
    b failed
privileged_mask
    msr daifset, #2 ; EL0 may not mask interrupts (SCTLR_EL1.UMA is clear)
    b failed
privileged_register
    mrs x9, ttbr1_el1 ; EL1 system registers are undefined at EL0
    b failed
execute_data
    ldr x9, =WIT_USER_DATA
    ldr w10, =0xD65F03C0 ; RET
    str w10, [x9]
    blr x9
    b failed
guard_low
    ldr x9, =WIT_USER_STACK_BOTTOM - 1
    mov w10, #1
    strb w10, [x9]
    b failed
guard_high
    ldr x9, =WIT_USER_STACK_TOP
    mov w10, #1
    strb w10, [x9]
    b failed
write_code
    ldr x9, =WIT_USER_CODE
    strb wzr, [x9]
    b failed
write_info
    strb wzr, [x20]
    b failed
peer_read
    ldr x9, =WIT_USER_PEER_PAGE
    ldr x9, [x9]
    b failed
null_read
    mov x9, #0
    ldr x9, [x9]
    b failed
invalid_opcode
    DCD 0x00000000 ; UDF #0
    b failed
bad_return
    ldr x9, =0x0000800000000000 ; outside the owning user stack
    mov sp, x9
    SYSCALL WIT_CALL_QUERY
    b spin_forever
preemption_state
    cmp x19, x19
state_loop
    ; Equal comparisons keep Z and C set; a lost condition flag, GPR, SIMD or thread register fails.
    b.ne failed
    b.lo failed
    fmov x9, d8
    cmp x9, x19
    b.ne failed
    mov x9, v8.d[1]
    cmp x9, x19
    b.ne failed
    mrs x9, tpidr_el0
    cmp x9, x19
    yield
    b state_loop
spin_forever
    yield
    b spin_forever
failed
    mov x0, #241
exit_component
    SYSCALL WIT_CALL_EXIT
    DCD 0x00000000 ; UDF #0
    LTORG
message
    DCB "Hello from EL0.", 10
message_end
    ENDP
    END
