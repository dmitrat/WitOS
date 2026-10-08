#include "user_abi_a64.h"
; ARM64 process fixture, the port of tests/User.X64/processes.asm (RFC 0011 v3 section 7.8, plan step K5.2c). A
; channel is created and one end goes to a new process through PROCESS_CREATE; the child's program is written into a
; memory object through a writable view, published through an executable view of the fixture's own, and mapped
; executable into the child at a fixed code address beside a stack object; THREAD_CREATE version 3 starts the child's
; first thread with the child-local endpoint handle as its argument; the child sends eight bytes over it and exits with
; 42. The fixture waits for the process, queries it, receives the message, then creates and kills a second child.
; Refusals are checked whole. The data page: the process request at 16, the thread request at 64, the map request at
; 128, the wait request at 192 with its handle at 224, the message at 256 with its data at 304, the process info at
; 352, the child-local handle at 400, a duplicate's output at 408, channel handles at 416 and 432, the stack object at
; 448, the thread handle at 456, the writable view at 464, the status of a failed check at 1304 and the number of
; checks passed at 1312. Registers: x20 startup block, x22 the data page, x23 the code object, x24 the first process,
; x25 the second process, x19 a subroutine's link.

#define FIXED_CODE (WIT_USER_CODE_BASE + 0x10000)
#define FIXED_DATA (WIT_USER_MEMORY_BASE + 0x100000)
#define READ_WRITE (WIT_MEMORY_READ + WIT_MEMORY_WRITE)
#define READ_EXECUTE (WIT_MEMORY_READ + WIT_MEMORY_EXECUTE)
#define STACK_BYTES 16384

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

    ; MEMORY_OBJECT_MAP: handle register, offset, bytes, address (0: chosen), protection, target register -> x0, x1.
    MACRO
    MAPT $handle, $offset, $bytes, $address, $protection, $target
    mov x0, $handle
    mov x1, #$offset
    ldr x2, =$bytes
    ldr x3, =$address
    mov x4, #$protection
    mov x5, $target
    bl object_map
    MEND

    MACRO
    CLOSE $handle
    mov x0, $handle
    SYSCALL WIT_CALL_HANDLE_CLOSE
    MEND

    MACRO
    RELEASE $base
    mov x0, $base
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_MEMORY_RELEASE
    MEND

    ; THREAD_CREATE version 3: entry register, stack pointer register, process register, argument register -> x0, x1.
    MACRO
    CREATE3 $entry, $stack, $process, $argument
    mov x4, $entry
    mov x5, $stack
    mov x6, $process
    mov x7, $argument
    bl thread_create3
    MEND

    EXPORT wit_user_start
