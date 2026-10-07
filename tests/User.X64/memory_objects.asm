option casemap:none
include user_abi.inc

; Memory object fixture over the memory objects of ABI-1 (RFC 0011 v3 section 7.2, plan step K5.1):
; MEMORY_OBJECT_CREATE, MEMORY_OBJECT_MAP at chosen and fixed addresses under every protection, views that share
; pages, protection changes within the handle's rights, attenuated handles, an object that outlives its handle and
; ends with its last mapping, a dual mapping that runs published code, an object moved through a channel, and the
; object, page and mapping quotas. The requests live in the data page: the map request at 16, a channel request at
; 96, the handles to move at 768, the received handles at 800, the handles of a scenario at 832, the mappings of the
; limits scenario at 1040, the output of HANDLE_DUPLICATE at 1200 and the status of a failed check at 1304. The calls
; are subroutines so that the fixture stays within its one page of code. Registers: r14 test mode, rbx the data page,
; r12 the object handle, r13 and rsi mappings, rdi a scenario handle.

READ_WRITE EQU WIT_MEMORY_READ + WIT_MEMORY_WRITE
READ_EXECUTE EQU WIT_MEMORY_READ + WIT_MEMORY_EXECUTE
FIXED_DATA EQU WIT_USER_MEMORY_BASE + 100000h
FIXED_CODE EQU WIT_USER_CODE_BASE + 10000h

EXPECT MACRO value
    cmp eax, value
    jne failed
ENDM
CALL0 MACRO operation
    mov eax, operation
    int 80h
ENDM
; MEMORY_OBJECT_CREATE: size -> rax, rdx handle.
CREATE MACRO size
    mov rcx, size
    call object_create
ENDM
; MEMORY_OBJECT_MAP: handle, offset, bytes, address (0: chosen), protection -> rax, rdx address.
MAP MACRO handle, offset, bytes, address, protection
    mov rcx, handle
    mov rdx, offset
    mov r8, bytes
    mov r9, address
    mov r10d, protection
    call object_map
ENDM
; MEMORY_RELEASE of a mapping or a reservation.
RELEASE MACRO base
    mov rcx, base
    call memory_release
ENDM
; MEMORY_PROTECT: base, size, protection.
PROTECT MACRO base, size, protection
    mov rcx, base
    mov edx, size
    mov r8d, protection
    call memory_protect
ENDM
CLOSE MACRO handle
    mov rcx, handle
    call close_handle
ENDM
; HANDLE_DUPLICATE with rights -> rax, rdx the new handle.
DUPLICATE MACRO handle, rights
    mov rcx, handle
    mov edx, rights
    call duplicate_handle
ENDM

