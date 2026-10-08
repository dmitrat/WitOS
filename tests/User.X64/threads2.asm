option casemap:none
include user_abi.inc

; Thread form and transport fixture (RFC 0011 v3 sections 7.3 and 6.1, plan step K5.2a). Every call here travels
; through SYSCALL with the SysV registers: the number in RAX, the arguments in RDI, RSI and RDX, the status in RAX,
; the value in RDX; the instruction clobbers RCX and R11. The first thread checks that its argument arrived in both
; RCX (Microsoft) and RDI (SysV), then: THREAD_SET_TLS changes its FS base; a stack of the fixture's own is reserved
; and committed; THREAD_CREATE with the version 2 request starts a worker on it with a TLS base of the fixture's
; choosing; the worker checks its argument, stack and TLS, changes its TLS, and exits naming the reservation; the
; creator joins it and finds the reservation released. Broken requests are refused whole. The data page: the
; request at 16, the wait request at 128, the thread info at 256, the TLS blocks at 2048 and 2560, the reservation
; base at 3072, the status of a failed check at 1304 and the number of checks passed at 1312.

EXPECT MACRO value
    inc QWORD PTR [rbx + 1312]
    cmp eax, value
    jne failed
ENDM
CALL0 MACRO operation
    mov eax, operation
    syscall
ENDM
CLOSE MACRO handle
    mov rdi, handle
    CALL0 WIT_CALL_HANDLE_CLOSE
ENDM

