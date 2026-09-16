option casemap:none
include user_abi.inc

EXPECT MACRO value
    cmp rax, value
    jne failed
ENDM

.code
PUBLIC wit_user_start
wit_user_start PROC
    mov r15, rcx
    mov ax, cs
    and eax, 3
    cmp eax, 3
    jne failed
    cmp DWORD PTR [r15], WIT_ABI_VERSION
    jne failed
    cmp DWORD PTR [r15 + 4], WIT_ABI_STARTUP_SIZE
    jne failed
    movq rax, xmm0
    test rax, rax
    jne failed
    mov r12, 055AA001100220033h
    movq xmm6, r12
    xor eax, eax
    mov rdi, WIT_USER_DATA
    mov ecx, 1024
    cld
    repe scasq
    jne failed

    mov rax, WIT_CALL_QUERY
    int 80h
    EXPECT WIT_STATUS_OK
    cmp rdx, WIT_ABI_VERSION
    jne failed

    mov r14, [r15 + WIT_TEST_MODE_OFFSET]
    cmp r14, WIT_TEST_KERNEL_READ
    je kernel_read
    cmp r14, WIT_TEST_KERNEL_WRITE
    je kernel_write
    cmp r14, WIT_TEST_PRIVILEGED_CLI
    je privileged_cli
    cmp r14, WIT_TEST_PRIVILEGED_PORT
    je privileged_port
    cmp r14, WIT_TEST_EXECUTE_DATA
    je execute_data
    cmp r14, WIT_TEST_GUARD_LOW
    je guard_low
    cmp r14, WIT_TEST_GUARD_HIGH
    je guard_high
    cmp r14, WIT_TEST_WRITE_CODE
    je write_code
    cmp r14, WIT_TEST_WRITE_INFO
    je write_info
    cmp r14, WIT_TEST_PEER_READ
    je peer_read
    cmp r14, WIT_TEST_SPIN
    je spin_forever
    cmp r14, WIT_TEST_NULL_READ
    je null_read
    cmp r14, WIT_TEST_INVALID_OPCODE
    je invalid_opcode
    cmp r14, WIT_TEST_BAD_RETURN
    je bad_return
    cmp r14, WIT_TEST_PREEMPTION_STATE
    je preemption_state
    cmp r14, WIT_TEST_MEMORY_LIFECYCLE
    jae memory_start
    cmp r14, WIT_TEST_NORMAL
    jne failed

    mov rax, 0FFFFh
    int 80h
    EXPECT WIT_STATUS_UNSUPPORTED
    mov rcx, [r15 + WIT_TEST_RO_OFFSET]
    call try_write
    EXPECT WIT_STATUS_DENIED
    mov rcx, [r15 + WIT_TEST_SELF_OFFSET]
    call try_write
    EXPECT WIT_STATUS_WRONG_TYPE
    mov rcx, [r15 + WIT_TEST_FOREIGN_OFFSET]
    call try_write
    EXPECT WIT_STATUS_BAD_HANDLE
    xor ecx, ecx
    call try_write
    EXPECT WIT_STATUS_BAD_HANDLE

    mov rcx, [r15 + 8]
    lea rdx, message
    mov r8, WIT_ABI_MAX_WRITE + 1
    mov rax, WIT_CALL_WRITE
    int 80h
    EXPECT WIT_STATUS_TOO_LARGE

    mov rcx, [r15 + 8]
    mov rdx, -2
    mov r8, 8
    mov rax, WIT_CALL_WRITE
    int 80h
    EXPECT WIT_STATUS_BAD_ADDRESS

    mov rcx, [r15 + 8]
    mov rdx, [r15 + WIT_TEST_KERNEL_OFFSET]
    mov r8, 8
    mov rax, WIT_CALL_WRITE
    int 80h
    EXPECT WIT_STATUS_BAD_ADDRESS

    mov rcx, [r15 + 8]
    mov rdx, WIT_USER_DATA_END - 2
    mov r8, 8
    mov rax, WIT_CALL_WRITE
    int 80h
    EXPECT WIT_STATUS_BAD_ADDRESS

    mov rcx, [r15 + 8]
    xor edx, edx
    xor r8d, r8d
    mov rax, WIT_CALL_WRITE
    int 80h
    EXPECT WIT_STATUS_OK
    test rdx, rdx
    jne failed

    ; A valid readable code buffer and a valid buffer spanning two data pages.
    mov rcx, [r15 + 8]
    call try_write
    EXPECT WIT_STATUS_OK
    cmp rdx, message_end - message
    jne failed
    mov rbx, WIT_USER_DATA + 4092
    mov DWORD PTR [rbx], 0736F7263h
    mov DWORD PTR [rbx + 4], 00A677073h
    mov rcx, [r15 + 8]
    mov rdx, rbx
    mov r8, 8
    mov rax, WIT_CALL_WRITE
    int 80h
    EXPECT WIT_STATUS_OK
    cmp rdx, 8
    jne failed

    mov rcx, [r15 + 8]
    mov rax, WIT_CALL_CLOSE
    int 80h
    EXPECT WIT_STATUS_OK
    mov rcx, [r15 + 8]
    call try_write
    EXPECT WIT_STATUS_BAD_HANDLE
    mov rcx, [r15 + 8]
    mov rax, WIT_CALL_CLOSE
    int 80h
    EXPECT WIT_STATUS_BAD_HANDLE

    mov rax, 055AA001100220033h
    cmp r12, rax
    jne failed
    movq rax, xmm6
    cmp rax, r12
    jne failed
    mov rbx, WIT_USER_DATA
    mov rax, [r15 + WIT_TEST_INSTANCE_OFFSET]
    mov [rbx], rax
    mov ecx, WIT_TEST_EXIT_CODE
    jmp exit_component