.code
PUBLIC wit_user_start
wit_user_start PROC
    mov r15, rcx
    mov rbx, WIT_USER_DATA
    cmp DWORD PTR [r15], WIT_ABI_VERSION
    jne failed
    mov r14, [r15 + WIT_TEST_MODE_OFFSET]
    cmp r14, WIT_MEMORY_OBJECT_TEST_CODE
    je code_test
    cmp r14, WIT_MEMORY_OBJECT_TEST_TRANSFER
    je transfer_test
    cmp r14, WIT_MEMORY_OBJECT_TEST_LIMITS
    je limits_test
    cmp r14, WIT_MEMORY_OBJECT_TEST_BASIC
    jne failed

    ; Creation validates the size and the flags before it takes pages.
    CREATE 0
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    CREATE 4097
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    mov ecx, 4096
    mov edx, 1
    xor r8d, r8d
    CALL0 WIT_CALL_MEMORY_OBJECT_CREATE
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    CREATE 8192
    EXPECT WIT_STATUS_OK
    mov r12, rdx
    ; The map request: a foreign version, a wrong size, a foreign target, WRITE with EXECUTE, a window beyond the
    ; object and an empty window are refused before any reservation is taken.
    MAP r12, 0, 8192, 0, READ_WRITE
    EXPECT WIT_STATUS_OK ; the first view, at an address the kernel chose in the data arena
    mov r13, rdx
    mov rax, WIT_USER_MEMORY_BASE
    cmp r13, rax
    jb failed
    mov DWORD PTR [rbx + 16], 2 ; the request the map left: a foreign version
    call raw_map
    EXPECT WIT_STATUS_UNSUPPORTED
    mov DWORD PTR [rbx + 16], WIT_MEMORY_MAP_VERSION
    mov QWORD PTR [rbx + 16 + 48], 0 ; a foreign target
    call raw_map
    EXPECT WIT_STATUS_UNSUPPORTED
    mov rax, -3 ; WIT_PROCESS_SELF
    mov [rbx + 16 + 48], rax
    mov edx, 55 ; a wrong size
    call raw_map_sized
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    MAP r12, 0, 4096, 0, 6 ; WRITE with EXECUTE
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    MAP r12, 4096, 8192, 0, WIT_MEMORY_READ
    EXPECT WIT_STATUS_TOO_LARGE
    MAP r12, 0, 0, 0, WIT_MEMORY_READ
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    ; Two views share the pages; a protection change within the rights takes effect on both.
    mov QWORD PTR [r13], 1122h
    mov QWORD PTR [r13 + 4096], 3344h
    MAP r12, 4096, 4096, 0, WIT_MEMORY_READ
    EXPECT WIT_STATUS_OK
    mov rsi, rdx
    cmp QWORD PTR [rsi], 3344h
    jne failed
    PROTECT rsi, 4096, READ_WRITE
    EXPECT WIT_STATUS_OK
    mov QWORD PTR [rsi], 5566h
    cmp QWORD PTR [r13 + 4096], 5566h
    jne failed
    ; An attenuated handle maps read-only views only, and its views cannot be made writable or committed.
    DUPLICATE r12, WIT_RIGHT_MAP + WIT_RIGHT_QUERY
    EXPECT WIT_STATUS_OK
    mov rdi, rdx
    MAP rdi, 0, 4096, 0, READ_WRITE
    EXPECT WIT_STATUS_DENIED
    MAP rdi, 0, 4096, 0, WIT_MEMORY_READ
    EXPECT WIT_STATUS_OK
    mov [rbx + 832], rdx
    mov rax, rdx
    cmp QWORD PTR [rax], 1122h
    jne failed
    PROTECT QWORD PTR [rbx + 832], 4096, READ_WRITE
    EXPECT WIT_STATUS_DENIED
    mov rcx, [rbx + 832]
    mov edx, 4096
    mov r8d, READ_WRITE
    CALL0 WIT_CALL_MEMORY_COMMIT
    EXPECT WIT_STATUS_DENIED
    RELEASE QWORD PTR [rbx + 832]
    EXPECT WIT_STATUS_OK
    CLOSE rdi
    EXPECT WIT_STATUS_OK
    ; A fixed address must be free; a free one is taken as asked.
    MAP r12, 0, 4096, r13, WIT_MEMORY_READ
    EXPECT WIT_STATUS_BUSY
    MAP r12, 0, 4096, FIXED_DATA, WIT_MEMORY_READ
    EXPECT WIT_STATUS_OK
    mov rax, FIXED_DATA
    cmp rdx, rax
    jne failed
    RELEASE rdx
    EXPECT WIT_STATUS_OK
    ; The object outlives its handle and ends with its last mapping; the object quota is whole again afterward.
    CLOSE r12
    EXPECT WIT_STATUS_OK
    cmp QWORD PTR [r13], 1122h
    jne failed
    cmp QWORD PTR [rsi], 5566h
    jne failed
    RELEASE r13
    EXPECT WIT_STATUS_OK
    RELEASE rsi
    EXPECT WIT_STATUS_OK
    xor edi, edi
objects:
    CREATE 4096
    EXPECT WIT_STATUS_OK
    mov [rbx + 832 + rdi * 8], rdx
    inc edi
    cmp edi, WIT_MEMORY_OBJECT_CAPACITY
    jb objects
    CREATE 4096
    EXPECT WIT_STATUS_NO_MEMORY
    xor edi, edi
