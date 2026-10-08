option casemap:none
include user_abi.inc

; Process fixture (RFC 0011 v3 section 7.8, plan step K5.2c), over SYSCALL with the SysV registers (the number in RAX,
; the arguments in RDI, RSI and RDX, the status in RAX, the value in RDX). A channel is created and one end goes to a
; new process through PROCESS_CREATE; the child's program is written into a memory object through a writable view,
; published through an executable view of the fixture's own, and mapped executable into the child at a fixed code
; address beside a stack object; THREAD_CREATE version 3 starts the child's first thread with the child-local endpoint
; handle as its argument; the child sends eight bytes over it and exits with 42. The fixture waits for the process,
; queries it, receives the message, then creates and kills a second child. Refusals are checked whole. The data page:
; the process request at 16, the thread request at 64, the map request at 128, the wait request at 192 with its
; handle at 224, the message at 256 with its data at 304, the process info at 352, the child-local handle at 400, a
; duplicate's output at 408, channel handles at 416 and 432, the stack object at 448, the thread handle at 456, the
; status of a failed check at 1304 and the number of checks passed at 1312.

FIXED_CODE EQU WIT_USER_CODE_BASE + 10000h
FIXED_DATA EQU WIT_USER_MEMORY_BASE + 100000h
READ_WRITE EQU WIT_MEMORY_READ + WIT_MEMORY_WRITE
READ_EXECUTE EQU WIT_MEMORY_READ + WIT_MEMORY_EXECUTE
STACK_BYTES EQU 16384

EXPECT MACRO value
    inc QWORD PTR [rbx + 1312]
    cmp eax, value
    jne failed
ENDM
CALL0 MACRO operation
    mov eax, operation
    syscall
ENDM
; MEMORY_OBJECT_MAP: handle, offset, bytes, address (0: chosen), protection, target -> rax, rdx address.
MAPT MACRO handle, offset, bytes, address, protection, target
    mov rdi, handle
    mov rsi, offset
    mov rdx, bytes
    mov r8, address
    mov r9d, protection
    mov r10, target
    call object_map
ENDM
CLOSE MACRO handle
    mov rdi, handle
    CALL0 WIT_CALL_HANDLE_CLOSE
ENDM
RELEASE MACRO base
    mov rdi, base
    xor esi, esi
    xor edx, edx
    CALL0 WIT_CALL_MEMORY_RELEASE
ENDM

