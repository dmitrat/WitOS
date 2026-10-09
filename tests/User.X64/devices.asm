option casemap:none
include user_abi.inc

; Device fixture over the devices of ABI-1 (RFC 0011 v3 section 7.7, plan step K3.1): the device table as a
; read-only memory object, DEVICE_ACQUIRE over it and DEVICE_MEMORY of the acquired device's regions. The fixture
; knows what the kernel does not: the identity of the board's virtio block function (vendor 1AF4h, device 1001h) and
; that its first region is the function's configuration space, whose first words repeat that identity. The data
; page: the map request at 16, a channel request at 96, the handles to move at 768, the received handles at 800,
; the created endpoints at 832, the output of HANDLE_DUPLICATE at 1200, the table handle at 1296 and the status of
; a failed check at 1304. Registers: rbx the data page, r15 the table handle, rsi the mapped table, rbp the
; descriptor of the block function, r13 its index, r12 the device handle, r14 and rdi scratch handles.

VIRTIO_BLOCK EQU 10011AF4h
DESCRIPTOR_IDENTITY EQU 8
DESCRIPTOR_REGION_COUNT EQU 24
DESCRIPTOR_REGIONS EQU 32
REGION_SIZE EQU 24
REGION_FLAGS EQU 16
READ_WRITE EQU WIT_MEMORY_READ + WIT_MEMORY_WRITE
READ_EXECUTE EQU WIT_MEMORY_READ + WIT_MEMORY_EXECUTE

EXPECT MACRO value
    cmp eax, value
    jne failed
ENDM
CALL0 MACRO operation
    mov eax, operation
    int 80h
ENDM
; MEMORY_OBJECT_MAP: handle, bytes, protection -> rax, rdx address (chosen by the kernel).
MAP MACRO handle, bytes, protection
    mov rcx, handle
    mov r8, bytes
    mov r10d, protection
    call object_map
ENDM
; DEVICE_ACQUIRE: table handle, index -> rax, rdx device handle.
ACQUIRE MACRO table, index
    mov rcx, table
    mov rdx, index
    call device_acquire
ENDM
; DEVICE_MEMORY: device handle, region -> rax, rdx object handle.
REGION MACRO device, index
    mov rcx, device
    mov rdx, index
    call device_memory
ENDM
RELEASE MACRO base
    mov rcx, base
    call memory_release
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
    mov rbx, WIT_USER_DATA
    cmp DWORD PTR [rcx], WIT_ABI_VERSION
    jne failed
    mov r14, [rcx + WIT_TEST_MODE_OFFSET]
    mov r15, [rcx + WIT_TEST_TABLE_OFFSET]
    mov [rbx + 1296], r15
    call map_table ; rsi the table, rbp the block function's descriptor, r13 its index
    cmp r14, WIT_DEVICE_TEST_AUTHORITY
    je authority_test
    cmp r14, WIT_DEVICE_TEST_TABLE
    jne failed

    ; The table is read-only: a writable view is refused by the handle's rights, and so is an executable one.
    MAP r15, 4096, READ_WRITE
    EXPECT WIT_STATUS_DENIED
    MAP r15, 4096, READ_EXECUTE
    EXPECT WIT_STATUS_DENIED
    ; The block function's first region is its configuration space; its words repeat the identity of the table.
    cmp DWORD PTR [rbp + DESCRIPTOR_REGION_COUNT], 1
    jb failed
    test DWORD PTR [rbp + DESCRIPTOR_REGIONS + REGION_FLAGS], WIT_DEVICE_REGION_CONFIG
    je failed
    ACQUIRE r15, r13
    EXPECT WIT_STATUS_OK
    mov r12, rdx
    REGION r12, 0
    EXPECT WIT_STATUS_OK
    mov rdi, rdx
    MAP rdi, 4096, WIT_MEMORY_READ
    EXPECT WIT_STATUS_OK
    mov r14, rdx
    cmp DWORD PTR [r14], VIRTIO_BLOCK
    jne failed
    mov eax, [rbp + DESCRIPTOR_IDENTITY + 4] ; class and revision
    cmp [r14 + 8], eax
    jne failed
    ; A writable view is within the region handle's rights; an executable one never is; the view is not committable.
    MAP rdi, 4096, READ_WRITE
    EXPECT WIT_STATUS_OK
    mov [rbx + 840], rdx
    mov rcx, rdx
    cmp DWORD PTR [rcx], VIRTIO_BLOCK
    jne failed
    RELEASE QWORD PTR [rbx + 840]
    EXPECT WIT_STATUS_OK
    MAP rdi, 4096, READ_EXECUTE
    EXPECT WIT_STATUS_DENIED
    mov rcx, r14
    mov edx, 4096
    mov r8d, READ_WRITE
    CALL0 WIT_CALL_MEMORY_COMMIT
    EXPECT WIT_STATUS_DENIED
    mov rcx, r14
    mov edx, 4096
    mov r8d, READ_EXECUTE
    CALL0 WIT_CALL_MEMORY_PROTECT
    EXPECT WIT_STATUS_DENIED
    ; The region object outlives the device handle and the view outlives the object's handle; the device is free
    ; again only when every handle to it is gone.
    CLOSE r12
    EXPECT WIT_STATUS_OK
    ACQUIRE r15, r13
    EXPECT WIT_STATUS_OK
    mov r12, rdx
    CLOSE rdi
    EXPECT WIT_STATUS_OK
    cmp DWORD PTR [r14], VIRTIO_BLOCK
    jne failed
    ; A call's buffer in a device region is refused: the kernel has no view of device memory (S6.2).
    mov rcx, r14
    mov edx, 56
    xor r8d, r8d
    CALL0 WIT_CALL_MEMORY_OBJECT_MAP
    EXPECT WIT_STATUS_BAD_ADDRESS
    RELEASE r14
    EXPECT WIT_STATUS_OK
    CLOSE r12
    EXPECT WIT_STATUS_OK
    jmp passed

