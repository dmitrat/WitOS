#include "user_abi_a64.h"
; ARM64 channel fixture, the port of tests/User.X64/channels.asm over the channels of ABI-1 (RFC 0011 v3 section
; 7.6, plan step K2): CHANNEL_CREATE, CHANNEL_SEND and CHANNEL_RECEIVE with message boundaries, the rights of
; endpoint handles, the queue depth, the peer's close, capabilities (events, endpoints, a thread handle) moved with a
; message, a wait on an endpoint from another thread, the channel and handle quotas and a message dropped with its
; endpoint. The requests and buffers live in the data page: the two created handles at 0, the request at 16, the
; payload at 128, the receive buffer at 512, the handles to move at 768, the received handles at 800, the handles of a
; scenario at 832, the duplicates at 1040 and the output of HANDLE_DUPLICATE at 1200. Registers: x20 startup block,
; x21 test mode, x22 the data page, x23 and x24 the endpoints of the first channel, x25 to x27 scenario state.

#define ALL_RIGHTS (WIT_RIGHT_WAIT + WIT_RIGHT_SIGNAL + WIT_RIGHT_TRANSFER)

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

    ; CHANNEL_SEND: endpoint register, bytes of the payload, handles to move -> x0.
    MACRO
    SEND $endpoint, $bytes, $handles
    mov x0, $endpoint
    mov x1, #$bytes
    mov x2, #$handles
    bl send_message
    MEND

    ; CHANNEL_RECEIVE: endpoint register, capacity, handle slots -> x0, x1 bytes | handles lsl 32.
    MACRO
    RECEIVE $endpoint, $bytes, $handles
    mov x0, $endpoint
    mov x1, #$bytes
    mov x2, #$handles
    bl receive_message
    MEND

    ; EVENT_CREATE with rights -> x1.
    MACRO
    CREATE_EVENT $rights
    mov x0, #0
    mov x1, #$rights
    mov x2, #0
    SYSCALL WIT_CALL_EVENT_CREATE
    EXPECT WIT_STATUS_OK
    MEND

    MACRO
    SET_EVENT $handle
    mov x0, $handle
    SYSCALL WIT_CALL_EVENT_SET
    MEND

    ; HANDLE_DUPLICATE with rights -> x0, x1 the new handle.
    MACRO
    DUPLICATE $handle, $rights
    mov x0, $handle
    mov x2, #$rights
    bl duplicate_handle
    MEND

    MACRO
    CLOSE $handle
    mov x0, $handle
    SYSCALL WIT_CALL_HANDLE_CLOSE
    MEND

    MACRO
    WAIT_OBJECT $handle, $deadline
    mov x0, $handle
    mov x1, $deadline
    bl wait_object
    MEND

    MACRO
    CREATE_THREAD $target
    adr x0, $target
    mov x1, #0
    mov x2, #0
    bl thread_create
    EXPECT WIT_STATUS_OK
    MEND

    MACRO
    JOIN $handle
    mov x0, $handle
    bl thread_join
    EXPECT WIT_STATUS_OK
    cmp x1, #WIT_TEST_EXIT_CODE
    b.ne failed
    MEND

    MACRO
    SLEEP_TICKS $ticks
    mov x0, #$ticks
    bl deadline_ticks
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
    ; The payload: byte i holds i + 1.
    mov x9, #0
