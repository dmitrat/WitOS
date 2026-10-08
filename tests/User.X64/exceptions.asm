option casemap:none
include user_abi.inc

; Exception fixture over the fault callback and the thread contexts of ABI-1 (RFC 0011 v3 sections 7.3 and 7.5, plan
; step K1.4): EXCEPTION_REGISTER, a read fault at address zero delivered to the callback with the interrupted
; context, EXCEPTION_QUERY and EXCEPTION_CONTINUE with a changed context, THREAD_ACTIVATE of the own thread through the
; same callback, the alternate stack of the thread (THREAD_STACK_ALTERNATE, S3.1: refusals, a fault with no room
; below the stack pointer delivered on it, BUSY while on it, cleared), CONTEXT_PROFILE and THREAD_CONTEXT_GET of the
; own thread. The record lives in the data page, the transfer 1024 bytes above it, the activation counter at 2048,
; the profile at 2112, the alternate stack request at 2176, the saved stack pointer at 2208 and the alternate base
; at 2216. Registers: r15 startup block, r14 test mode, r12 the marked register, r13 token, rbx vector, rsi
; address, rdi the record.

INFO_AREA EQU WIT_USER_DATA
TRANSFER EQU WIT_USER_DATA + 1024
COUNTER EQU WIT_USER_DATA + 2048
PROFILE EQU WIT_USER_DATA + 2112
ALT_REQUEST EQU WIT_USER_DATA + 2176
SAVED_SP EQU WIT_USER_DATA + 2208
ALT_BASE EQU WIT_USER_DATA + 2216
ALT_BYTES EQU 16384
CONTEXT_SIZE EQU WIT_THREAD_CONTEXT_SIZE_X64
INFO_SIZE EQU CONTEXT_SIZE + 48
TRANSFER_SIZE EQU CONTEXT_SIZE + 16

EXPECT MACRO value
    cmp rax, value
    jne failed
ENDM
CALL0 MACRO operation
    mov eax, operation
    int 80h
ENDM
; THREAD_STACK_ALTERNATE with the request in the data page.
ALT_CALL MACRO
    mov rcx, ALT_REQUEST
    mov edx, 24
    xor r8d, r8d
    CALL0 WIT_CALL_THREAD_STACK_ALTERNATE
ENDM

.code
PUBLIC wit_user_start
wit_user_start PROC
    mov r15, rcx
    cmp DWORD PTR [r15], WIT_ABI_VERSION
    jne failed
    mov r14, [r15 + WIT_TEST_MODE_OFFSET]
    ; Register the callback: a data address is rejected, then the callback is installed.
    mov rcx, WIT_USER_DATA
    mov edx, WIT_EXCEPTION_VERSION
    xor r8d, r8d
    CALL0 WIT_CALL_EXCEPTION_REGISTER
    EXPECT WIT_STATUS_BAD_ADDRESS
    lea rcx, callback
    mov edx, WIT_EXCEPTION_VERSION
    xor r8d, r8d
    CALL0 WIT_CALL_EXCEPTION_REGISTER
    EXPECT WIT_STATUS_OK
    mov r12, 1122334455667788h
    ; The fault: a read at address zero, delivered to the callback with this context.
    xor eax, eax
fault_site:
    mov rax, QWORD PTR [rax]
    jmp failed