authority_test:
    ; A table handle without ACQUIRE acquires nothing; indexes and reserved arguments are checked first.
    DUPLICATE r15, WIT_RIGHT_MAP + WIT_RIGHT_QUERY
    EXPECT WIT_STATUS_OK
    mov rdi, rdx
    ACQUIRE rdi, r13
    EXPECT WIT_STATUS_DENIED
    CLOSE rdi
    EXPECT WIT_STATUS_OK
    ACQUIRE r15, 0FFFFh
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    mov rcx, r15
    mov rdx, r13
    mov r8d, 1
    CALL0 WIT_CALL_DEVICE_ACQUIRE
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    ; One holder at a time; a region index beyond the descriptor, a reserved argument and a port region are refused.
    ACQUIRE r15, r13
    EXPECT WIT_STATUS_OK
    mov r12, rdx
    ACQUIRE r15, r13
    EXPECT WIT_STATUS_BUSY
    REGION r12, 99
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    mov rcx, r12
    xor edx, edx
    mov r8d, 1
    CALL0 WIT_CALL_DEVICE_MEMORY
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    xor edi, edi
port_regions:
    cmp edi, [rbp + DESCRIPTOR_REGION_COUNT]
    jae port_regions_done
    imul eax, edi, REGION_SIZE
    test DWORD PTR [rbp + DESCRIPTOR_REGIONS + rax + REGION_FLAGS], WIT_DEVICE_REGION_PORT
    je port_regions_next
    REGION r12, rdi
    EXPECT WIT_STATUS_UNSUPPORTED
port_regions_next:
    inc edi
    jmp port_regions