; Memory tests use the same INT 80h boundary as future runtime callers.
memory_start:
    mov rcx, 0800000000h ; 32 GiB reservation
    mov edx, 0200000h
    mov eax, WIT_CALL_MEMORY_RESERVE
    int 80h
    EXPECT WIT_STATUS_OK
    mov rbx, rdx
    mov rax, WIT_USER_MEMORY_BASE
    cmp rbx, rax
    jne failed
    cmp r14, WIT_TEST_MEMORY_RESERVED
    je memory_read_fault

    ; Force a partially completed commit to exhaust the component's frame quota.
    ; The syscall must report failure, leave no accessible prefix and allow retry.
    mov rcx, rbx
    mov edx, WIT_USER_PAGE_CAPACITY * 4096
    mov r8d, 3
    mov eax, WIT_CALL_MEMORY_COMMIT
    int 80h
    EXPECT WIT_STATUS_NO_MEMORY
    test rdx, rdx
    jne failed
    call memory_bad_buffer

    mov rcx, rbx
    mov edx, 8192
    mov r8d, WIT_MEMORY_READ + WIT_MEMORY_WRITE
    mov eax, WIT_CALL_MEMORY_COMMIT
    int 80h
    EXPECT WIT_STATUS_OK
    mov rdi, rbx
    mov ecx, 1024
    xor eax, eax
    repe scasq
    jne failed
    mov QWORD PTR [rbx], 12345678h
    mov QWORD PTR [rbx + 8184], 76543210h
    cmp r14, WIT_TEST_MEMORY_NX
    je memory_execute_fault

    ; Warm writable translations before removing permissions.
    mov rcx, rbx
    mov edx, 8192
    mov r8d, WIT_MEMORY_READ
    mov eax, WIT_CALL_MEMORY_PROTECT
    int 80h
    EXPECT WIT_STATUS_OK
    cmp r14, WIT_TEST_MEMORY_READONLY
    je memory_write_fault
    cmp QWORD PTR [rbx], 12345678h
    jne failed
    cmp QWORD PTR [rbx + 8184], 76543210h
    jne failed

    mov rcx, rbx
    mov edx, 8192
    xor r8d, r8d
    mov eax, WIT_CALL_MEMORY_PROTECT
    int 80h
    EXPECT WIT_STATUS_OK
    cmp r14, WIT_TEST_MEMORY_NOACCESS
    je memory_read_fault
    call memory_bad_buffer
    ; Idempotent commit must preserve NOACCESS and content.
    mov rcx, rbx
    mov edx, 8192
    mov r8d, 3
    mov eax, WIT_CALL_MEMORY_COMMIT
    int 80h
    EXPECT WIT_STATUS_OK
    call memory_bad_buffer
    mov rcx, rbx
    mov edx, 8192
    mov r8d, 3
    mov eax, WIT_CALL_MEMORY_PROTECT
    int 80h
    EXPECT WIT_STATUS_OK
    cmp QWORD PTR [rbx], 12345678h
    jne failed
    cmp QWORD PTR [rbx + 8184], 76543210h
    jne failed
    ; A range containing a hole must fail without changing its mapped prefix.
    mov rcx, rbx
    mov edx, 12288
    xor r8d, r8d
    mov eax, WIT_CALL_MEMORY_PROTECT
    int 80h
    EXPECT WIT_STATUS_NOT_COMMITTED
    mov QWORD PTR [rbx], 1111h

    mov rcx, rbx
    mov edx, 8192
    mov eax, WIT_CALL_MEMORY_DECOMMIT
    int 80h
    EXPECT WIT_STATUS_OK
    cmp r14, WIT_TEST_MEMORY_DECOMMITTED
    je memory_read_fault
    call memory_bad_buffer
    ; Allocate physical memory with no access, then expose zero-filled contents.
    mov rcx, rbx
    mov edx, 8192
    xor r8d, r8d
    mov eax, WIT_CALL_MEMORY_COMMIT
    int 80h
    EXPECT WIT_STATUS_OK
    call memory_bad_buffer
    mov rcx, rbx
    mov edx, 8192
    mov r8d, 3
    mov eax, WIT_CALL_MEMORY_PROTECT
    int 80h
    EXPECT WIT_STATUS_OK
    mov rdi, rbx
    mov ecx, 1024
    xor eax, eax
    repe scasq
    jne failed
    mov QWORD PTR [rbx], 2222h
    mov rcx, rbx
    mov eax, WIT_CALL_MEMORY_RELEASE
    int 80h
    EXPECT WIT_STATUS_OK
    cmp r14, WIT_TEST_MEMORY_RELEASED
    je memory_read_fault
    call memory_bad_buffer
    mov rcx, rbx
    mov edx, 4096
    mov r8d, 3
    mov eax, WIT_CALL_MEMORY_COMMIT
    int 80h
    EXPECT WIT_STATUS_NOT_RESERVED
    mov rcx, rbx
    mov eax, WIT_CALL_MEMORY_RELEASE
    int 80h
    EXPECT WIT_STATUS_NOT_RESERVED

    mov ecx, 8192
    mov edx, 4096
    mov eax, WIT_CALL_MEMORY_RESERVE
    int 80h
    EXPECT WIT_STATUS_OK
    cmp rdx, rbx
    jne failed
    mov rcx, rbx
    mov edx, 8192
    mov r8d, 3
    mov eax, WIT_CALL_MEMORY_COMMIT
    int 80h
    EXPECT WIT_STATUS_OK
    mov rdi, rbx
    mov ecx, 1024
    xor eax, eax
    repe scasq
    jne failed
    ; Dynamic user buffers must work, including a page boundary.
    mov DWORD PTR [rbx + 4092], 0746D656Dh
    mov DWORD PTR [rbx + 4096], 00A747365h
    mov rcx, [r15 + 8]
    lea rdx, [rbx + 4092]
    mov r8d, 8
    mov eax, WIT_CALL_WRITE
    int 80h
    EXPECT WIT_STATUS_OK
    cmp rdx, 8
    jne failed

    mov rcx, WIT_USER_CODE
    mov edx, 4096
    mov r8d, 3
    mov eax, WIT_CALL_MEMORY_PROTECT
    int 80h
    EXPECT WIT_STATUS_BAD_ADDRESS
    mov rcx, rbx
    inc rcx
    mov edx, 4096
    mov eax, WIT_CALL_MEMORY_DECOMMIT
    int 80h
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    mov rcx, rbx
    mov edx, 4096
    mov r8d, 5 ; executable dynamic memory is not in this ABI
    mov eax, WIT_CALL_MEMORY_PROTECT
    int 80h
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    mov rcx, rbx
    mov rdx, -4096
    mov r8d, 3
    mov eax, WIT_CALL_MEMORY_COMMIT
    int 80h
    EXPECT WIT_STATUS_BAD_ADDRESS
    mov ecx, 4096
    mov edx, 12288
    mov eax, WIT_CALL_MEMORY_RESERVE
    int 80h
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    test rdx, rdx
    jne failed
    mov rcx, rbx
    mov eax, WIT_CALL_MEMORY_RELEASE
    int 80h
    EXPECT WIT_STATUS_OK
    movq rax, xmm6
    cmp rax, r12
    jne failed
    mov ecx, WIT_TEST_EXIT_CODE
    jmp exit_component