landing:
    ; Reached through EXCEPTION_CONTINUE with Rip changed to this label and R12 to 5A5Ah.
    cmp r12, 5A5Ah
    jne failed
    ; The activation of the own thread: its callback runs through the fault callback before this call returns.
    mov rcx, -2 ; WIT_THREAD_SELF
    lea rdx, activation_target
    mov r8d, 77h
    CALL0 WIT_CALL_THREAD_ACTIVATE
    EXPECT WIT_STATUS_OK
    mov rax, COUNTER
    cmp QWORD PTR [rax], 1
    jne failed
    ; The alternate stack (S3.1): 16 KiB committed at the start of a 64 KiB reservation.
    mov ecx, 65536
    mov edx, 4096
    xor r8d, r8d
    CALL0 WIT_CALL_MEMORY_RESERVE
    EXPECT WIT_STATUS_OK
    mov rax, ALT_BASE
    mov [rax], rdx
    mov rcx, rdx
    mov edx, ALT_BYTES
    mov r8d, WIT_MEMORY_READ + WIT_MEMORY_WRITE
    CALL0 WIT_CALL_MEMORY_COMMIT
    EXPECT WIT_STATUS_OK
    ; THREAD_STACK_ALTERNATE: refused whole before anything changes, then installed.
    mov rax, ALT_REQUEST
    mov DWORD PTR [rax], 2 ; a foreign version
    mov DWORD PTR [rax + 4], 24
    mov rcx, ALT_BASE
    mov rcx, [rcx]
    mov [rax + 8], rcx ; Base
    mov QWORD PTR [rax + 16], ALT_BYTES ; Bytes
    ALT_CALL
    EXPECT WIT_STATUS_UNSUPPORTED
    mov rax, ALT_REQUEST
    mov DWORD PTR [rax], WIT_THREAD_ALTERNATE_STACK_VERSION
    mov rcx, ALT_REQUEST ; a wrong size
    mov edx, 23
    xor r8d, r8d
    CALL0 WIT_CALL_THREAD_STACK_ALTERNATE
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    mov rax, ALT_REQUEST
    add QWORD PTR [rax + 8], 8 ; an unaligned base
    ALT_CALL
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    mov rax, ALT_REQUEST
    sub QWORD PTR [rax + 8], 8
    mov QWORD PTR [rax + 16], 4080 ; too small
    ALT_CALL
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    mov rax, ALT_REQUEST
    mov QWORD PTR [rax + 16], ALT_BYTES
    mov rcx, WIT_USER_STACK_BOTTOM ; the thread's own stack
    mov [rax + 8], rcx
    ALT_CALL
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    mov rax, ALT_REQUEST
    mov rcx, 0FFFF800000000000h ; not a user address
    mov [rax + 8], rcx
    ALT_CALL
    EXPECT WIT_STATUS_BAD_ADDRESS
    mov rax, ALT_REQUEST
    mov rcx, ALT_BASE
    mov rcx, [rcx]
    add rcx, ALT_BYTES ; reserved, not committed
    mov [rax + 8], rcx
    ALT_CALL
    EXPECT WIT_STATUS_BAD_ADDRESS
    mov rax, ALT_REQUEST
    mov rcx, ALT_BASE
    mov rcx, [rcx]
    mov [rax + 8], rcx
    ALT_CALL
    EXPECT WIT_STATUS_OK
    ; A fault with no room below the stack pointer: delivered on the alternate stack, continued at the second
    ; landing with the saved stack pointer.
    mov rax, SAVED_SP
    mov [rax], rsp
    mov rsp, WIT_USER_STACK_BOTTOM + 16
    xor eax, eax
fault_site2:
    mov rax, QWORD PTR [rax]
    jmp failed