port_regions_done:
    ; A device handle without BIND maps no region; the device stays held through it.
    DUPLICATE r12, WIT_RIGHT_QUERY
    EXPECT WIT_STATUS_OK
    mov rdi, rdx
    REGION rdi, 0
    EXPECT WIT_STATUS_DENIED
    CLOSE r12
    EXPECT WIT_STATUS_OK
    ACQUIRE r15, r13
    EXPECT WIT_STATUS_BUSY
    CLOSE rdi
    EXPECT WIT_STATUS_OK
    ; The device moves through a channel: in flight it is held, received it works, and a dropped message frees it.
    lea rcx, [rbx + 832]
    xor edx, edx
    xor r8d, r8d
    CALL0 WIT_CALL_CHANNEL_CREATE
    EXPECT WIT_STATUS_OK
    ACQUIRE r15, r13
    EXPECT WIT_STATUS_OK
    mov r12, rdx
    mov [rbx + 768], r12
    mov rcx, [rbx + 832]
    call send_handle
    EXPECT WIT_STATUS_OK
    REGION r12, 0
    EXPECT WIT_STATUS_BAD_HANDLE
    ACQUIRE r15, r13
    EXPECT WIT_STATUS_BUSY
    mov rcx, [rbx + 840]
    call receive_handle
    EXPECT WIT_STATUS_OK
    mov r12, [rbx + 800]
    REGION r12, 0
    EXPECT WIT_STATUS_OK
    CLOSE rdx
    EXPECT WIT_STATUS_OK
    mov [rbx + 768], r12
    mov rcx, [rbx + 832]
    call send_handle
    EXPECT WIT_STATUS_OK
    CLOSE QWORD PTR [rbx + 840] ; the receiving endpoint: its queue is dropped with the device handle
    EXPECT WIT_STATUS_OK
    ACQUIRE r15, r13
    EXPECT WIT_STATUS_OK
    CLOSE rdx
    EXPECT WIT_STATUS_OK
    CLOSE QWORD PTR [rbx + 832]
    EXPECT WIT_STATUS_OK
    jmp passed

; Maps the table read-only into rsi, checks its header and finds the block function: rbp its descriptor, r13 its
; index. The table handle is r15.
map_table:
    MAP r15, 4096, WIT_MEMORY_READ
    EXPECT WIT_STATUS_OK
    mov rsi, rdx
    cmp DWORD PTR [rsi], WIT_DEVICE_TABLE_VERSION
    jne failed
    cmp DWORD PTR [rsi + 4], WIT_DEVICE_TABLE_SIZE
    jne failed
    cmp DWORD PTR [rsi + 12], WIT_DEVICE_DESCRIPTOR_SIZE
    jne failed
    mov eax, [rsi + 8]
    test eax, eax
    je failed
    cmp eax, WIT_DEVICE_CAPACITY
    ja failed
    xor r13d, r13d
    lea rbp, [rsi + WIT_DEVICE_TABLE_SIZE]
find_device:
    cmp DWORD PTR [rbp + DESCRIPTOR_IDENTITY], VIRTIO_BLOCK
    je found_device
    add rbp, WIT_DEVICE_DESCRIPTOR_SIZE
    inc r13d
    cmp r13d, [rsi + 8]
    jb find_device
    jmp failed
found_device:
    ret

; The map request at data page + 16: rcx object, r8 bytes, r10d protection; offset 0, address chosen.
object_map:
    lea rax, [rbx + 16]
    mov DWORD PTR [rax], WIT_MEMORY_MAP_VERSION
    mov DWORD PTR [rax + 4], 56
    mov [rax + 8], rcx
    mov QWORD PTR [rax + 16], 0
    mov [rax + 24], r8
    mov QWORD PTR [rax + 32], 0
    mov [rax + 40], r10d
    mov DWORD PTR [rax + 44], 0
    mov rcx, -3 ; WIT_PROCESS_SELF
    mov [rax + 48], rcx
    lea rcx, [rbx + 16]
    mov edx, 56
    xor r8d, r8d
    CALL0 WIT_CALL_MEMORY_OBJECT_MAP
    ret

device_acquire:
    xor r8d, r8d
    CALL0 WIT_CALL_DEVICE_ACQUIRE
    ret

device_memory:
    xor r8d, r8d
    CALL0 WIT_CALL_DEVICE_MEMORY
    ret

memory_release:
    xor edx, edx
    xor r8d, r8d
    CALL0 WIT_CALL_MEMORY_RELEASE
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
    lea r9, [rbx + 768]
    call channel_request
    CALL0 WIT_CALL_CHANNEL_SEND
    ret
receive_handle:
    lea r9, [rbx + 800]
    call channel_request
    CALL0 WIT_CALL_CHANNEL_RECEIVE
    ret
channel_request:
    lea rax, [rbx + 96]
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
    ret

failed:
    mov [rbx + 1304], rax ; the status the failed check saw
    mov ecx, 241
    jmp exit_process
passed:
    RELEASE rsi
    EXPECT WIT_STATUS_OK
    CLOSE QWORD PTR [rbx + 1296]
    EXPECT WIT_STATUS_OK
    mov ecx, WIT_TEST_EXIT_CODE
exit_process:
    CALL0 WIT_CALL_PROCESS_EXIT
    ud2
wit_user_start ENDP
END