fill
    add w10, w9, #1
    add x11, x22, #128
    strb w10, [x11, x9]
    add x9, x9, #1
    cmp x9, #256
    b.lo fill
    cmp x21, #WIT_CHANNEL_TEST_WAIT
    b.eq wait_test
    cmp x21, #WIT_CHANNEL_TEST_TRANSFER
    b.eq transfer_test
    cmp x21, #WIT_CHANNEL_TEST_LIMITS
    b.eq limits_test
    cmp x21, #WIT_CHANNEL_TEST_DROP
    b.eq drop_test
    cmp x21, #WIT_CHANNEL_TEST_BASIC
    b.ne failed

    ; Creation validates its flags and the output before it takes a channel.
    mov x0, x22
    mov x1, #1
    mov x2, #0
    SYSCALL WIT_CALL_CHANNEL_CREATE
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    mov x0, #0
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_CHANNEL_CREATE
    EXPECT WIT_STATUS_BAD_ADDRESS
    bl channel_create
    EXPECT WIT_STATUS_OK
    ldr x23, [x22]
    ldr x24, [x22, #8]
    cbz x23, failed
    cbz x24, failed
    cmp x23, x24
    b.eq failed
    RECEIVE x23, 256, 4
    EXPECT WIT_STATUS_TIMED_OUT
    cbnz x1, failed
    ; Two framed messages; the quotas and the request format are checked before anything is queued.
    SEND x23, 3, 0
    EXPECT WIT_STATUS_OK
    SEND x23, 256, 0
    EXPECT WIT_STATUS_OK
    SEND x23, 257, 0
    EXPECT WIT_STATUS_TOO_LARGE
    SEND x23, 1, 5
    EXPECT WIT_STATUS_TOO_LARGE
    add x25, x22, #16
    mov w9, #2 ; a foreign version
    str w9, [x25]
    mov x0, x23
    mov x1, x25
    mov x2, #40
    SYSCALL WIT_CALL_CHANNEL_SEND
    EXPECT WIT_STATUS_UNSUPPORTED
    mov w9, #WIT_CHANNEL_MESSAGE_VERSION
    str w9, [x25]
    mov x0, x23
    mov x1, x25
    mov x2, #39 ; a wrong size
    SYSCALL WIT_CALL_CHANNEL_SEND
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    mov w9, #1 ; a flag
    str w9, [x25, #32]
    mov x0, x23
    mov x1, x25
    mov x2, #40
    SYSCALL WIT_CALL_CHANNEL_SEND
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    str wzr, [x25, #32]
    str xzr, [x25, #8] ; unmapped data
    mov w9, #1
    str w9, [x25, #24]
    str wzr, [x25, #28]
    mov x0, x23
    mov x1, x25
    mov x2, #40
    SYSCALL WIT_CALL_CHANNEL_SEND
    EXPECT WIT_STATUS_BAD_ADDRESS
    add x9, x22, #128
    str x9, [x25, #8]
    str xzr, [x25, #16] ; unmapped handles
    mov w9, #1
    str w9, [x25, #28]
    mov x0, x23
    mov x1, x25
    mov x2, #40
    SYSCALL WIT_CALL_CHANNEL_SEND
    EXPECT WIT_STATUS_BAD_ADDRESS
    ; Delivery keeps the boundaries: a buffer too small leaves the message, each receive takes one message whole.
    RECEIVE x24, 2, 0
    EXPECT WIT_STATUS_TOO_LARGE
    RECEIVE x24, 256, 0
    EXPECT WIT_STATUS_OK
    cmp x1, #3
    b.ne failed
    ldrb w9, [x22, #512]
    cmp w9, #1
    b.ne failed
    ldrb w9, [x22, #514]
    cmp w9, #3
    b.ne failed
    ldrb w9, [x22, #515]
    cbnz w9, failed
    RECEIVE x24, 256, 0
    EXPECT WIT_STATUS_OK
    cmp x1, #256
    b.ne failed
    ldrb w9, [x22, #512 + 254]
    cmp w9, #255
    b.ne failed
    ldrb w9, [x22, #512 + 255]
    cbnz w9, failed
    RECEIVE x24, 256, 0
    EXPECT WIT_STATUS_TIMED_OUT
    ; Rights: a handle without SEND cannot send, one without DUPLICATE cannot be duplicated, one without WAIT cannot
    ; be waited for, and a right outside the endpoint's is unsupported.
    DUPLICATE x23, (WIT_RIGHT_WAIT + WIT_RIGHT_RECEIVE)
    EXPECT WIT_STATUS_OK
    mov x25, x1
    SEND x25, 1, 0
    EXPECT WIT_STATUS_DENIED
    RECEIVE x25, 256, 0
    EXPECT WIT_STATUS_TIMED_OUT
    CLOSE x25
    EXPECT WIT_STATUS_OK
    DUPLICATE x23, WIT_RIGHT_SIGNAL
    EXPECT WIT_STATUS_UNSUPPORTED
    DUPLICATE x23, WIT_RIGHT_SEND
    EXPECT WIT_STATUS_OK
    mov x25, x1
    DUPLICATE x25, 0
    EXPECT WIT_STATUS_DENIED
    CLOSE x25
    EXPECT WIT_STATUS_OK
    DUPLICATE x23, (WIT_RIGHT_SEND + WIT_RIGHT_RECEIVE)
    EXPECT WIT_STATUS_OK
    mov x25, x1
    WAIT_OBJECT x25, #0
    EXPECT WIT_STATUS_DENIED
    CLOSE x25
    EXPECT WIT_STATUS_OK
    ; The queue holds four messages; the fifth waits for room.
    mov x27, #4
fill_queue
    SEND x23, 1, 0
    EXPECT WIT_STATUS_OK
    subs x27, x27, #1
    b.ne fill_queue
    SEND x23, 1, 0
    EXPECT WIT_STATUS_BUSY
    mov x27, #4
drain
    RECEIVE x24, 256, 0
    EXPECT WIT_STATUS_OK
    cmp x1, #1
    b.ne failed
    subs x27, x27, #1
    b.ne drain
    RECEIVE x24, 256, 0
    EXPECT WIT_STATUS_TIMED_OUT
    ; The peer's close drops what it had not received; the survivor learns PEER_CLOSED both ways.
    SEND x23, 1, 0
    EXPECT WIT_STATUS_OK
    SEND x23, 1, 0
    EXPECT WIT_STATUS_OK
    CLOSE x24
    EXPECT WIT_STATUS_OK
    SEND x23, 1, 0
    EXPECT WIT_STATUS_PEER_CLOSED
    RECEIVE x23, 256, 0
    EXPECT WIT_STATUS_PEER_CLOSED
    SEND x24, 1, 0
    EXPECT WIT_STATUS_BAD_HANDLE
    CLOSE x23
    EXPECT WIT_STATUS_OK
    RECEIVE x23, 256, 0
    EXPECT WIT_STATUS_BAD_HANDLE
    CLOSE x23
    EXPECT WIT_STATUS_BAD_HANDLE
    b passed

wait_test
    ; Another thread sends a message after a delay and closes its endpoint after another: each wakes the wait.
    bl channel_create
    EXPECT WIT_STATUS_OK
    ldr x23, [x22]
    ldr x24, [x22, #8]
    CREATE_THREAD wait_sender
    mov x25, x1
    WAIT_OBJECT x23, #-1
    EXPECT WIT_STATUS_OK
    cbnz x1, failed
    RECEIVE x23, 256, 0
    EXPECT WIT_STATUS_OK
    cmp x1, #1
    b.ne failed
    ldrb w9, [x22, #512]
    cmp w9, #7
    b.ne failed
    WAIT_OBJECT x23, #-1
    EXPECT WIT_STATUS_OK
    RECEIVE x23, 256, 0
    EXPECT WIT_STATUS_PEER_CLOSED
    JOIN x25
    CLOSE x23
    EXPECT WIT_STATUS_OK
    b passed

wait_sender
    ldr x22, =WIT_USER_DATA
    ldr x24, [x22, #8]
    SLEEP_TICKS 2
    mov w9, #7
    strb w9, [x22, #128]
    SEND x24, 1, 0
    EXPECT WIT_STATUS_OK
    SLEEP_TICKS 2
    CLOSE x24
    EXPECT WIT_STATUS_OK
    b thread_passed

transfer_test
    bl channel_create
    EXPECT WIT_STATUS_OK
    ldr x23, [x22]
    ldr x24, [x22, #8]
    ; An event moves with a message: the sender's handle is gone, the receiver's works.
    CREATE_EVENT ALL_RIGHTS
    str x1, [x22, #768]
    mov x25, x1
    SEND x23, 1, 1
    EXPECT WIT_STATUS_OK
    SET_EVENT x25
    EXPECT WIT_STATUS_BAD_HANDLE
    RECEIVE x24, 256, 1
    EXPECT WIT_STATUS_OK
    mov x9, #1
    orr x9, x9, #0x100000000
    cmp x1, x9
    b.ne failed
    ldr x26, [x22, #800]
    cmp x26, x25
    b.eq failed
    SET_EVENT x26
    EXPECT WIT_STATUS_OK
    WAIT_OBJECT x26, #0
    EXPECT WIT_STATUS_OK
    ; Attenuated before sending: the receiver cannot signal, but sees the signal of the full handle.
    DUPLICATE x26, (WIT_RIGHT_WAIT + WIT_RIGHT_TRANSFER)
    EXPECT WIT_STATUS_OK
    str x1, [x22, #768]
    SEND x23, 0, 1
    EXPECT WIT_STATUS_OK
    RECEIVE x24, 256, 1
    EXPECT WIT_STATUS_OK
    mov x9, #0x100000000
    cmp x1, x9
    b.ne failed
    ldr x27, [x22, #800]
    SET_EVENT x27
    EXPECT WIT_STATUS_DENIED
    WAIT_OBJECT x27, #0
    EXPECT WIT_STATUS_TIMED_OUT
    SET_EVENT x26
    EXPECT WIT_STATUS_OK
    WAIT_OBJECT x27, #0
    EXPECT WIT_STATUS_OK
    CLOSE x27
    EXPECT WIT_STATUS_OK
    CLOSE x26
    EXPECT WIT_STATUS_OK
    ; Without TRANSFER a handle stays where it is; a handle named twice is refused whole.
    CREATE_EVENT 0
    str x1, [x22, #768]
    mov x25, x1
    SEND x23, 1, 1
    EXPECT WIT_STATUS_DENIED
    SET_EVENT x25
    EXPECT WIT_STATUS_OK
    CLOSE x25
    EXPECT WIT_STATUS_OK
    CREATE_EVENT ALL_RIGHTS
    str x1, [x22, #768]
    str x1, [x22, #776]
    mov x25, x1
    SEND x23, 1, 2
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    SET_EVENT x25
    EXPECT WIT_STATUS_OK
    CLOSE x25
    EXPECT WIT_STATUS_OK
    ; An endpoint moves too: the received handle talks to the other end of its own channel.
    bl channel_create
    EXPECT WIT_STATUS_OK
    ldr x25, [x22, #8]
    str x25, [x22, #768]
    SEND x23, 0, 1
    EXPECT WIT_STATUS_OK
    SEND x25, 1, 0
    EXPECT WIT_STATUS_BAD_HANDLE
    RECEIVE x24, 256, 1
    EXPECT WIT_STATUS_OK
    ldr x26, [x22, #800]
    mov w9, #9
    strb w9, [x22, #128]
    ldr x25, [x22]
    SEND x25, 1, 0
    EXPECT WIT_STATUS_OK
    RECEIVE x26, 256, 0
    EXPECT WIT_STATUS_OK
    cmp x1, #1
    b.ne failed
    ldrb w9, [x22, #512]
    cmp w9, #9
    b.ne failed
    CLOSE x25
    EXPECT WIT_STATUS_OK
    RECEIVE x26, 256, 0
    EXPECT WIT_STATUS_PEER_CLOSED
    CLOSE x26
    EXPECT WIT_STATUS_OK
    ; A thread handle moves with its record: the receiver joins the thread.
    CREATE_THREAD exit_worker
    str x1, [x22, #768]
    mov x25, x1
    SEND x23, 0, 1
    EXPECT WIT_STATUS_OK
    mov x0, x25
    bl thread_join
    EXPECT WIT_STATUS_BAD_HANDLE
    RECEIVE x24, 256, 1
    EXPECT WIT_STATUS_OK
    ldr x26, [x22, #800]
    JOIN x26
    CLOSE x23
    EXPECT WIT_STATUS_OK
    CLOSE x24
    EXPECT WIT_STATUS_OK
    b passed

exit_worker
    b thread_passed

limits_test
    ; Four channels, then NO_MEMORY with the output untouched; the handle table fills with duplicates, a moved
    ; handle frees its slot, and a message whose handle would not fit stays queued until a slot is free.
    mov x27, #0
channels
    bl channel_create
    EXPECT WIT_STATUS_OK
    ldr x9, [x22]
    add x10, x22, #832
    str x9, [x10, x27, lsl #3]
    ldr x9, [x22, #8]
    add x10, x22, #840
    str x9, [x10, x27, lsl #3]
    add x27, x27, #2
    cmp x27, #8
    b.lo channels
    ldr x9, =0xAAAAAAAAAAAAAAAA
    str x9, [x22]
    str x9, [x22, #8]
    bl channel_create
    EXPECT WIT_STATUS_NO_MEMORY
    ldr x9, =0xAAAAAAAAAAAAAAAA
    ldr x10, [x22]
    cmp x10, x9
    b.ne failed
    ldr x10, [x22, #8]
    cmp x10, x9
    b.ne failed
    mov x27, #0
duplicates
    ldr x25, [x22, #832]
    DUPLICATE x25, 0
    cmp x0, #WIT_STATUS_NO_MEMORY
    b.eq duplicates_done
    EXPECT WIT_STATUS_OK
    add x10, x22, #1040
    str x1, [x10, x27, lsl #3]
    add x27, x27, #1
    cmp x27, #16
    b.lo duplicates
    b failed
duplicates_done
    ; The duplicates that filled the table: the thread's identity, the eight endpoints and whatever else the kernel
    ; holds for the component precede them, so at least four fit.
    mov x28, x27
    cmp x27, #4
    b.lo failed
    add x10, x22, #1032
    ldr x9, [x10, x28, lsl #3] ; the last duplicate
    str x9, [x22, #768]
    ldr x25, [x22, #832]
    ldr x26, [x22, #840]
    SEND x25, 0, 1
    EXPECT WIT_STATUS_OK
    RECEIVE x26, 256, 1
    EXPECT WIT_STATUS_OK
    ldr x9, [x22, #800]
    str x9, [x22, #1040 + 6 * 8]
    str x9, [x22, #768]
    SEND x25, 0, 1
    EXPECT WIT_STATUS_OK
    DUPLICATE x25, 0
    EXPECT WIT_STATUS_OK
    str x1, [x22, #1208]
    RECEIVE x26, 256, 1
    EXPECT WIT_STATUS_NO_MEMORY
    ldr x9, [x22, #1208]
    CLOSE x9
    EXPECT WIT_STATUS_OK
    RECEIVE x26, 256, 1
    EXPECT WIT_STATUS_OK
    ldr x9, [x22, #800]
    add x10, x22, #1032
    str x9, [x10, x28, lsl #3]
    mov x27, #0
close_duplicates
    add x10, x22, #1040
    ldr x9, [x10, x27, lsl #3]
    CLOSE x9
    EXPECT WIT_STATUS_OK
    add x27, x27, #1
    cmp x27, x28
    b.lo close_duplicates
    mov x27, #0
close_channels
    add x10, x22, #832
    ldr x9, [x10, x27, lsl #3]
    CLOSE x9
    EXPECT WIT_STATUS_OK
    add x27, x27, #1
    cmp x27, #8
    b.lo close_channels
    bl channel_create
    EXPECT WIT_STATUS_OK
    ldr x9, [x22]
    CLOSE x9
    EXPECT WIT_STATUS_OK
    ldr x9, [x22, #8]
    CLOSE x9
    EXPECT WIT_STATUS_OK
    b passed

drop_test
    ; A message in a closed endpoint's queue is dropped with the event it carried: the event quota is whole again.
    bl channel_create
    EXPECT WIT_STATUS_OK
    ldr x23, [x22]
    ldr x24, [x22, #8]
    CREATE_EVENT ALL_RIGHTS
    str x1, [x22, #768]
    SEND x23, 1, 1
    EXPECT WIT_STATUS_OK
    CLOSE x24
    EXPECT WIT_STATUS_OK
    mov x27, #0
events
    CREATE_EVENT 0
    add x10, x22, #832
    str x1, [x10, x27, lsl #3]
    add x27, x27, #1
    cmp x27, #4
    b.lo events
    mov x0, #0
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_EVENT_CREATE
    EXPECT WIT_STATUS_NO_MEMORY
    mov x27, #0
close_events
    add x10, x22, #832
    ldr x9, [x10, x27, lsl #3]
    CLOSE x9
    EXPECT WIT_STATUS_OK
    add x27, x27, #1
    cmp x27, #4
    b.lo close_events
    SEND x23, 1, 0
    EXPECT WIT_STATUS_PEER_CLOSED
    CLOSE x23
    EXPECT WIT_STATUS_OK
    b passed

; CHANNEL_CREATE into the data page -> x0.
channel_create
    mov x0, x22
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_CHANNEL_CREATE
    ret

; The message request: x0 endpoint, x1 bytes, x2 handles; data from the payload, handles from the move list.
send_message
    add x9, x22, #16
    mov w10, #WIT_CHANNEL_MESSAGE_VERSION
    str w10, [x9]
    mov w10, #40
    str w10, [x9, #4]
    add x10, x22, #128
    str x10, [x9, #8]
    add x10, x22, #768
    str x10, [x9, #16]
    str w1, [x9, #24]
    str w2, [x9, #28]
    str wzr, [x9, #32]
    str wzr, [x9, #36]
    mov x1, x9
    mov x2, #40
    SYSCALL WIT_CALL_CHANNEL_SEND
    ret

; The receive request: x0 endpoint, x1 capacity, x2 handle slots; data to the buffer, handles to the received list.
receive_message
    add x9, x22, #16
    mov w10, #WIT_CHANNEL_MESSAGE_VERSION
    str w10, [x9]
    mov w10, #40
    str w10, [x9, #4]
    add x10, x22, #512
    str x10, [x9, #8]
    add x10, x22, #800
    str x10, [x9, #16]
    str w1, [x9, #24]
    str w2, [x9, #28]
    str wzr, [x9, #32]
    str wzr, [x9, #36]
    mov x1, x9
    mov x2, #40
    SYSCALL WIT_CALL_CHANNEL_RECEIVE
    ret

; HANDLE_DUPLICATE: x0 source, x2 rights -> x0, x1 the new handle.
duplicate_handle
    add x1, x22, #1200
    SYSCALL WIT_CALL_HANDLE_DUPLICATE
    ldr x1, [x22, #1200]
    ret

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

; OBJECT_WAIT on one handle: x0 handle, x1 deadline; x0 status, x1 winner.
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

; Absolute monotonic deadline x0 ticks of 10 ms from now, into x9.
deadline_ticks
    mov x12, x30
    mov x11, x0
    mov x0, #0
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_CLOCK_FREQUENCY
    EXPECT WIT_STATUS_OK
    mov x10, #100
    udiv x9, x1, x10
    mul x9, x9, x11
    mov x0, #0
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_CLOCK_READ
    EXPECT WIT_STATUS_OK
    add x9, x9, x1
    mov x30, x12
    ret

failed
    str x0, [x22, #1304] ; the status the failed check saw
    mov x0, #241
    b exit_process
passed
    mov x0, #WIT_TEST_EXIT_CODE
exit_process
    SYSCALL WIT_CALL_PROCESS_EXIT
    DCD 0x00000000 ; UDF #0
thread_passed
    mov x0, #WIT_TEST_EXIT_CODE ; THREAD_EXIT(code, 0, 0): the other arguments must be zero
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_THREAD_EXIT
    DCD 0x00000000 ; UDF #0
    LTORG
    ENDP
    END
