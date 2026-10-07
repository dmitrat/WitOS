option casemap:none
include user_abi.inc

; Channel fixture over the channels of ABI-1 (RFC 0011 v3 section 7.6, plan step K2): CHANNEL_CREATE, CHANNEL_SEND
; and CHANNEL_RECEIVE with message boundaries, the rights of endpoint handles, the queue depth, the peer's close,
; capabilities (events, endpoints, a thread handle) moved with a message, a wait on an endpoint from another thread,
; the channel and handle quotas and a message dropped with its endpoint. The requests and buffers live in the data
; page: the two created handles at 0, the request at 16, the payload at 128, the receive buffer at 512, the handles
; to move at 768, the received handles at 800, the handles of a scenario at 832, the duplicates at 1040 and the
; output of HANDLE_DUPLICATE at 1200. The calls are subroutines and the checks compare the low status word so that
; the fixture stays within its one page of code. Registers: r15 startup block, r14 test mode, rbx the data page, r12
; and r13 the endpoints of the first channel.

ALL_RIGHTS EQU WIT_RIGHT_WAIT + WIT_RIGHT_SIGNAL + WIT_RIGHT_TRANSFER

EXPECT MACRO value
    cmp eax, value
    jne failed
ENDM
CALL0 MACRO operation
    mov eax, operation
    int 80h
ENDM
; CHANNEL_SEND: endpoint, bytes of the payload, handles to move -> rax.
SEND MACRO endpoint, bytes, handles
    mov rcx, endpoint
    mov edx, bytes + (handles SHL 16)
    call send_message
ENDM
; CHANNEL_RECEIVE: endpoint, capacity, handle slots -> rax, rdx bytes | handles shl 32.
RECEIVE MACRO endpoint, bytes, handles
    mov rcx, endpoint
    mov edx, bytes + (handles SHL 16)
    call receive_message
ENDM
; EVENT_CREATE with rights -> rdx.
CREATE_EVENT MACRO rights
    mov edx, rights
    call create_event
    EXPECT WIT_STATUS_OK
ENDM
SET_EVENT MACRO handle
    mov rcx, handle
    call set_event
ENDM
; HANDLE_DUPLICATE with rights -> rax, rdx the new handle.
DUPLICATE MACRO handle, rights
    mov rcx, handle
    mov edx, rights
    call duplicate_handle
ENDM
CLOSE MACRO handle
    mov rcx, handle
    call close_handle
ENDM
WAIT_OBJECT MACRO handle, deadline
    mov rcx, handle
    mov rdx, deadline
    call wait_object
ENDM
JOIN MACRO handle
    mov rcx, handle
    call thread_join
    EXPECT WIT_STATUS_OK
    cmp edx, WIT_TEST_EXIT_CODE
    jne failed
ENDM

.code
PUBLIC wit_user_start
wit_user_start PROC
    mov r15, rcx
    mov rbx, WIT_USER_DATA
    cmp DWORD PTR [r15], WIT_ABI_VERSION
    jne failed
    mov r14, [r15 + WIT_TEST_MODE_OFFSET]
    ; The payload: byte i holds i + 1.
    xor ecx, ecx