memory_bad_buffer:
    mov rcx, [r15 + 8]
    mov rdx, rbx
    mov r8d, 8
    mov eax, WIT_CALL_WRITE
    int 80h
    EXPECT WIT_STATUS_BAD_ADDRESS
    test rdx, rdx
    jne failed
    ret
memory_read_fault:
    mov rax, [rbx]
    jmp failed
memory_write_fault:
    mov QWORD PTR [rbx], 0
    jmp failed
memory_execute_fault:
    mov BYTE PTR [rbx], 0C3h
    call rbx
    jmp failed

try_write:
    lea rdx, message
    mov r8, message_end - message
    mov rax, WIT_CALL_WRITE
    int 80h
    ret

kernel_read:
    mov rax, [r15 + WIT_TEST_KERNEL_OFFSET]
    mov rax, [rax]
    jmp failed
kernel_write:
    mov rax, [r15 + WIT_TEST_KERNEL_OFFSET]
    mov QWORD PTR [rax], 0
    jmp failed
privileged_cli:
    cli
    jmp failed
privileged_port:
    mov dx, 0F4h
    mov eax, 10h
    out dx, eax
    jmp failed
execute_data:
    mov rax, WIT_USER_DATA
    mov BYTE PTR [rax], 0C3h
    call rax
    jmp failed