.code
PUBLIC wit_user_start
wit_user_start PROC
    mov r15, rcx
    mov rbx, WIT_USER_DATA
    cmp DWORD PTR [r15], WIT_ABI_VERSION
    jne failed
    ; QUERY reports the process family.
    xor edi, edi
    xor esi, esi
    xor edx, edx
    CALL0 WIT_CALL_QUERY
    EXPECT WIT_STATUS_OK
    shr rdx, 32
    test edx, WIT_ABI_FEATURE_PROCESSES
    jz failed
    ; A channel: A stays here, B goes to the child.
    lea rdi, [rbx + 416]
    xor esi, esi
    xor edx, edx
    CALL0 WIT_CALL_CHANNEL_CREATE
    EXPECT WIT_STATUS_OK
    ; The code object; it is no endpoint, so PROCESS_CREATE refuses it.
    mov edi, 4096
    xor esi, esi
    xor edx, edx
    CALL0 WIT_CALL_MEMORY_OBJECT_CREATE
    EXPECT WIT_STATUS_OK
    mov r12, rdx
    mov rdi, r12
    call process_create
    EXPECT WIT_STATUS_WRONG_TYPE
    ; The child, with B; B is gone from this table.
    mov rdi, [rbx + 424]
    call process_create
    EXPECT WIT_STATUS_OK
    mov r13, rdx
    CLOSE QWORD PTR [rbx + 424]
    EXPECT WIT_STATUS_BAD_HANDLE
    ; Live, no thread yet.
    mov rdi, r13
    call process_query
    EXPECT WIT_STATUS_OK
    cmp DWORD PTR [rbx + 352 + 8], WIT_PROCESS_STATE_LIVE
    jne failed
    cmp DWORD PTR [rbx + 352 + 12], 0
    jne failed
    ; The child's program: written through a writable view, published through an executable view of our own.
    MAPT r12, 0, 4096, 0, READ_WRITE, -3
    EXPECT WIT_STATUS_OK
    mov r14, rdx
    lea rsi, child_begin
    mov rdi, r14
    mov ecx, child_end - child_begin
    rep movsb
    MAPT r12, 0, 4096, FIXED_CODE, READ_EXECUTE, -3
    EXPECT WIT_STATUS_OK
    mov rdi, FIXED_CODE
    mov esi, 4096
    xor edx, edx
    CALL0 WIT_CALL_CODE_PUBLISH
    EXPECT WIT_STATUS_OK
    ; A handle without MANAGE may not map into the child; the full handle maps the code at the same fixed address.
    mov rdi, r13
    mov esi, WIT_RIGHT_WAIT + WIT_RIGHT_QUERY
    call duplicate
    EXPECT WIT_STATUS_OK
    mov [rbx + 408], rdx
    MAPT r12, 0, 4096, FIXED_CODE, READ_EXECUTE, [rbx + 408]
    EXPECT WIT_STATUS_DENIED
    MAPT r12, 0, 4096, FIXED_CODE, READ_EXECUTE, r13
    EXPECT WIT_STATUS_OK
    mov rax, FIXED_CODE
    cmp rdx, rax
    jne failed
    ; The stack object, mapped writable into the child.
    mov edi, STACK_BYTES
    xor esi, esi
    xor edx, edx
    CALL0 WIT_CALL_MEMORY_OBJECT_CREATE
    EXPECT WIT_STATUS_OK
    mov [rbx + 448], rdx
    MAPT [rbx + 448], 0, STACK_BYTES, FIXED_DATA, READ_WRITE, r13
    EXPECT WIT_STATUS_OK
    ; The first thread: a stack pointer outside the child's reservations is refused; the stack's top starts it.
    mov r8, FIXED_CODE
    mov r9, FIXED_DATA + 2 * STACK_BYTES
    mov r10, r13
    mov r11, [rbx + 400]
    call thread_create3
    EXPECT WIT_STATUS_BAD_ADDRESS
    mov r8, FIXED_CODE
    mov r9, FIXED_DATA + STACK_BYTES
    mov r10, r13
    mov r11, [rbx + 400]
    call thread_create3
    EXPECT WIT_STATUS_OK
    mov [rbx + 456], rdx
    ; The process ends with the child's exit: its handle is ready, its state exited with 42, its thread gone.
    mov rdi, r13
    call object_wait
    EXPECT WIT_STATUS_OK
    mov rdi, r13
    call process_query
    EXPECT WIT_STATUS_OK
    cmp DWORD PTR [rbx + 352 + 8], WIT_PROCESS_STATE_EXITED
    jne failed
    cmp DWORD PTR [rbx + 352 + 12], 0
    jne failed
    cmp QWORD PTR [rbx + 352 + 16], 42
    jne failed
    mov rdi, [rbx + 456]
    call object_wait
    EXPECT WIT_STATUS_OK
    ; The child's message arrived on A: eight bytes, no handle.
    mov rdi, [rbx + 416]
    call receive
    EXPECT WIT_STATUS_OK
    mov rax, 8
    cmp rdx, rax
    jne failed
    mov rax, 1122334455667788h
    cmp [rbx + 304], rax
    jne failed
    ; A thread into the ended process is refused.
    mov r8, FIXED_CODE
    mov r9, FIXED_DATA + STACK_BYTES
    mov r10, r13
    mov r11, [rbx + 400]
    call thread_create3
    EXPECT WIT_STATUS_CLOSED
    ; A second child, killed before it has a thread: exited with the code given, its handle ready.
    lea rdi, [rbx + 432]
    xor esi, esi
    xor edx, edx
    CALL0 WIT_CALL_CHANNEL_CREATE
    EXPECT WIT_STATUS_OK
    mov rdi, [rbx + 440]
    call process_create
    EXPECT WIT_STATUS_OK
    mov r14, rdx ; the second process; the writable view's address stays at data page + 464
    mov rdi, r14
    mov esi, 7
    xor edx, edx
    CALL0 WIT_CALL_PROCESS_KILL
    EXPECT WIT_STATUS_OK
    mov rdi, r14
    call process_query
    EXPECT WIT_STATUS_OK
    cmp DWORD PTR [rbx + 352 + 8], WIT_PROCESS_STATE_EXITED
    jne failed
    cmp QWORD PTR [rbx + 352 + 16], 7
    jne failed
    mov rdi, r14
    call object_wait
    EXPECT WIT_STATUS_OK
    mov rdi, r14
    mov esi, 7
    xor edx, edx
    CALL0 WIT_CALL_PROCESS_KILL
    EXPECT WIT_STATUS_CLOSED
    ; Everything goes: the process handles, the thread handle, the channels, the views and the objects.
    CLOSE r14
    EXPECT WIT_STATUS_OK
    CLOSE QWORD PTR [rbx + 432]
    EXPECT WIT_STATUS_OK
    CLOSE r13
    EXPECT WIT_STATUS_OK
    CLOSE QWORD PTR [rbx + 408]
    EXPECT WIT_STATUS_OK
    CLOSE QWORD PTR [rbx + 456]
    EXPECT WIT_STATUS_OK
    CLOSE QWORD PTR [rbx + 416]
    EXPECT WIT_STATUS_OK
    RELEASE FIXED_CODE
    EXPECT WIT_STATUS_OK
    RELEASE QWORD PTR [rbx + 464] ; the writable view, at the address the kernel chose
    EXPECT WIT_STATUS_OK
    CLOSE r12
    EXPECT WIT_STATUS_OK
    CLOSE QWORD PTR [rbx + 448]
    EXPECT WIT_STATUS_OK
    mov edi, WIT_TEST_EXIT_CODE
    jmp exit_process