fill:
    lea eax, [rcx + 1]
    mov BYTE PTR [rbx + 128 + rcx], al
    inc ecx
    cmp ecx, 256
    jb fill
    cmp r14, WIT_CHANNEL_TEST_WAIT
    je wait_test
    cmp r14, WIT_CHANNEL_TEST_TRANSFER
    je transfer_test
    cmp r14, WIT_CHANNEL_TEST_LIMITS
    je limits_test
    cmp r14, WIT_CHANNEL_TEST_DROP
    je drop_test
    cmp r14, WIT_CHANNEL_TEST_BASIC
    jne failed

    ; Creation validates its flags and the output before it takes a channel.
    mov rcx, rbx
    mov edx, 1
    xor r8d, r8d
    CALL0 WIT_CALL_CHANNEL_CREATE
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    xor ecx, ecx
    xor edx, edx
    CALL0 WIT_CALL_CHANNEL_CREATE
    EXPECT WIT_STATUS_BAD_ADDRESS
    call channel_create
    EXPECT WIT_STATUS_OK
    mov r12, [rbx]
    mov r13, [rbx + 8]
    test r12, r12
    je failed
    test r13, r13
    je failed
    cmp r12, r13
    je failed
    RECEIVE r12, 256, 4
    EXPECT WIT_STATUS_TIMED_OUT
    test rdx, rdx
    jne failed
    ; Two framed messages; the quotas and the request format are checked before anything is queued.
    SEND r12, 3, 0
    EXPECT WIT_STATUS_OK
    SEND r12, 256, 0
    EXPECT WIT_STATUS_OK
    SEND r12, 257, 0
    EXPECT WIT_STATUS_TOO_LARGE
    SEND r12, 1, 5
    EXPECT WIT_STATUS_TOO_LARGE
    lea rsi, [rbx + 16] ; the request the last send left
    mov DWORD PTR [rsi], 2 ; a foreign version
    mov r8d, 40
    call raw_send
    EXPECT WIT_STATUS_UNSUPPORTED
    mov DWORD PTR [rsi], WIT_CHANNEL_MESSAGE_VERSION
    mov r8d, 39 ; a wrong size
    call raw_send
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    mov DWORD PTR [rsi + 32], 1 ; a flag
    mov r8d, 40
    call raw_send
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    mov DWORD PTR [rsi + 32], 0
    mov QWORD PTR [rsi + 8], 0 ; unmapped data
    mov DWORD PTR [rsi + 24], 1
    mov DWORD PTR [rsi + 28], 0
    call raw_send
    EXPECT WIT_STATUS_BAD_ADDRESS
    lea rax, [rbx + 128]
    mov [rsi + 8], rax
    mov QWORD PTR [rsi + 16], 0 ; unmapped handles
    mov DWORD PTR [rsi + 28], 1
    call raw_send
    EXPECT WIT_STATUS_BAD_ADDRESS
    ; Delivery keeps the boundaries: a buffer too small leaves the message, each receive takes one message whole.
    RECEIVE r13, 2, 0
    EXPECT WIT_STATUS_TOO_LARGE
    RECEIVE r13, 256, 0
    EXPECT WIT_STATUS_OK
    cmp edx, 3
    jne failed
    cmp BYTE PTR [rbx + 512], 1
    jne failed
    cmp BYTE PTR [rbx + 514], 3
    jne failed
    cmp BYTE PTR [rbx + 515], 0
    jne failed
    RECEIVE r13, 256, 0
    EXPECT WIT_STATUS_OK
    cmp edx, 256
    jne failed
    cmp BYTE PTR [rbx + 512 + 254], 255
    jne failed
    cmp BYTE PTR [rbx + 512 + 255], 0
    jne failed
    RECEIVE r13, 256, 0
    EXPECT WIT_STATUS_TIMED_OUT
    ; Rights: a handle without SEND cannot send, one without DUPLICATE cannot be duplicated, one without WAIT cannot
    ; be waited for, and a right outside the endpoint's is unsupported.
    DUPLICATE r12, WIT_RIGHT_WAIT + WIT_RIGHT_RECEIVE
    EXPECT WIT_STATUS_OK
    mov rsi, rdx
    SEND rsi, 1, 0
    EXPECT WIT_STATUS_DENIED
    RECEIVE rsi, 256, 0
    EXPECT WIT_STATUS_TIMED_OUT
    CLOSE rsi
    EXPECT WIT_STATUS_OK
    DUPLICATE r12, WIT_RIGHT_SIGNAL
    EXPECT WIT_STATUS_UNSUPPORTED
    DUPLICATE r12, WIT_RIGHT_SEND
    EXPECT WIT_STATUS_OK
    mov rsi, rdx
    DUPLICATE rsi, 0
    EXPECT WIT_STATUS_DENIED
    CLOSE rsi
    EXPECT WIT_STATUS_OK
    DUPLICATE r12, WIT_RIGHT_SEND + WIT_RIGHT_RECEIVE
    EXPECT WIT_STATUS_OK
    mov rsi, rdx
    WAIT_OBJECT rsi, 0
    EXPECT WIT_STATUS_DENIED
    CLOSE rsi
    EXPECT WIT_STATUS_OK
    ; The queue holds four messages; the fifth waits for room.
    mov edi, 4