guard_low:
    mov rax, WIT_USER_STACK_BOTTOM - 1
    mov BYTE PTR [rax], 1
    jmp failed
guard_high:
    mov rax, WIT_USER_STACK_TOP
    mov BYTE PTR [rax], 1
    jmp failed
write_code:
    mov rax, WIT_USER_CODE
    mov BYTE PTR [rax], 90h
    jmp failed
write_info:
    mov BYTE PTR [r15], 0
    jmp failed
peer_read:
    mov rax, WIT_USER_PEER_PAGE
    mov rax, [rax]
    jmp failed
null_read:
    xor eax, eax
    mov rax, [rax]
    jmp failed
invalid_opcode:
    ud2
    jmp failed
bad_return:
    mov rsp, 0000800000000000h ; noncanonical in four-level paging
    mov rax, WIT_CALL_QUERY
    int 80h
    jmp spin_forever
preemption_state:
    cmp r12, r12
state_loop:
    ; Every instruction preserves ZF=1 until a successful comparison sets it again.
    ; Lost condition flags can take 'failed'; GPR/SIMD mismatches also fail.
    jne failed
    movq rax, xmm6
    cmp rax, r12
    jne failed
    pause
    jmp state_loop
spin_forever:
    pause
    jmp spin_forever
failed:
    mov ecx, 241
exit_component:
    mov rax, WIT_CALL_EXIT
    int 80h
    ud2
message BYTE 'Hello from ring 3.', 10
message_end:
wit_user_start ENDP
END