; The child's program, copied into its code object: position-independent, SysV SYSCALL convention, the child-local
; endpoint handle in RDI. It sends eight bytes over the endpoint from its stack and exits with 42 (43 on a failure).
child_begin:
    mov r12, rdi
    sub rsp, 128
    mov DWORD PTR [rsp], WIT_CHANNEL_MESSAGE_VERSION
    mov DWORD PTR [rsp + 4], 40
    lea rax, [rsp + 64]
    mov [rsp + 8], rax
    mov QWORD PTR [rsp + 16], 0
    mov DWORD PTR [rsp + 24], 8
    mov DWORD PTR [rsp + 28], 0
    mov QWORD PTR [rsp + 32], 0
    mov rax, 1122334455667788h
    mov [rsp + 64], rax
    mov rdi, r12
    mov rsi, rsp
    mov edx, 40
    mov eax, WIT_CALL_CHANNEL_SEND
    syscall
    test eax, eax
    jne child_failed
    mov edi, 42
    xor esi, esi
    xor edx, edx
    mov eax, WIT_CALL_PROCESS_EXIT
    syscall
    ud2
child_failed:
    mov edi, 43
    xor esi, esi
    xor edx, edx
    mov eax, WIT_CALL_PROCESS_EXIT
    syscall
    ud2
child_end:

; PROCESS_CREATE: rdi endpoint handle -> rax, rdx the process handle; the child-local endpoint handle at 400.
process_create:
    lea rax, [rbx + 16]
    mov DWORD PTR [rax], WIT_PROCESS_CREATE_VERSION
    mov DWORD PTR [rax + 4], 32
    mov [rax + 8], rdi
    mov QWORD PTR [rax + 16], 0
    mov QWORD PTR [rax + 24], 0
    mov rdi, rax
    mov esi, 32
    lea rdx, [rbx + 400]
    CALL0 WIT_CALL_PROCESS_CREATE
    ret