fill_queue:
    SEND r12, 1, 0
    EXPECT WIT_STATUS_OK
    dec edi
    jne fill_queue
    SEND r12, 1, 0
    EXPECT WIT_STATUS_BUSY
    mov edi, 4
drain:
    RECEIVE r13, 256, 0
    EXPECT WIT_STATUS_OK
    cmp edx, 1
    jne failed
    dec edi
    jne drain
    RECEIVE r13, 256, 0
    EXPECT WIT_STATUS_TIMED_OUT
    ; The peer's close drops what it had not received; the survivor learns PEER_CLOSED both ways.
    SEND r12, 1, 0
    EXPECT WIT_STATUS_OK
    SEND r12, 1, 0
    EXPECT WIT_STATUS_OK
    CLOSE r13
    EXPECT WIT_STATUS_OK
    SEND r12, 1, 0
    EXPECT WIT_STATUS_PEER_CLOSED
    RECEIVE r12, 256, 0
    EXPECT WIT_STATUS_PEER_CLOSED
    SEND r13, 1, 0
    EXPECT WIT_STATUS_BAD_HANDLE
    CLOSE r12
    EXPECT WIT_STATUS_OK
    RECEIVE r12, 256, 0
    EXPECT WIT_STATUS_BAD_HANDLE
    jmp passed

wait_test:
    ; Another thread sends a message and then closes its endpoint: each wakes the parked wait.
    call channel_create
    EXPECT WIT_STATUS_OK
    mov r12, [rbx]
    mov r13, [rbx + 8]
    lea rcx, wait_sender
    call thread_create
    EXPECT WIT_STATUS_OK
    mov rsi, rdx
    WAIT_OBJECT r12, WIT_WAIT_INFINITE
    EXPECT WIT_STATUS_OK
    test rdx, rdx
    jne failed
    RECEIVE r12, 256, 0
    EXPECT WIT_STATUS_OK
    cmp edx, 1
    jne failed
    cmp BYTE PTR [rbx + 512], 7
    jne failed
    WAIT_OBJECT r12, WIT_WAIT_INFINITE
    EXPECT WIT_STATUS_OK
    RECEIVE r12, 256, 0
    EXPECT WIT_STATUS_PEER_CLOSED
    JOIN rsi
    CLOSE r12
    EXPECT WIT_STATUS_OK
    jmp passed

wait_sender:
    mov rbx, WIT_USER_DATA
    mov r13, [rbx + 8]
    mov BYTE PTR [rbx + 128], 7
    SEND r13, 1, 0
    EXPECT WIT_STATUS_OK
    CALL0 WIT_CALL_THREAD_YIELD ; the receiver runs, takes the message and parks again
    EXPECT WIT_STATUS_OK
    CLOSE r13
    EXPECT WIT_STATUS_OK
    jmp thread_passed