close_objects:
    CLOSE QWORD PTR [rbx + 832 + rdi * 8]
    EXPECT WIT_STATUS_OK
    inc edi
    cmp edi, WIT_MEMORY_OBJECT_CAPACITY
    jb close_objects
    jmp passed

code_test:
    ; A writable view receives the instructions, an executable view at a fixed address of the code arena runs them
    ; once published; publication through the writable view is refused.
    CREATE 4096
    EXPECT WIT_STATUS_OK
    mov r12, rdx
    MAP r12, 0, 4096, 0, READ_WRITE
    EXPECT WIT_STATUS_OK
    mov r13, rdx
    MAP r12, 0, 4096, FIXED_CODE, READ_EXECUTE
    EXPECT WIT_STATUS_OK
    mov rsi, rdx
    mov rax, FIXED_CODE
    cmp rsi, rax
    jne failed
    mov DWORD PTR [r13], 001234B8h ; mov eax, 1234h
    mov WORD PTR [r13 + 4], 0C300h ; ret
    mov rcx, r13
    mov edx, 4096
    xor r8d, r8d
    CALL0 WIT_CALL_CODE_PUBLISH
    EXPECT WIT_STATUS_DENIED
    mov rcx, rsi
    mov edx, 4096
    xor r8d, r8d
    CALL0 WIT_CALL_CODE_PUBLISH
    EXPECT WIT_STATUS_OK
    call rsi
    cmp eax, 1234h
    jne failed
    PROTECT r13, 4096, 6 ; WRITE with EXECUTE
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    PROTECT r13, 4096, READ_EXECUTE
    EXPECT WIT_STATUS_OK
    mov rcx, r13
    mov edx, 4096
    xor r8d, r8d
    CALL0 WIT_CALL_CODE_PUBLISH
    EXPECT WIT_STATUS_OK
    call r13
    cmp eax, 1234h
    jne failed
    RELEASE rsi
    EXPECT WIT_STATUS_OK
    RELEASE r13
    EXPECT WIT_STATUS_OK
    CLOSE r12
    EXPECT WIT_STATUS_OK
    jmp passed

transfer_test:
    ; An object moves through a channel: the sender's handle is gone, its view stays, the receiver's view reads.
    CREATE 4096
    EXPECT WIT_STATUS_OK
    mov r12, rdx
    MAP r12, 0, 4096, 0, READ_WRITE
    EXPECT WIT_STATUS_OK
    mov r13, rdx
    mov QWORD PTR [r13], 7777h
    mov rcx, rbx
    xor edx, edx
    xor r8d, r8d
    CALL0 WIT_CALL_CHANNEL_CREATE
    EXPECT WIT_STATUS_OK
    mov [rbx + 768], r12
    mov rcx, [rbx]
    call send_handle
    EXPECT WIT_STATUS_OK
    MAP r12, 0, 4096, 0, WIT_MEMORY_READ
    EXPECT WIT_STATUS_BAD_HANDLE
    mov rcx, [rbx + 8]
    call receive_handle
    EXPECT WIT_STATUS_OK
    mov rdi, [rbx + 800]
    MAP rdi, 0, 4096, 0, WIT_MEMORY_READ
    EXPECT WIT_STATUS_OK
    mov rsi, rdx
    cmp QWORD PTR [rsi], 7777h
    jne failed
    RELEASE r13
    EXPECT WIT_STATUS_OK
    RELEASE rsi
    EXPECT WIT_STATUS_OK
    CLOSE rdi
    EXPECT WIT_STATUS_OK
    CLOSE QWORD PTR [rbx]
    EXPECT WIT_STATUS_OK
    CLOSE QWORD PTR [rbx + 8]
    EXPECT WIT_STATUS_OK
    jmp passed

