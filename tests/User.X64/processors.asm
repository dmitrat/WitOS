option casemap:none
include user_abi.inc

; Processor fixture (RFC 0011 v3 section 7.9, plan step K7.1), over SYSCALL with the SysV registers: PROCESSOR_QUERY
; in the 4-byte form of the frozen line and in the record form, the refusals of a wrong size and a foreign version;
; THREAD_AFFINITY of the current thread: the default mask, a set within the table, an empty mask and a mask beyond the
; table refused, and a duplicate without the right refused the set. The data page: the 4-byte form at 16, the record
; form at 64 (280 bytes), the mask at 400, a duplicate's output at 408, the status of a failed check at 1304 and the
; number of checks passed at 1312.

EXPECT MACRO value
    inc QWORD PTR [rbx + 1312]
    cmp eax, value
    jne failed
ENDM
CALL0 MACRO operation
    mov eax, operation
    syscall
ENDM
; THREAD_AFFINITY: handle, flags (the mask at data page + 400) -> rax.
AFFINITY MACRO handle, flags
    mov rdi, handle
    lea rsi, [rbx + 400]
    mov edx, flags
    CALL0 WIT_CALL_THREAD_AFFINITY
ENDM

.code
PUBLIC wit_user_start
wit_user_start PROC
    mov r15, rcx
    mov rbx, WIT_USER_DATA
    cmp DWORD PTR [r15], WIT_ABI_VERSION
    jne failed
    ; The 4-byte form: the current processor is group 0, number 0.
    mov DWORD PTR [rbx + 16], 0FFFFFFFFh
    lea rdi, [rbx + 16]
    mov esi, 4
    xor edx, edx
    CALL0 WIT_CALL_PROCESSOR_QUERY
    EXPECT WIT_STATUS_OK
    cmp edx, 4
    jne failed
    cmp DWORD PTR [rbx + 16], 0
    jne failed
    ; The record form: version 1, 280 bytes; the boot processor first, online, the one online.
    mov DWORD PTR [rbx + 64], WIT_PROCESSOR_INFO_VERSION
    mov DWORD PTR [rbx + 68], WIT_PROCESSOR_INFO_SIZE
    lea rdi, [rbx + 64]
    mov esi, WIT_PROCESSOR_INFO_SIZE
    xor edx, edx
    CALL0 WIT_CALL_PROCESSOR_QUERY
    EXPECT WIT_STATUS_OK
    cmp edx, WIT_PROCESSOR_INFO_SIZE
    jne failed
    cmp DWORD PTR [rbx + 64], WIT_PROCESSOR_INFO_VERSION
    jne failed
    cmp DWORD PTR [rbx + 72], 0 ; Current
    jne failed
    cmp DWORD PTR [rbx + 76], 1 ; Count
    jb failed
    cmp DWORD PTR [rbx + 80], 1 ; Online
    jne failed
    cmp DWORD PTR [rbx + 88 + 8], WIT_PROCESSOR_ONLINE + WIT_PROCESSOR_BOOT ; the first record's flags
    jne failed
    cmp WORD PTR [rbx + 88 + 14], 0 ; its number
    jne failed
    ; A wrong size and a foreign version are refused.
    lea rdi, [rbx + 64]
    mov esi, 100
    xor edx, edx
    CALL0 WIT_CALL_PROCESSOR_QUERY
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    mov DWORD PTR [rbx + 64], 2
    lea rdi, [rbx + 64]
    mov esi, WIT_PROCESSOR_INFO_SIZE
    xor edx, edx
    CALL0 WIT_CALL_PROCESSOR_QUERY
    EXPECT WIT_STATUS_UNSUPPORTED
    ; Affinity: the default is the boot processor; a set of the same is accepted; an empty mask and a mask beyond
    ; the table are refused; a foreign flag is refused.
    mov rdi, -2 ; WIT_THREAD_SELF
    AFFINITY rdi, WIT_THREAD_AFFINITY_GET
    EXPECT WIT_STATUS_OK
    cmp QWORD PTR [rbx + 400], 1
    jne failed
    mov rdi, -2 ; WIT_THREAD_SELF
    AFFINITY rdi, WIT_THREAD_AFFINITY_SET
    EXPECT WIT_STATUS_OK
    mov QWORD PTR [rbx + 400], 0
    mov rdi, -2 ; WIT_THREAD_SELF
    AFFINITY rdi, WIT_THREAD_AFFINITY_SET
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    mov rax, 8000000000000000h
    mov [rbx + 400], rax
    mov rdi, -2 ; WIT_THREAD_SELF
    AFFINITY rdi, WIT_THREAD_AFFINITY_SET
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    mov QWORD PTR [rbx + 400], 1
    mov rdi, -2 ; WIT_THREAD_SELF
    AFFINITY rdi, 2
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    ; A thread handle without the AFFINITY right reads the mask but may not set it.
    mov rdi, -2 ; WIT_THREAD_SELF
    lea rsi, [rbx + 408]
    mov edx, WIT_RIGHT_QUERY
    CALL0 WIT_CALL_HANDLE_DUPLICATE
    EXPECT WIT_STATUS_OK
    mov QWORD PTR [rbx + 400], 0
    mov rdi, [rbx + 408]
    AFFINITY rdi, WIT_THREAD_AFFINITY_GET
    EXPECT WIT_STATUS_OK
    cmp QWORD PTR [rbx + 400], 1
    jne failed
    mov rdi, [rbx + 408]
    AFFINITY rdi, WIT_THREAD_AFFINITY_SET
    EXPECT WIT_STATUS_DENIED
    mov rdi, [rbx + 408]
    CALL0 WIT_CALL_HANDLE_CLOSE
    EXPECT WIT_STATUS_OK
    mov edi, WIT_TEST_EXIT_CODE
    jmp exit_process

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