transfer_test:
    call channel_create
    EXPECT WIT_STATUS_OK
    mov r12, [rbx]
    mov r13, [rbx + 8]
    ; An event moves with a message: the sender's handle is gone, the receiver's works.
    CREATE_EVENT ALL_RIGHTS
    mov [rbx + 768], rdx
    mov rsi, rdx
    SEND r12, 1, 1
    EXPECT WIT_STATUS_OK
    SET_EVENT rsi
    EXPECT WIT_STATUS_BAD_HANDLE
    RECEIVE r13, 256, 1
    EXPECT WIT_STATUS_OK
    mov rax, 1
    bts rax, 32
    cmp rdx, rax
    jne failed
    mov rdi, [rbx + 800]
    cmp rdi, rsi
    je failed
    SET_EVENT rdi
    EXPECT WIT_STATUS_OK
    WAIT_OBJECT rdi, 0
    EXPECT WIT_STATUS_OK
    ; Attenuated before sending: the receiver cannot signal, but sees the signal of the full handle.
    DUPLICATE rdi, WIT_RIGHT_WAIT + WIT_RIGHT_TRANSFER
    EXPECT WIT_STATUS_OK
    mov [rbx + 768], rdx
    SEND r12, 0, 1
    EXPECT WIT_STATUS_OK
    RECEIVE r13, 256, 1
    EXPECT WIT_STATUS_OK
    mov rax, 1
    shl rax, 32
    cmp rdx, rax
    jne failed
    mov rsi, [rbx + 800]
    SET_EVENT rsi
    EXPECT WIT_STATUS_DENIED
    WAIT_OBJECT rsi, 0
    EXPECT WIT_STATUS_TIMED_OUT
    SET_EVENT rdi
    EXPECT WIT_STATUS_OK
    WAIT_OBJECT rsi, 0
    EXPECT WIT_STATUS_OK
    CLOSE rsi
    EXPECT WIT_STATUS_OK
    CLOSE rdi
    EXPECT WIT_STATUS_OK
    ; Without TRANSFER a handle stays where it is; a handle named twice is refused whole.
    CREATE_EVENT 0
    mov [rbx + 768], rdx
    mov rsi, rdx
    SEND r12, 1, 1
    EXPECT WIT_STATUS_DENIED
    SET_EVENT rsi
    EXPECT WIT_STATUS_OK
    CLOSE rsi
    EXPECT WIT_STATUS_OK
    CREATE_EVENT ALL_RIGHTS
    mov [rbx + 768], rdx
    mov [rbx + 776], rdx
    mov rsi, rdx
    SEND r12, 1, 2
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    SET_EVENT rsi
    EXPECT WIT_STATUS_OK
    CLOSE rsi
    EXPECT WIT_STATUS_OK
    ; An endpoint moves too: the received handle talks to the other end of its own channel.
    call channel_create
    EXPECT WIT_STATUS_OK
    mov rsi, [rbx + 8]
    mov [rbx + 768], rsi
    SEND r12, 0, 1
    EXPECT WIT_STATUS_OK
    SEND rsi, 1, 0
    EXPECT WIT_STATUS_BAD_HANDLE
    RECEIVE r13, 256, 1
    EXPECT WIT_STATUS_OK
    mov rdi, [rbx + 800]
    mov BYTE PTR [rbx + 128], 9
    mov rsi, [rbx]
    SEND rsi, 1, 0
    EXPECT WIT_STATUS_OK
    RECEIVE rdi, 256, 0
    EXPECT WIT_STATUS_OK
    cmp edx, 1
    jne failed
    cmp BYTE PTR [rbx + 512], 9
    jne failed
    CLOSE rsi
    EXPECT WIT_STATUS_OK
    RECEIVE rdi, 256, 0
    EXPECT WIT_STATUS_PEER_CLOSED
    CLOSE rdi
    EXPECT WIT_STATUS_OK
    ; A thread handle moves with its record: the receiver joins the thread.
    lea rcx, thread_passed
    call thread_create
    EXPECT WIT_STATUS_OK
    mov [rbx + 768], rdx
    mov rsi, rdx
    SEND r12, 0, 1
    EXPECT WIT_STATUS_OK
    mov rcx, rsi
    call thread_join
    EXPECT WIT_STATUS_BAD_HANDLE
    RECEIVE r13, 256, 1
    EXPECT WIT_STATUS_OK
    JOIN QWORD PTR [rbx + 800]
    CLOSE r12
    EXPECT WIT_STATUS_OK
    CLOSE r13
    EXPECT WIT_STATUS_OK
    jmp passed

