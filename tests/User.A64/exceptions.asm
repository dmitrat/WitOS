#include "user_abi_a64.h"
; ARM64 exception fixture, the port of tests/User.X64/exceptions.asm over the fault callback and the thread contexts
; of ABI-1 (RFC 0011 v3 sections 7.3 and 7.5, plan step K1.4): EXCEPTION_REGISTER, a read fault at address zero
; delivered to the callback with the interrupted AArch64 context, EXCEPTION_QUERY and EXCEPTION_CONTINUE with a
; changed context, THREAD_ACTIVATE of the own thread through the same callback, CONTEXT_PROFILE and
; THREAD_CONTEXT_GET of the own thread. The record lives in the data page, the transfer 1024 bytes above it, the
; activation counter at 2048 and the profile at 2112. Registers: x20 startup block, x21 test mode, x22 the marked
; register, x23 token, x24 vector, x25 address, x26 the record.

#define CONTEXT_SIZE WIT_THREAD_CONTEXT_SIZE_ARM64
#define RECORD_SIZE (CONTEXT_SIZE + 48)
#define TRANSFER_SIZE (CONTEXT_SIZE + 16)

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
    ldr x21, [x20, #WIT_TEST_MODE_OFFSET]
    ; Register the callback: a data address is rejected, then the callback is installed.
    ldr x0, =WIT_USER_DATA
    mov x1, #WIT_EXCEPTION_VERSION
    mov x2, #0
    SYSCALL WIT_CALL_EXCEPTION_REGISTER
    EXPECT WIT_STATUS_BAD_ADDRESS
    adr x0, callback
    mov x1, #WIT_EXCEPTION_VERSION
    mov x2, #0
    SYSCALL WIT_CALL_EXCEPTION_REGISTER
    EXPECT WIT_STATUS_OK
    ldr x22, =0x1122334455667788
    ; The fault: a read at address zero, delivered to the callback with this context.
    mov x9, #0
fault_site
    ldr x9, [x9]
    b failed
landing
    ; Reached through EXCEPTION_CONTINUE with Pc changed to this label and X22 to 0x5A5A.
    mov x9, #0x5A5A
    cmp x22, x9
    b.ne failed
    ; The activation of the own thread: its callback runs through the fault callback before this call returns.
    ldr x0, =0xFFFFFFFFFFFFFFFE ; WIT_THREAD_SELF
    adr x1, activation_target
    mov x2, #0x77
    SYSCALL WIT_CALL_THREAD_ACTIVATE
    EXPECT WIT_STATUS_OK
    ldr x9, =WIT_USER_DATA + 2048
    ldr x9, [x9]
    cmp x9, #1
    b.ne failed
    ; CONTEXT_PROFILE: the AArch64 block with the FP/SIMD state.
    ldr x0, =WIT_USER_DATA + 2112
    mov x1, #32
    mov x2, #WIT_CPU_CONTEXT_VERSION
    SYSCALL WIT_CALL_CONTEXT_PROFILE
    EXPECT WIT_STATUS_OK
    ldr x9, =WIT_USER_DATA + 2112
    ldr w10, [x9]
    cmp w10, #WIT_CPU_CONTEXT_VERSION
    b.ne failed
    ldr w10, [x9, #4]
    cmp w10, #32
    b.ne failed
    ldr x10, [x9, #8]
    cmp x10, #WIT_CPU_CONTEXT_FPSIMD
    b.ne failed
    ; The own context: running, the AArch64 block, the fixed stack bounds; the running thread's context cannot be set.
    ldr x0, =0xFFFFFFFFFFFFFFFE
    ldr x1, =WIT_USER_DATA
    mov x2, #CONTEXT_SIZE
    SYSCALL WIT_CALL_THREAD_CONTEXT_GET
    EXPECT WIT_STATUS_OK
    ldr x9, =WIT_USER_DATA
    ldr w10, [x9, #32]
    cmp w10, #WIT_THREAD_CONTEXT_RUNNING
    b.ne failed
    ldr w10, [x9, #36]
    cmp w10, #WIT_THREAD_CONTEXT_FPSIMD
    b.ne failed
    ldr x10, [x9, #16]
    ldr x11, =WIT_USER_STACK_BOTTOM
    cmp x10, x11
    b.ne failed
    ldr x10, [x9, #24]
    ldr x11, =WIT_USER_STACK_TOP
    cmp x10, x11
    b.ne failed
    ldr x0, =0xFFFFFFFFFFFFFFFE
    ldr x1, =WIT_USER_DATA
    mov x2, #CONTEXT_SIZE
    SYSCALL WIT_CALL_THREAD_CONTEXT_SET
    EXPECT WIT_STATUS_BUSY
    mov x0, #WIT_TEST_EXIT_CODE
    b process_exit

; The fault callback: x0 token, x1 vector, x2 address, on this thread's stack below the interrupted frame.
callback
    mov x23, x0
    mov x24, x1
    mov x25, x2
    ldr x26, =WIT_USER_DATA
    ; A foreign token is unknown and the record is read with its exact size only.
    add x0, x23, #1
    mov x1, x26
    mov x2, #RECORD_SIZE
    SYSCALL WIT_CALL_EXCEPTION_QUERY
    EXPECT WIT_STATUS_BAD_HANDLE
    mov x0, x23
    mov x1, x26
    mov x2, #(RECORD_SIZE - 1)
    SYSCALL WIT_CALL_EXCEPTION_QUERY
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    mov x0, x23
    mov x1, x26
    mov x2, #RECORD_SIZE
    SYSCALL WIT_CALL_EXCEPTION_QUERY
    EXPECT WIT_STATUS_OK
    ldr w9, [x26]
    cmp w9, #WIT_EXCEPTION_VERSION
    b.ne failed
    ldr w9, [x26, #4]
    cmp w9, #RECORD_SIZE
    b.ne failed
    ldr x9, [x26, #8] ; Token
    cmp x9, x23
    b.ne failed
    ldr x9, [x26, #16] ; Vector
    cmp x9, x24
    b.ne failed
    ldr x9, [x26, #32] ; Address
    cmp x9, x25
    b.ne failed
    ldr w9, [x26, #48 + 32] ; Context.State
    cmp w9, #WIT_THREAD_CONTEXT_RUNNING
    b.ne failed
    ldr w9, [x26, #48 + 36] ; Context.Flags
    cmp w9, #(WIT_THREAD_CONTEXT_FPSIMD + WIT_THREAD_CONTEXT_EXCEPTION_ACTIVE)
    b.ne failed
    cmn x24, #2 ; WIT_EXCEPTION_ACTIVATION_VECTOR is ~1
    b.eq activation
    ; The fault: a data abort (class 0x24) at address zero with the translation-fault syndrome, the interrupted PC at
    ; the faulting instruction, X22 as it was and a PSTATE of condition flags alone.
    cmp x24, #0x24
    b.ne failed
    ldr x9, [x26, #24] ; Error
    ldr x10, =0x92000006
    cmp x9, x10
    b.ne failed
    cbnz x25, failed
    ldr x9, [x26, #48 + 296] ; Context.Pc
    adr x10, fault_site
    cmp x9, x10
    b.ne failed
    ldr x9, [x26, #48 + 40 + 22 * 8] ; Context.X22
    ldr x10, =0x1122334455667788
    cmp x9, x10
    b.ne failed
    ldr x9, [x26, #48 + 304] ; Context.Pstate
    tst x9, #0x0FFFFFFF
    b.ne failed
    cmp x21, #WIT_EXCEPTION_TEST_REJECT
    b.ne continue_changed
    mov x0, x23
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_EXCEPTION_REJECT
    b failed
continue_changed
    ; Continue at the landing with X22 changed; a transfer of the wrong size, a foreign token and a context whose
    ; PSTATE names another exception level are refused first.
    bl build_transfer
    ldr x9, =WIT_USER_DATA + 1024
    adr x10, landing
    str x10, [x9, #16 + 296] ; Context.Pc
    mov x10, #0x5A5A
    str x10, [x9, #16 + 40 + 22 * 8] ; Context.X22
    mov x0, x23
    mov x1, x9
    mov x2, #(TRANSFER_SIZE - 1)
    SYSCALL WIT_CALL_EXCEPTION_CONTINUE
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    add x0, x23, #1
    ldr x1, =WIT_USER_DATA + 1024
    mov x2, #TRANSFER_SIZE
    SYSCALL WIT_CALL_EXCEPTION_CONTINUE
    EXPECT WIT_STATUS_BAD_HANDLE
    ldr x9, =WIT_USER_DATA + 1024
    ldr x10, [x9, #16 + 304]
    orr x11, x10, #0x4 ; M[2]: EL1t
    str x11, [x9, #16 + 304]
    mov x0, x23
    mov x1, x9
    mov x2, #TRANSFER_SIZE
    SYSCALL WIT_CALL_EXCEPTION_CONTINUE
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    ldr x9, =WIT_USER_DATA + 1024
    str x10, [x9, #16 + 304]
    b continue_transfer
activation
    ; The activation record: Address is the callback and Error its argument; the callback runs here and the context
    ; then continues as it was.
    adr x9, activation_target
    cmp x25, x9
    b.ne failed
    ldr x9, [x26, #24]
    cmp x9, #0x77
    b.ne failed
    ldr x0, [x26, #24]
    ldr x9, [x26, #32]
    blr x9
    bl build_transfer
continue_transfer
    mov x0, x23
    ldr x1, =WIT_USER_DATA + 1024
    mov x2, #TRANSFER_SIZE
    SYSCALL WIT_CALL_EXCEPTION_CONTINUE
    b failed

; The transfer: version, size, the current token and a copy of the record's context.
build_transfer
    ldr x9, =WIT_USER_DATA + 1024
    mov w10, #WIT_EXCEPTION_TRANSFER_VERSION
    str w10, [x9]
    mov w10, #TRANSFER_SIZE
    str w10, [x9, #4]
    str x23, [x9, #8]
    add x10, x9, #16
    add x11, x26, #48
    mov x12, #(CONTEXT_SIZE / 16)
copy_context
    ldp x13, x14, [x11], #16
    stp x13, x14, [x10], #16
    subs x12, x12, #1
    b.ne copy_context
    ret

activation_target
    ; x0: the argument 0x77.
    cmp x0, #0x77
    b.ne failed
    ldr x9, =WIT_USER_DATA + 2048
    ldr x10, [x9]
    add x10, x10, #1
    str x10, [x9]
    ret

failed
    mov x0, #241
process_exit
    SYSCALL WIT_CALL_PROCESS_EXIT
    DCD 0x00000000 ; UDF #0
    LTORG
    ENDP
    END
