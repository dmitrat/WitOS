option casemap:none
include user_abi.inc
EXTERN __ImageBase:BYTE
PUBLIC data_pointer, written, split_pointer, readonly_pointer, bss_data

.code
PUBLIC wit_pe_start
wit_pe_start PROC
    mov r15, rcx
    lea rbx, __ImageBase
    cmp WORD PTR [rbx], 5A4Dh
    jne failed
    cmp DWORD PTR [r15], WIT_ABI_VERSION
    jne failed
    mov ax, cs
    and eax, 3
    cmp eax, 3
    jne failed

    mov rax, QWORD PTR [data_pointer]
    lea rdx, message
    cmp rax, rdx
    jne failed
    mov rax, QWORD PTR [readonly_pointer]
    lea rdx, helper
    cmp rax, rdx
    jne failed
    mov rax, QWORD PTR [split_pointer]
    cmp rax, rdx
    jne failed
    call QWORD PTR [readonly_pointer]
    cmp rax, 5A17h
    jne failed
    lea rdi, bss_data
    mov ecx, 8192
    xor eax, eax
    cld
    repe scasb
    jne failed
    cmp QWORD PTR [written], 0
    jne failed
    mov rax, [r15 + WIT_TEST_INSTANCE_OFFSET]
    mov QWORD PTR [written], rax

    mov r14, [r15 + WIT_TEST_MODE_OFFSET]
    cmp r14, WIT_IMAGE_TEST_WRITE_CODE
    je write_code
    cmp r14, WIT_IMAGE_TEST_WRITE_RO
    je write_ro
    cmp r14, WIT_IMAGE_TEST_WRITE_HEADER
    je write_header
    cmp r14, WIT_IMAGE_TEST_EXECUTE_DATA
    je execute_data
    cmp r14, WIT_IMAGE_TEST_END
    je read_end
    cmp r14, WIT_IMAGE_TEST_GAP
    je read_gap
    test r14, r14
    jne failed
    mov rcx, [r15 + 8]
    lea rdx, message
    mov r8d, message_end - message
    mov eax, WIT_CALL_DEBUG_WRITE
    int 80h
    test rax, rax
    jne failed
    cmp rdx, message_end - message
    jne failed
    mov ecx, WIT_TEST_EXIT_CODE
    jmp exit_component
write_code:
    lea rax, wit_pe_start
    mov BYTE PTR [rax], 90h
    jmp failed
write_ro:
    lea rax, readonly_pointer
    mov BYTE PTR [rax], 0
    jmp failed
write_header:
    mov BYTE PTR [rbx], 0
    jmp failed
execute_data:
    lea rax, data_pointer
    mov BYTE PTR [rax], 0C3h
    call rax
    jmp failed
read_end:
    mov eax, DWORD PTR [rbx + 3Ch]
    mov eax, DWORD PTR [rbx + rax + 50h]
    mov al, BYTE PTR [rbx + rax]
    jmp failed
read_gap:
    mov eax, DWORD PTR [rbx + 3Ch]
    mov eax, DWORD PTR [rbx + rax + 0B0h]
    sub eax, 4096
    mov al, BYTE PTR [rbx + rax]
    jmp failed
failed:
    mov ecx, 241
exit_component:
    mov eax, WIT_CALL_PROCESS_EXIT
    int 80h
    ud2
helper::
    mov eax, 5A17h
    ret
wit_pe_start ENDP

.const
readonly_pointer QWORD OFFSET helper
message BYTE 'Hello from a relocated PE image.', 10
message_end:

USERDATA SEGMENT ALIGN(4096) 'DATA'
data_pointer QWORD OFFSET message
written QWORD 0
BYTE 4076 DUP (0)
split_pointer QWORD OFFSET helper ; DIR64 starts four bytes before a page boundary
USERDATA ENDS

.data?
bss_data BYTE 8192 DUP (?)
END