limits_test:
    ; Four channels, then NO_MEMORY with the output untouched; the handle table fills with duplicates, a moved
    ; handle frees its slot, and a message whose handle would not fit stays queued until a slot is free.
    xor edi, edi
channels:
    call channel_create
    EXPECT WIT_STATUS_OK
    mov rax, [rbx]
    mov [rbx + 832 + rdi * 8], rax
    mov rax, [rbx + 8]
    mov [rbx + 840 + rdi * 8], rax
    add edi, 2
    cmp edi, 8
    jb channels
    mov rax, 0AAAAAAAAAAAAAAAAh
    mov [rbx], rax
    mov [rbx + 8], rax
    call channel_create
    EXPECT WIT_STATUS_NO_MEMORY
    mov rax, 0AAAAAAAAAAAAAAAAh
    cmp [rbx], rax
    jne failed
    cmp [rbx + 8], rax
    jne failed
    mov r12, [rbx + 832]
    mov r13, [rbx + 840]
    xor edi, edi
duplicates:
    DUPLICATE r12, 0
    cmp eax, WIT_STATUS_NO_MEMORY
    je duplicates_done
    EXPECT WIT_STATUS_OK
    mov [rbx + 1040 + rdi * 8], rdx
    inc edi
    cmp edi, 16
    jb duplicates
    jmp failed
duplicates_done:
    ; The duplicates that filled the table: the thread's identity, the eight endpoints and whatever else the kernel
    ; holds for the component precede them, so at least four fit.
    mov r14d, edi
    cmp edi, 4
    jb failed
    mov rax, [rbx + 1032 + r14 * 8] ; the last duplicate
    mov [rbx + 768], rax
    SEND r12, 0, 1
    EXPECT WIT_STATUS_OK
    RECEIVE r13, 256, 1
    EXPECT WIT_STATUS_OK
    mov rax, [rbx + 800]
    mov [rbx + 768], rax
    SEND r12, 0, 1
    EXPECT WIT_STATUS_OK
    DUPLICATE r12, 0
    EXPECT WIT_STATUS_OK
    mov rsi, rdx
    RECEIVE r13, 256, 1
    EXPECT WIT_STATUS_NO_MEMORY
    CLOSE rsi
    EXPECT WIT_STATUS_OK
    RECEIVE r13, 256, 1
    EXPECT WIT_STATUS_OK
    mov rax, [rbx + 800]
    mov [rbx + 1032 + r14 * 8], rax
    xor edi, edi
close_all:
    CLOSE QWORD PTR [rbx + 1040 + rdi * 8] ; the duplicates, then the 8 endpoints behind them
    EXPECT WIT_STATUS_OK
    inc edi
    cmp edi, r14d
    jb close_all
    jmp close_endpoints
close_endpoints:
    xor edi, edi
close_channels:
    CLOSE QWORD PTR [rbx + 832 + rdi * 8]
    EXPECT WIT_STATUS_OK
    inc edi
    cmp edi, 8
    jb close_channels
    call channel_create
    EXPECT WIT_STATUS_OK
    CLOSE QWORD PTR [rbx]
    EXPECT WIT_STATUS_OK
    CLOSE QWORD PTR [rbx + 8]
    EXPECT WIT_STATUS_OK
    jmp passed

drop_test:
    ; A message in a closed endpoint's queue is dropped with the event it carried: the event quota is whole again.
    call channel_create
    EXPECT WIT_STATUS_OK
    mov r12, [rbx]
    mov r13, [rbx + 8]
    CREATE_EVENT ALL_RIGHTS
    mov [rbx + 768], rdx
    SEND r12, 1, 1
    EXPECT WIT_STATUS_OK
    CLOSE r13
    EXPECT WIT_STATUS_OK
    xor edi, edi
events:
    CREATE_EVENT 0
    mov [rbx + 832 + rdi * 8], rdx
    inc edi
    cmp edi, 4
    jb events
    xor edx, edx
    call create_event
    EXPECT WIT_STATUS_NO_MEMORY
    xor edi, edi