landing2:
    mov rax, SAVED_SP
    cmp rsp, [rax]
    jne failed
    ; On the alternate stack the thread cannot change it; off it, the stack is cleared.
    mov rax, ALT_BASE
    mov rax, [rax]
    lea rsp, [rax + ALT_BYTES - 64]
    ALT_CALL
    EXPECT WIT_STATUS_BUSY
    mov rax, SAVED_SP
    mov rsp, [rax]
    mov rax, ALT_REQUEST
    mov QWORD PTR [rax + 8], 0
    mov QWORD PTR [rax + 16], 0
    ALT_CALL
    EXPECT WIT_STATUS_OK
    ; CONTEXT_PROFILE: the x64 block with the legacy x87 and SSE state.
    mov rcx, PROFILE
    mov edx, 32
    mov r8d, WIT_CPU_CONTEXT_VERSION
    CALL0 WIT_CALL_CONTEXT_PROFILE
    EXPECT WIT_STATUS_OK
    mov rax, PROFILE
    cmp DWORD PTR [rax], WIT_CPU_CONTEXT_VERSION
    jne failed
    cmp DWORD PTR [rax + 4], 32
    jne failed
    cmp QWORD PTR [rax + 8], 3 ; WIT_CPU_CONTEXT_LEGACY: x87 and SSE
    jne failed
    ; The own context: running, the x64 block, the fixed stack bounds; the running thread's context cannot be set.
    mov rcx, -2
    mov rdx, INFO_AREA
    mov r8d, CONTEXT_SIZE
    CALL0 WIT_CALL_THREAD_CONTEXT_GET
    EXPECT WIT_STATUS_OK
    mov rax, INFO_AREA
    cmp DWORD PTR [rax + 32], WIT_THREAD_CONTEXT_RUNNING
    jne failed
    cmp DWORD PTR [rax + 36], WIT_THREAD_CONTEXT_FXSAVE64
    jne failed
    mov rcx, WIT_USER_STACK_BOTTOM
    cmp [rax + 16], rcx
    jne failed
    mov rcx, WIT_USER_STACK_TOP
    cmp [rax + 24], rcx
    jne failed
    mov rcx, -2
    mov rdx, INFO_AREA
    mov r8d, CONTEXT_SIZE
    CALL0 WIT_CALL_THREAD_CONTEXT_SET
    EXPECT WIT_STATUS_BUSY
    mov ecx, WIT_TEST_EXIT_CODE
    jmp process_exit

; The fault callback: rcx token, rdx vector, r8 address, on this thread's stack below the interrupted frame.
callback:
    mov r13, rcx
    mov rbx, rdx
    mov rsi, r8
    ; A foreign token is unknown and the record is read with its exact size only.
    lea rcx, [r13 + 1]
    mov rdx, INFO_AREA
    mov r8d, INFO_SIZE
    CALL0 WIT_CALL_EXCEPTION_QUERY
    EXPECT WIT_STATUS_BAD_HANDLE
    mov rcx, r13
    mov rdx, INFO_AREA
    mov r8d, INFO_SIZE - 1
    CALL0 WIT_CALL_EXCEPTION_QUERY
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    mov rcx, r13
    mov rdx, INFO_AREA
    mov r8d, INFO_SIZE
    CALL0 WIT_CALL_EXCEPTION_QUERY
    EXPECT WIT_STATUS_OK
    mov rdi, INFO_AREA
    cmp DWORD PTR [rdi], WIT_EXCEPTION_VERSION
    jne failed
    cmp DWORD PTR [rdi + 4], INFO_SIZE
    jne failed
    cmp [rdi + 8], r13 ; Token
    jne failed
    cmp [rdi + 16], rbx ; Vector
    jne failed
    cmp [rdi + 32], rsi ; Address
    jne failed
    cmp DWORD PTR [rdi + 48 + 32], WIT_THREAD_CONTEXT_RUNNING ; Context.State
    jne failed
    cmp DWORD PTR [rdi + 48 + 36], WIT_THREAD_CONTEXT_FXSAVE64 + WIT_THREAD_CONTEXT_EXCEPTION_ACTIVE ; Context.Flags
    jne failed
    cmp rbx, -2 ; WIT_EXCEPTION_ACTIVATION_VECTOR
    je activation
    ; The fault: vector 14 at address zero with error 4 (a user read of a missing page), the interrupted RIP at the
    ; faulting instruction and R12 as it was.
    cmp rbx, 14
    jne failed
    cmp QWORD PTR [rdi + 24], 4
    jne failed
    test rsi, rsi
    jne failed
    lea rax, fault_site2
    cmp [rdi + 48 + 40], rax ; the second fault, from the alternate stack
    je alt_fault
    lea rax, fault_site
    cmp [rdi + 48 + 40], rax ; Context.Rip
    jne failed
    mov rax, 1122334455667788h
    cmp [rdi + 48 + 168], rax ; Context.R12
    jne failed
    cmp r14, WIT_EXCEPTION_TEST_REJECT
    jne continue_changed
    mov rcx, r13
    xor edx, edx
    xor r8d, r8d
    CALL0 WIT_CALL_EXCEPTION_REJECT
    jmp failed