; PROCESS_QUERY: rdi process handle -> rax; the info at 352.
process_query:
    mov DWORD PTR [rbx + 352], WIT_PROCESS_INFO_VERSION
    mov DWORD PTR [rbx + 356], 40
    lea rsi, [rbx + 352]
    mov edx, 40
    CALL0 WIT_CALL_PROCESS_QUERY
    ret

; THREAD_CREATE version 3 at data page + 64: r8 entry, r9 stack pointer, r10 process, r11 argument -> rax, rdx handle.
thread_create3:
    lea rax, [rbx + 64]
    mov DWORD PTR [rax], WIT_THREAD_CREATE_VERSION_3
    mov DWORD PTR [rax + 4], 56
    mov [rax + 8], r8
    mov [rax + 16], r11
    mov [rax + 24], r9
    mov QWORD PTR [rax + 32], 0
    mov [rax + 40], r10
    mov QWORD PTR [rax + 48], 0
    mov rdi, rax
    mov esi, 56
    xor edx, edx
    CALL0 WIT_CALL_THREAD_CREATE
    ret

; The map request at data page + 128: rdi object, rsi offset, rdx bytes, r8 address, r9d protection, r10 target. A
; chosen address (address 0) is also kept at 464 for the release of the writable view.
object_map:
    lea rax, [rbx + 128]
    mov DWORD PTR [rax], WIT_MEMORY_MAP_VERSION
    mov DWORD PTR [rax + 4], 56
    mov [rax + 8], rdi
    mov [rax + 16], rsi
    mov [rax + 24], rdx
    mov [rax + 32], r8
    mov [rax + 40], r9d
    mov DWORD PTR [rax + 44], 0
    mov [rax + 48], r10
    mov rdi, rax
    mov esi, 56
    xor edx, edx
    CALL0 WIT_CALL_MEMORY_OBJECT_MAP
    test eax, eax
    jne object_map_done
    cmp QWORD PTR [rbx + 128 + 32], 0
    jne object_map_done
    mov [rbx + 464], rdx
object_map_done:
    ret

; OBJECT_WAIT on one handle without a deadline: rdi handle -> rax, rdx the winner.
object_wait:
    mov [rbx + 224], rdi
    lea rax, [rbx + 192]
    mov DWORD PTR [rax], WIT_WAIT_OBJECTS_VERSION
    mov DWORD PTR [rax + 4], 32
    lea r8, [rbx + 224]
    mov [rax + 8], r8
    mov DWORD PTR [rax + 16], 1
    mov DWORD PTR [rax + 20], 0
    mov r8, WIT_WAIT_INFINITE
    mov [rax + 24], r8
    mov rdi, rax
    mov esi, 32
    xor edx, edx
    CALL0 WIT_CALL_OBJECT_WAIT
    ret

; CHANNEL_RECEIVE of up to 16 bytes and no handle into data page + 304: rdi endpoint -> rax, rdx bytes and handles.
receive:
    lea rax, [rbx + 256]
    mov DWORD PTR [rax], WIT_CHANNEL_MESSAGE_VERSION
    mov DWORD PTR [rax + 4], 40
    lea r8, [rbx + 304]
    mov [rax + 8], r8
    mov QWORD PTR [rax + 16], 0
    mov DWORD PTR [rax + 24], 16
    mov DWORD PTR [rax + 28], 0
    mov QWORD PTR [rax + 32], 0
    mov rsi, rax
    mov edx, 40
    CALL0 WIT_CALL_CHANNEL_RECEIVE
    ret

; HANDLE_DUPLICATE: rdi source, esi rights -> rax, rdx the new handle.
duplicate:
    mov edx, esi
    lea rsi, [rbx + 408]
    CALL0 WIT_CALL_HANDLE_DUPLICATE
    mov rdx, [rbx + 408]
    ret

failed:
    mov [rbx + 1304], rax ; the status the failed check saw
    mov edi, 241
exit_process:
    xor esi, esi
    xor edx, edx
    CALL0 WIT_CALL_PROCESS_EXIT
    ud2
wit_user_start ENDP
END