wit_user_start PROC
    mov x20, x0
    ldr x22, =WIT_USER_DATA
    ldr w9, [x20]
    cmp w9, #WIT_ABI_VERSION
    b.ne failed
    ; QUERY reports the process family.
    mov x0, #0
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_QUERY
    EXPECT WIT_STATUS_OK
    lsr x1, x1, #32
    tst x1, #WIT_ABI_FEATURE_PROCESSES
    b.eq failed
    ; A channel: A stays here, B goes to the child.
    add x0, x22, #416
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_CHANNEL_CREATE
    EXPECT WIT_STATUS_OK
    ; The code object; it is no endpoint, so PROCESS_CREATE refuses it.
    mov x0, #4096
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_MEMORY_OBJECT_CREATE
    EXPECT WIT_STATUS_OK
    mov x23, x1
    mov x0, x23
    bl process_create
    EXPECT WIT_STATUS_WRONG_TYPE
    ; The child, with B; B is gone from this table.
    ldr x0, [x22, #424]
    bl process_create
    EXPECT WIT_STATUS_OK
    mov x24, x1
    ldr x9, [x22, #424]
    CLOSE x9
    EXPECT WIT_STATUS_BAD_HANDLE
    ; Live, no thread yet.
    mov x0, x24
    bl process_query
    EXPECT WIT_STATUS_OK
    ldr w9, [x22, #(352 + 8)]
    cmp w9, #WIT_PROCESS_STATE_LIVE
    b.ne failed
    ldr w9, [x22, #(352 + 12)]
    cbnz w9, failed
    ; The child's program: written through a writable view, published through an executable view of our own.
    mov x10, #-3 ; WIT_PROCESS_SELF
    MAPT x23, 0, 4096, 0, READ_WRITE, x10
    EXPECT WIT_STATUS_OK
    str x1, [x22, #464]
    adr x11, child_begin
    adr x12, child_end
    mov x13, x1
copy_child
    ldr w9, [x11], #4
    str w9, [x13], #4
    cmp x11, x12
    b.lo copy_child
    mov x10, #-3
    MAPT x23, 0, 4096, FIXED_CODE, READ_EXECUTE, x10
    EXPECT WIT_STATUS_OK
    ldr x0, =FIXED_CODE
    mov x1, #4096
    mov x2, #0
    SYSCALL WIT_CALL_CODE_PUBLISH
    EXPECT WIT_STATUS_OK
    ; A handle without MANAGE may not map into the child; the full handle maps the code at the same fixed address.
    mov x0, x24
    mov x1, #(WIT_RIGHT_WAIT + WIT_RIGHT_QUERY)
    bl duplicate
    EXPECT WIT_STATUS_OK
    str x1, [x22, #408]
    mov x10, x1
    MAPT x23, 0, 4096, FIXED_CODE, READ_EXECUTE, x10
    EXPECT WIT_STATUS_DENIED
    MAPT x23, 0, 4096, FIXED_CODE, READ_EXECUTE, x24
    EXPECT WIT_STATUS_OK
    ldr x9, =FIXED_CODE
    cmp x1, x9
    b.ne failed
    ; The stack object, mapped writable into the child.
    mov x0, #STACK_BYTES
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_MEMORY_OBJECT_CREATE
    EXPECT WIT_STATUS_OK
    str x1, [x22, #448]
    mov x10, x1
    MAPT x10, 0, STACK_BYTES, FIXED_DATA, READ_WRITE, x24
    EXPECT WIT_STATUS_OK
    ; The first thread: a stack pointer outside the child's reservations is refused; the stack's top starts it.
    ldr x10, =FIXED_CODE
    ldr x11, =(FIXED_DATA + 2 * STACK_BYTES)
    ldr x12, [x22, #400]
    CREATE3 x10, x11, x24, x12
    EXPECT WIT_STATUS_BAD_ADDRESS
    ldr x10, =FIXED_CODE
    ldr x11, =(FIXED_DATA + STACK_BYTES)
    ldr x12, [x22, #400]
    CREATE3 x10, x11, x24, x12
    EXPECT WIT_STATUS_OK
    str x1, [x22, #456]
    ; The process ends with the child's exit: its handle is ready, its state exited with 42, its thread gone.
    mov x0, x24
    bl object_wait
    EXPECT WIT_STATUS_OK
    mov x0, x24
    bl process_query
    EXPECT WIT_STATUS_OK
    ldr w9, [x22, #(352 + 8)]
    cmp w9, #WIT_PROCESS_STATE_EXITED
    b.ne failed
    ldr w9, [x22, #(352 + 12)]
    cbnz w9, failed
    ldr x9, [x22, #(352 + 16)]
    cmp x9, #42
    b.ne failed
    ldr x0, [x22, #456]
    bl object_wait
    EXPECT WIT_STATUS_OK
    ; The child's message arrived on A: eight bytes, no handle.
    ldr x0, [x22, #416]
    bl receive
    EXPECT WIT_STATUS_OK
    cmp x1, #8
    b.ne failed
    ldr x9, [x22, #304]
    movz x10, #0x7788
    movk x10, #0x5566, lsl #16
    movk x10, #0x3344, lsl #32
    movk x10, #0x1122, lsl #48
    cmp x9, x10
    b.ne failed
    ; A thread into the ended process is refused.
    ldr x10, =FIXED_CODE
    ldr x11, =(FIXED_DATA + STACK_BYTES)
    ldr x12, [x22, #400]
    CREATE3 x10, x11, x24, x12
    EXPECT WIT_STATUS_CLOSED
    ; A second child, killed before it has a thread: exited with the code given, its handle ready.
    add x0, x22, #432
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_CHANNEL_CREATE
    EXPECT WIT_STATUS_OK
    ldr x0, [x22, #440]
    bl process_create
    EXPECT WIT_STATUS_OK
    mov x25, x1
    mov x0, x25
    mov x1, #7
    mov x2, #0
    SYSCALL WIT_CALL_PROCESS_KILL
    EXPECT WIT_STATUS_OK
    mov x0, x25
    bl process_query
    EXPECT WIT_STATUS_OK
    ldr w9, [x22, #(352 + 8)]
    cmp w9, #WIT_PROCESS_STATE_EXITED
    b.ne failed
    ldr x9, [x22, #(352 + 16)]
    cmp x9, #7
    b.ne failed
    mov x0, x25
    bl object_wait
    EXPECT WIT_STATUS_OK
    mov x0, x25
    mov x1, #7
    mov x2, #0
    SYSCALL WIT_CALL_PROCESS_KILL
    EXPECT WIT_STATUS_CLOSED
    ; Everything goes: the process handles, the thread handle, the channels, the views and the objects.
    CLOSE x25
    EXPECT WIT_STATUS_OK
    ldr x9, [x22, #432]
    CLOSE x9
    EXPECT WIT_STATUS_OK
    CLOSE x24
    EXPECT WIT_STATUS_OK
    ldr x9, [x22, #408]
    CLOSE x9
    EXPECT WIT_STATUS_OK
    ldr x9, [x22, #456]
    CLOSE x9
    EXPECT WIT_STATUS_OK
    ldr x9, [x22, #416]
    CLOSE x9
    EXPECT WIT_STATUS_OK
    ldr x9, =FIXED_CODE
    RELEASE x9
    EXPECT WIT_STATUS_OK
    ldr x9, [x22, #464]
    RELEASE x9
    EXPECT WIT_STATUS_OK
    CLOSE x23
    EXPECT WIT_STATUS_OK
    ldr x9, [x22, #448]
    CLOSE x9
    EXPECT WIT_STATUS_OK
    mov x0, #WIT_TEST_EXIT_CODE
    b exit_process

; The child's program, copied into its code object: position-independent (no literal pool), the child-local endpoint
; handle in x0. It sends eight bytes over the endpoint from its stack and exits with 42 (43 on a failure).
child_begin
    mov x19, x0
    sub sp, sp, #128
    mov w9, #WIT_CHANNEL_MESSAGE_VERSION
    str w9, [sp]
    mov w9, #40
    str w9, [sp, #4]
    add x9, sp, #64
    str x9, [sp, #8]
    str xzr, [sp, #16]
    mov w9, #8
    str w9, [sp, #24]
    str wzr, [sp, #28]
    str xzr, [sp, #32]
    movz x9, #0x7788
    movk x9, #0x5566, lsl #16
    movk x9, #0x3344, lsl #32
    movk x9, #0x1122, lsl #48
    str x9, [sp, #64]
    mov x0, x19
    mov x1, sp
    mov x2, #40
    mov x8, #WIT_CALL_CHANNEL_SEND
    svc #0
    cbnz x0, child_failed
    mov x0, #42
    mov x1, #0
    mov x2, #0
    mov x8, #WIT_CALL_PROCESS_EXIT
    svc #0
    DCD 0x00000000
child_failed
    mov x0, #43
    mov x1, #0
    mov x2, #0
    mov x8, #WIT_CALL_PROCESS_EXIT
    svc #0
    DCD 0x00000000
child_end

; PROCESS_CREATE: x0 endpoint handle -> x0, x1 the process handle; the child-local endpoint handle at 400.
process_create
    add x9, x22, #16
    mov w10, #WIT_PROCESS_CREATE_VERSION
    str w10, [x9]
    mov w10, #32
    str w10, [x9, #4]
    str x0, [x9, #8]
    str xzr, [x9, #16]
    str xzr, [x9, #24]
    mov x0, x9
    mov x1, #32
    add x2, x22, #400
    SYSCALL WIT_CALL_PROCESS_CREATE
    ret

; PROCESS_QUERY: x0 process handle -> x0; the info at 352.
process_query
    mov w10, #WIT_PROCESS_INFO_VERSION
    str w10, [x22, #352]
    mov w10, #40
    str w10, [x22, #356]
    add x1, x22, #352
    mov x2, #40
    SYSCALL WIT_CALL_PROCESS_QUERY
    ret

; THREAD_CREATE version 3 at data page + 64: x4 entry, x5 stack pointer, x6 process, x7 argument -> x0, x1 handle.
thread_create3
    add x9, x22, #64
    mov w10, #WIT_THREAD_CREATE_VERSION_3
    str w10, [x9]
    mov w10, #56
    str w10, [x9, #4]
    str x4, [x9, #8]
    str x7, [x9, #16]
    str x5, [x9, #24]
    str xzr, [x9, #32]
    str x6, [x9, #40]
    str xzr, [x9, #48]
    mov x0, x9
    mov x1, #56
    mov x2, #0
    SYSCALL WIT_CALL_THREAD_CREATE
    ret

; The map request at data page + 128: x0 object, x1 offset, x2 bytes, x3 address, x4 protection, x5 target.
object_map
    add x9, x22, #128
    mov w10, #WIT_MEMORY_MAP_VERSION
    str w10, [x9]
    mov w10, #56
    str w10, [x9, #4]
    str x0, [x9, #8]
    str x1, [x9, #16]
    str x2, [x9, #24]
    str x3, [x9, #32]
    str w4, [x9, #40]
    str wzr, [x9, #44]
    str x5, [x9, #48]
    mov x0, x9
    mov x1, #56
    mov x2, #0
    SYSCALL WIT_CALL_MEMORY_OBJECT_MAP
    ret

; OBJECT_WAIT on one handle without a deadline: x0 handle -> x0, x1 the winner.
object_wait
    str x0, [x22, #224]
    add x9, x22, #192
    mov w10, #WIT_WAIT_OBJECTS_VERSION
    str w10, [x9]
    mov w10, #32
    str w10, [x9, #4]
    add x10, x22, #224
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

; CHANNEL_RECEIVE of up to 16 bytes and no handle into data page + 304: x0 endpoint -> x0, x1 bytes and handles.
receive
    add x9, x22, #256
    mov w10, #WIT_CHANNEL_MESSAGE_VERSION
    str w10, [x9]
    mov w10, #40
    str w10, [x9, #4]
    add x10, x22, #304
    str x10, [x9, #8]
    str xzr, [x9, #16]
    mov w10, #16
    str w10, [x9, #24]
    str wzr, [x9, #28]
    str xzr, [x9, #32]
    mov x1, x9
    mov x2, #40
    SYSCALL WIT_CALL_CHANNEL_RECEIVE
    ret

; HANDLE_DUPLICATE: x0 source, x1 rights -> x0, x1 the new handle.
duplicate
    mov x2, x1
    add x1, x22, #408
    SYSCALL WIT_CALL_HANDLE_DUPLICATE
    ldr x1, [x22, #408]
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