.code
PUBLIC wit_user_start
wit_user_start PROC
    mov r15, rcx
    cmp rdi, rcx ; the startup block in both entry conventions
    jne failed
    mov rbx, WIT_USER_DATA
    cmp DWORD PTR [r15], WIT_ABI_VERSION
    jne failed
    ; QUERY over SYSCALL: the ABI version in the low half of the value.
    xor edi, edi
    xor esi, esi
    xor edx, edx
    CALL0 WIT_CALL_QUERY
    EXPECT WIT_STATUS_OK
    cmp edx, WIT_ABI_VERSION
    jne failed
    ; THREAD_SET_TLS: the FS base of this thread moves to a block of the data page.
    mov QWORD PTR [rbx + 2048], 1234h
    mov QWORD PTR [rbx + 2560], 5678h
    lea rdi, [rbx + 2048]
    xor esi, esi
    xor edx, edx
    CALL0 WIT_CALL_THREAD_SET_TLS
    EXPECT WIT_STATUS_OK
    cmp QWORD PTR fs:[0], 1234h
    jne failed
    lea rdi, [rbx + 2048]
    mov esi, 1
    xor edx, edx
    CALL0 WIT_CALL_THREAD_SET_TLS
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    mov rdi, 0FFFF800000000000h ; not a user address
    xor esi, esi
    xor edx, edx
    CALL0 WIT_CALL_THREAD_SET_TLS
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    ; A stack of our own: a reservation of 64 KiB, committed writable.
    mov edi, 65536
    mov esi, 4096
    xor edx, edx
    CALL0 WIT_CALL_MEMORY_RESERVE
    EXPECT WIT_STATUS_OK
    mov r12, rdx
    mov [rbx + 3072], r12
    mov rdi, r12
    mov esi, 65536
    mov edx, WIT_MEMORY_READ + WIT_MEMORY_WRITE
    CALL0 WIT_CALL_MEMORY_COMMIT
    EXPECT WIT_STATUS_OK
    ; Refusals of the version 2 request: a stack pointer outside any reservation, a foreign version, a flag, a TLS
    ; base outside user space, a stack pointer off alignment.
    lea r8, worker
    mov r9, rbx ; the data page is a fixed mapping, not a reservation
    lea r10, [rbx + 2048]
    mov r11d, 2
    call thread_create2
    EXPECT WIT_STATUS_BAD_ADDRESS
    lea r8, worker
    lea r9, [r12 + 65536]
    lea r10, [rbx + 2048]
    mov r11d, 3
    call thread_create2
    EXPECT WIT_STATUS_UNSUPPORTED
    lea r8, worker
    lea r9, [r12 + 65536]
    lea r10, [rbx + 2048]
    mov r11d, 2
    mov DWORD PTR [rbx + 1320], 2 ; an unknown flag
    call thread_create2
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    mov DWORD PTR [rbx + 1320], 0
    lea r8, worker
    lea r9, [r12 + 65536]
    mov r10, 0FFFF800000000000h
    mov r11d, 2
    call thread_create2
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    lea r8, worker
    lea r9, [r12 + 65536 - 8]
    lea r10, [rbx + 2048]
    mov r11d, 2
    call thread_create2
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    ; A version 1 thread (this one, on the kernel's stack) may not name a reservation at its exit.
    mov edi, 7
    mov rsi, rbx
    xor edx, edx
    CALL0 WIT_CALL_THREAD_EXIT
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    ; The worker on our stack with our TLS base; its argument is 77h.
    lea r8, worker
    lea r9, [r12 + 65536]
    lea r10, [rbx + 2048]
    mov r11d, 2
    call thread_create2
    EXPECT WIT_STATUS_OK
    mov r13, rdx
    mov rdi, r13
    call thread_join
    EXPECT WIT_STATUS_OK
    cmp edx, 42
    jne failed
    ; The worker's exit released the reservation.
    mov rdi, r12
    xor esi, esi
    xor edx, edx
    CALL0 WIT_CALL_MEMORY_RELEASE
    EXPECT WIT_STATUS_NOT_RESERVED
    mov edi, WIT_TEST_EXIT_CODE
    jmp exit_process

; The worker: argument in RDI and RCX, the stack from the reservation, FS at the block holding 1234h.
worker:
    mov rbx, WIT_USER_DATA
    cmp rdi, 77h
    jne worker_failed
    cmp rcx, 77h
    jne worker_failed
    mov rax, [rbx + 3072]
    add rax, 65536
    cmp rsp, rax ; the stack pointer is exactly the one requested
    jne worker_failed
    push rax ; the stack takes writes
    pop rax
    cmp QWORD PTR fs:[0], 1234h
    jne worker_failed
    lea rdi, [rbx + 2560]
    xor esi, esi
    xor edx, edx
    CALL0 WIT_CALL_THREAD_SET_TLS
    test eax, eax
    jne worker_failed
    cmp QWORD PTR fs:[0], 5678h
    jne worker_failed
    ; THREAD_EXIT naming something that is not a reservation returns instead of exiting.
    mov edi, 7
    mov rsi, rbx
    xor edx, edx
    CALL0 WIT_CALL_THREAD_EXIT
    cmp eax, WIT_STATUS_NOT_RESERVED
    jne worker_failed
    mov edi, 42
    mov rsi, [rbx + 3072] ; the kernel releases the stack's reservation once this thread no longer runs on it
    xor edx, edx
    CALL0 WIT_CALL_THREAD_EXIT
    ud2
worker_failed:
    mov edi, 9
    xor esi, esi
    xor edx, edx
    CALL0 WIT_CALL_THREAD_EXIT
    ud2

; THREAD_CREATE with the version 2 request at data page + 16: r8 entry, r9 stack pointer, r10 TLS base, r11d the
; version; the flags come from 1320. Returns rax, rdx the handle.
thread_create2:
    lea rax, [rbx + 16]
    mov [rax], r11d
    mov DWORD PTR [rax + 4], 48
    mov [rax + 8], r8
    mov QWORD PTR [rax + 16], 77h
    mov [rax + 24], r9
    mov [rax + 32], r10
    mov ecx, [rbx + 1320]
    mov [rax + 40], ecx
    mov DWORD PTR [rax + 44], 0
    lea rdi, [rbx + 16]
    mov esi, 48
    xor edx, edx
    CALL0 WIT_CALL_THREAD_CREATE
    ret

; Wait for the thread, read its exit code and close the handle: rdi handle -> rax status, rdx exit code.
thread_join:
    mov [rbx + 128 + 32], rdi
    lea rax, [rbx + 128]
    mov DWORD PTR [rax], WIT_WAIT_OBJECTS_VERSION
    mov DWORD PTR [rax + 4], 32
    lea r8, [rax + 32]
    mov [rax + 8], r8
    mov DWORD PTR [rax + 16], 1
    mov DWORD PTR [rax + 20], 0
    mov r8, WIT_WAIT_INFINITE
    mov [rax + 24], r8
    mov rdi, rax
    mov esi, 32
    xor edx, edx
    CALL0 WIT_CALL_OBJECT_WAIT
    test rax, rax
    jne thread_join_done
    mov DWORD PTR [rbx + 256], WIT_THREAD_INFO_VERSION
    mov DWORD PTR [rbx + 260], WIT_THREAD_INFO_SIZE
    mov rdi, [rbx + 128 + 32]
    lea rsi, [rbx + 256]
    mov edx, WIT_THREAD_INFO_SIZE
    CALL0 WIT_CALL_THREAD_QUERY
    test rax, rax
    jne thread_join_done
    mov rdi, [rbx + 128 + 32]
    CALL0 WIT_CALL_HANDLE_CLOSE
    mov rdx, [rbx + 256 + 72] ; ExitCode
thread_join_done:
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