close_events:
    CLOSE QWORD PTR [rbx + 832 + rdi * 8]
    EXPECT WIT_STATUS_OK
    inc edi
    cmp edi, 4
    jb close_events
    SEND r12, 1, 0
    EXPECT WIT_STATUS_PEER_CLOSED
    CLOSE r12
    EXPECT WIT_STATUS_OK
    jmp passed

; CHANNEL_CREATE into the data page -> rax.
channel_create:
    mov rcx, rbx
    xor edx, edx
    xor r8d, r8d
    CALL0 WIT_CALL_CHANNEL_CREATE
    ret

; The request at data page + 16: rcx endpoint, edx bytes | handles shl 16; r9 data, r10 handle buffer.
build_request:
    lea rax, [rbx + 16]
    mov DWORD PTR [rax], WIT_CHANNEL_MESSAGE_VERSION
    mov DWORD PTR [rax + 4], 40
    mov [rax + 8], r9
    mov [rax + 16], r10
    mov r8d, edx
    shr r8d, 16
    and edx, 0FFFFh
    mov [rax + 24], edx
    mov [rax + 28], r8d
    mov DWORD PTR [rax + 32], 0
    mov DWORD PTR [rax + 36], 0
    mov rdx, rax
    mov r8d, 40
    ret

; CHANNEL_SEND of the payload and the handles to move.
send_message:
    lea r9, [rbx + 128]
    lea r10, [rbx + 768]
    call build_request
raw_send_request:
    CALL0 WIT_CALL_CHANNEL_SEND
    ret

; CHANNEL_SEND of the request as the caller left it in rsi, with r8d as its size.
raw_send:
    mov rcx, r12
    mov rdx, rsi
    jmp raw_send_request

; CHANNEL_RECEIVE into the buffer and the received handles.
receive_message:
    lea r9, [rbx + 512]
    lea r10, [rbx + 800]
    call build_request
    CALL0 WIT_CALL_CHANNEL_RECEIVE
    ret

; EVENT_CREATE with edx rights -> rax, rdx the handle.
create_event:
    xor ecx, ecx
    xor r8d, r8d
    CALL0 WIT_CALL_EVENT_CREATE
    ret

set_event:
    CALL0 WIT_CALL_EVENT_SET
    ret

close_handle:
    CALL0 WIT_CALL_HANDLE_CLOSE
    ret

; HANDLE_DUPLICATE: rcx source, edx rights -> rax, rdx the new handle.
duplicate_handle:
    mov r8d, edx
    lea rdx, [rbx + 1200]
    CALL0 WIT_CALL_HANDLE_DUPLICATE
    mov rdx, [rbx + 1200]
    ret

; THREAD_CREATE with a request on the stack: rcx entry -> rax status, rdx handle.
thread_create:
    sub rsp, 56
    mov DWORD PTR [rsp], WIT_THREAD_CREATE_VERSION
    mov DWORD PTR [rsp + 4], 48
    mov [rsp + 8], rcx
    xor eax, eax
    mov [rsp + 16], rax
    mov [rsp + 24], rax
    mov [rsp + 32], rax
    mov [rsp + 40], rax
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

; OBJECT_WAIT on one handle: rcx handle, rdx deadline; rax status, rdx winner.
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

failed:
    mov [rbx + 1304], rax ; the status the failed check saw
    mov ecx, 241
    jmp exit_process
passed:
    mov ecx, WIT_TEST_EXIT_CODE
exit_process:
    CALL0 WIT_CALL_PROCESS_EXIT
    ud2
thread_passed:
    mov ecx, WIT_TEST_EXIT_CODE ; THREAD_EXIT(code, 0, 0): the other arguments must be zero
    xor edx, edx
    xor r8d, r8d
    CALL0 WIT_CALL_THREAD_EXIT
    ud2
wit_user_start ENDP
END
