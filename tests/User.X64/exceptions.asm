option casemap:none
include user_abi.inc

; Exception fixture over the fault callback and the thread contexts of ABI-1 (RFC 0011 v3 sections 7.3 and 7.5, plan
; step K1.4): EXCEPTION_REGISTER, a read fault at address zero delivered to the callback with the interrupted
; context, EXCEPTION_QUERY and EXCEPTION_CONTINUE with a changed context, THREAD_ACTIVATE of the own thread through the
; same callback, CONTEXT_PROFILE and THREAD_CONTEXT_GET of the own thread. The record lives in the data page, the
; transfer 1024 bytes above it, the activation counter at 2048 and the profile at 2112. Registers: r15 startup block,
; r14 test mode, r12 the marked register, r13 token, rbx vector, rsi address, rdi the record.

INFO_AREA EQU WIT_USER_DATA
TRANSFER EQU WIT_USER_DATA + 1024
COUNTER EQU WIT_USER_DATA + 2048
PROFILE EQU WIT_USER_DATA + 2112
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