continue_changed:
    ; Continue at the landing with R12 changed; a transfer of the wrong size, a foreign token and a context with
    ; privileged flags are refused first.
    call build_transfer
    mov rax, TRANSFER
    lea rcx, landing
    mov [rax + 16 + 40], rcx ; Context.Rip
    mov QWORD PTR [rax + 16 + 168], 5A5Ah ; Context.R12
    mov rcx, r13
    mov rdx, TRANSFER
    mov r8d, TRANSFER_SIZE - 1
    CALL0 WIT_CALL_EXCEPTION_CONTINUE
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    lea rcx, [r13 + 1]
    mov rdx, TRANSFER
    mov r8d, TRANSFER_SIZE
    CALL0 WIT_CALL_EXCEPTION_CONTINUE
    EXPECT WIT_STATUS_BAD_HANDLE
    mov rax, TRANSFER
    mov rcx, [rax + 16 + 56] ; Context.Rflags
    or QWORD PTR [rax + 16 + 56], 3000h ; IOPL
    push rcx
    mov rcx, r13
    mov rdx, TRANSFER
    mov r8d, TRANSFER_SIZE
    CALL0 WIT_CALL_EXCEPTION_CONTINUE
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    pop rcx
    mov rax, TRANSFER
    mov [rax + 16 + 56], rcx
    jmp continue_transfer
alt_fault:
    ; The second fault had no room below its stack pointer: the callback runs on the alternate stack, the record
    ; keeps the interrupted stack pointer, and the context continues at the second landing with the saved one.
    mov rax, ALT_BASE
    mov rax, [rax]
    cmp rsp, rax
    jb failed
    add rax, ALT_BYTES
    cmp rsp, rax
    jae failed
    mov rax, WIT_USER_STACK_BOTTOM + 16
    cmp [rdi + 48 + 48], rax ; Context.Rsp
    jne failed
    call build_transfer
    mov rax, TRANSFER
    lea rcx, landing2
    mov [rax + 16 + 40], rcx ; Context.Rip
    mov rcx, SAVED_SP
    mov rcx, [rcx]
    mov [rax + 16 + 48], rcx ; Context.Rsp
    jmp continue_transfer
activation:
    ; The activation record: Address is the callback and Error its argument; the callback runs here and the context
    ; then continues as it was.
    lea rax, activation_target
    cmp rsi, rax
    jne failed
    cmp QWORD PTR [rdi + 24], 77h
    jne failed
    mov rcx, [rdi + 24]
    sub rsp, 32
    call QWORD PTR [rdi + 32]
    add rsp, 32
    call build_transfer
continue_transfer:
    mov rcx, r13
    mov rdx, TRANSFER
    mov r8d, TRANSFER_SIZE
    CALL0 WIT_CALL_EXCEPTION_CONTINUE
    jmp failed

; The transfer: version, size, the current token and a copy of the record's context.
build_transfer:
    mov rax, TRANSFER
    mov DWORD PTR [rax], WIT_EXCEPTION_TRANSFER_VERSION
    mov DWORD PTR [rax + 4], TRANSFER_SIZE
    mov [rax + 8], r13
    lea rdi, [rax + 16]
    mov rsi, INFO_AREA + 48
    mov ecx, CONTEXT_SIZE
    cld
    rep movsb
    ret

activation_target:
    ; rcx: the argument 77h.
    cmp rcx, 77h
    jne failed
    mov rax, COUNTER
    inc QWORD PTR [rax]
    ret

failed:
    mov ecx, 241
process_exit:
    CALL0 WIT_CALL_PROCESS_EXIT
    ud2
wit_user_start ENDP
END