limits_test:
    ; An object beyond its page quota is refused; a second large object exceeds the component's pages and takes
    ; none; the mappings of one object are bounded by the reservations.
    CREATE (WIT_MEMORY_OBJECT_PAGES + 1) * 4096
    EXPECT WIT_STATUS_TOO_LARGE
    CREATE WIT_MEMORY_OBJECT_PAGES * 4096
    EXPECT WIT_STATUS_OK
    mov r12, rdx
    CREATE WIT_MEMORY_OBJECT_PAGES * 4096
    EXPECT WIT_STATUS_NO_MEMORY
    CLOSE r12
    EXPECT WIT_STATUS_OK
    CREATE WIT_MEMORY_OBJECT_PAGES * 4096
    EXPECT WIT_STATUS_OK
    CLOSE rdx
    EXPECT WIT_STATUS_OK
    CREATE 4096
    EXPECT WIT_STATUS_OK
    mov r12, rdx
    xor edi, edi
mappings:
    MAP r12, 0, 4096, 0, WIT_MEMORY_READ
    EXPECT WIT_STATUS_OK
    mov [rbx + 1040 + rdi * 8], rdx
    inc edi
    cmp edi, WIT_USER_RESERVATION_CAPACITY
    jb mappings
    MAP r12, 0, 4096, 0, WIT_MEMORY_READ
    EXPECT WIT_STATUS_NO_MEMORY
    xor edi, edi
release_mappings:
    RELEASE QWORD PTR [rbx + 1040 + rdi * 8]
    EXPECT WIT_STATUS_OK
    inc edi
    cmp edi, WIT_USER_RESERVATION_CAPACITY
    jb release_mappings
    CLOSE r12
    EXPECT WIT_STATUS_OK
    jmp passed

; MEMORY_OBJECT_CREATE: rcx size -> rax, rdx handle.
object_create:
    xor edx, edx
    xor r8d, r8d
    CALL0 WIT_CALL_MEMORY_OBJECT_CREATE
    ret

; The map request at data page + 16: rcx object, rdx offset, r8 bytes, r9 address, r10d protection; then the call.
object_map:
    lea rax, [rbx + 16]
    mov DWORD PTR [rax], WIT_MEMORY_MAP_VERSION
    mov DWORD PTR [rax + 4], 56
    mov [rax + 8], rcx
    mov [rax + 16], rdx
    mov [rax + 24], r8
    mov [rax + 32], r9
    mov [rax + 40], r10d
    mov DWORD PTR [rax + 44], 0
    mov rcx, -3 ; WIT_PROCESS_SELF
    mov [rax + 48], rcx
; MEMORY_OBJECT_MAP of the request as it lies at data page + 16; raw_map_sized takes the size in edx.
raw_map:
    mov edx, 56
raw_map_sized:
    lea rcx, [rbx + 16]
    xor r8d, r8d
    CALL0 WIT_CALL_MEMORY_OBJECT_MAP
    ret

memory_release:
    xor edx, edx
    xor r8d, r8d
    CALL0 WIT_CALL_MEMORY_RELEASE
    ret

memory_protect:
    CALL0 WIT_CALL_MEMORY_PROTECT
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

; CHANNEL_SEND of the one handle at data page + 768 through rcx; CHANNEL_RECEIVE of one handle into + 800 from rcx.
send_handle:
    lea rax, [rbx + 96]
    lea r9, [rbx + 768]
    jmp channel_request
receive_handle:
    lea rax, [rbx + 96]
    lea r9, [rbx + 800]
channel_request:
    mov DWORD PTR [rax], WIT_CHANNEL_MESSAGE_VERSION
    mov DWORD PTR [rax + 4], 40
    mov QWORD PTR [rax + 8], 0
    mov [rax + 16], r9
    mov DWORD PTR [rax + 24], 0
    mov DWORD PTR [rax + 28], 1
    mov DWORD PTR [rax + 32], 0
    mov DWORD PTR [rax + 36], 0
    mov rdx, rax
    mov r8d, 40
    lea rax, [rbx + 768]
    cmp r9, rax
    jne receive_call
    CALL0 WIT_CALL_CHANNEL_SEND
    ret
receive_call:
    CALL0 WIT_CALL_CHANNEL_RECEIVE
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
wit_user_start ENDP
END
