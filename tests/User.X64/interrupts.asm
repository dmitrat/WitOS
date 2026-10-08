option casemap:none
include user_abi.inc

; Interrupt and DMA fixture over the devices of ABI-1 (RFC 0011 v3 section 7.7, plan step K3.2): INTERRUPT_BIND of
; the virtio block function's line to an event, the binding's rights, uniqueness, acknowledgement and lifetime, and
; DMA_PIN of an anonymous object for the device with its ranges and refusals. The driver that uses both is the
; virtio fixture. The data page: the map request at 16, the pin request at 128, its ranges at 192 (8 entries), the
; handles of a scenario at 832, the output of HANDLE_DUPLICATE at 1200, the table handle at 1296, the status of a
; failed check at 1304, the number of checks passed at 1312 and the block function's index at 1320. Registers: rbx the data page, r15 the table handle,
; rsi the mapped table, rbp the descriptor of the block function, r13 its index, r12 the device handle, r14 and rdi
; scratch handles.

VIRTIO_BLOCK EQU 10011AF4h
DESCRIPTOR_IDENTITY EQU 8
DESCRIPTOR_LINE_COUNT EQU 28
READ_WRITE EQU WIT_MEMORY_READ + WIT_MEMORY_WRITE

EXPECT MACRO value
    inc QWORD PTR [rbx + 1312]
    cmp eax, value
    jne failed
ENDM
CALL0 MACRO operation
    mov eax, operation
    int 80h
ENDM
; MEMORY_OBJECT_MAP: handle, bytes, protection -> rax, rdx address.
MAP MACRO handle, bytes, protection
    mov rcx, handle
    mov r8, bytes
    mov r10d, protection
    call object_map
ENDM
ACQUIRE MACRO table, index
    mov rcx, table
    mov rdx, index
    xor r8d, r8d
    CALL0 WIT_CALL_DEVICE_ACQUIRE
ENDM
; INTERRUPT_BIND: device, line, event -> rax, rdx interrupt handle.
BIND MACRO device, line, event
    mov rcx, device
    mov rdx, line
    mov r8, event
    CALL0 WIT_CALL_INTERRUPT_BIND
ENDM
ACK MACRO handle
    mov rcx, handle
    xor edx, edx
    xor r8d, r8d
    CALL0 WIT_CALL_INTERRUPT_ACK
ENDM
CLOSE MACRO handle
    mov rcx, handle
    CALL0 WIT_CALL_HANDLE_CLOSE
ENDM
; HANDLE_DUPLICATE with rights -> rax, rdx the new handle.
DUPLICATE MACRO handle, rights
    mov rcx, handle
    mov edx, rights
    call duplicate_handle
ENDM
; DMA_PIN of the request at 128 as prepared: device, object, offset, bytes, capacity -> rax, rdx pin handle.
PIN MACRO device, object, offset, bytes, capacity
    mov rcx, device
    mov rdx, object
    mov r8, offset
    mov r9, bytes
    mov r10d, capacity
    call dma_pin
ENDM
UNPIN MACRO handle
    mov rcx, handle
    xor edx, edx
    xor r8d, r8d
    CALL0 WIT_CALL_DMA_UNPIN
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
    mov [rbx + 1320], r13 ; the index outlives r13
    ACQUIRE r15, r13
    EXPECT WIT_STATUS_OK
    mov r12, rdx
    cmp r14, WIT_INTERRUPT_TEST_DMA
    je dma_test
    cmp r14, WIT_INTERRUPT_TEST_BIND
    jne failed

    ; The block function has a line; an auto-reset event with WAIT and SIGNAL receives it.
    cmp DWORD PTR [rbp + DESCRIPTOR_LINE_COUNT], 1
    jb failed
    xor ecx, ecx
    xor edx, edx
    xor r8d, r8d
    CALL0 WIT_CALL_EVENT_CREATE
    EXPECT WIT_STATUS_OK
    mov r13, rdx
    BIND r12, 0, r13
    EXPECT WIT_STATUS_OK
    mov r14, rdx
    ; One binding per line; a line beyond the descriptor; an event without SIGNAL; a device without BIND.
    BIND r12, 0, r13
    EXPECT WIT_STATUS_BUSY
    BIND r12, 5, r13
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    DUPLICATE r13, WIT_RIGHT_WAIT
    EXPECT WIT_STATUS_OK
    mov rdi, rdx
    BIND r12, 0, rdi
    EXPECT WIT_STATUS_DENIED
    CLOSE rdi
    EXPECT WIT_STATUS_OK
    DUPLICATE r12, WIT_RIGHT_QUERY
    EXPECT WIT_STATUS_OK
    mov rdi, rdx
    BIND rdi, 0, r13
    EXPECT WIT_STATUS_DENIED
    CLOSE rdi
    EXPECT WIT_STATUS_OK
    ; Acknowledgement needs ACK and no extra arguments; the line can be acknowledged when nothing is pending.
    ACK r14
    EXPECT WIT_STATUS_OK
    mov rcx, r14
    mov edx, 1
    xor r8d, r8d
    CALL0 WIT_CALL_INTERRUPT_ACK
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    DUPLICATE r14, WIT_RIGHT_QUERY
    EXPECT WIT_STATUS_OK
    mov rdi, rdx
    ACK rdi
    EXPECT WIT_STATUS_DENIED
    CLOSE rdi
    EXPECT WIT_STATUS_OK
    ; The binding holds the event and the device: both handles can go, the device stays acquired until the binding
    ; ends with its last handle.
    CLOSE r13
    EXPECT WIT_STATUS_OK
    CLOSE r12
    EXPECT WIT_STATUS_OK
    ACQUIRE r15, QWORD PTR [rbx + 1320]
    EXPECT WIT_STATUS_BUSY
    CLOSE r14
    EXPECT WIT_STATUS_OK
    ACQUIRE r15, QWORD PTR [rbx + 1320]
    EXPECT WIT_STATUS_OK
    CLOSE rdx
    EXPECT WIT_STATUS_OK
    jmp passed

dma_test:
    ; Two pages of an anonymous object pinned for the device: the ranges cover them, page-aligned, and end with a
    ; zero entry.
    mov ecx, 8192
    xor edx, edx
    xor r8d, r8d
    CALL0 WIT_CALL_MEMORY_OBJECT_CREATE
    EXPECT WIT_STATUS_OK
    mov r13, rdx
    PIN r12, r13, 0, 8192, 8
    EXPECT WIT_STATUS_OK
    mov rdi, rdx
    mov rax, [rbx + 192 + 8] ; the first range's size
    add rax, [rbx + 192 + 24] ; plus the second's, zero when the pages are contiguous
    cmp rax, 8192
    jne failed
    mov rax, [rbx + 192]
    test rax, rax
    je failed
    test eax, 4095
    jne failed
    cmp QWORD PTR [rbx + 192 + 40], 0 ; the third entry is empty
    jne failed
    ; Refusals: a window beyond the object, no capacity, a foreign version, a wrong size, a device region as the
    ; object, a device without BIND, an object without MAP.
    PIN r12, r13, 0, 12288, 8
    EXPECT WIT_STATUS_TOO_LARGE
    PIN r12, r13, 4096, 8192, 8
    EXPECT WIT_STATUS_TOO_LARGE
    PIN r12, r13, 0, 4096, 0
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    mov DWORD PTR [rbx + 128], 2
    call raw_pin
    EXPECT WIT_STATUS_UNSUPPORTED
    mov DWORD PTR [rbx + 128], WIT_DMA_PIN_VERSION
    mov edx, 55
    call raw_pin_sized
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    mov rcx, r12
    xor edx, edx
    xor r8d, r8d
    CALL0 WIT_CALL_DEVICE_MEMORY
    EXPECT WIT_STATUS_OK
    mov r14, rdx
    PIN r12, r14, 0, 4096, 8
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    CLOSE r14
    EXPECT WIT_STATUS_OK
    DUPLICATE r12, WIT_RIGHT_QUERY
    EXPECT WIT_STATUS_OK
    mov r14, rdx
    PIN r14, r13, 0, 4096, 8
    EXPECT WIT_STATUS_DENIED
    CLOSE r14
    EXPECT WIT_STATUS_OK
    DUPLICATE r13, WIT_RIGHT_QUERY
    EXPECT WIT_STATUS_OK
    mov r14, rdx
    PIN r12, r14, 0, 4096, 8
    EXPECT WIT_STATUS_DENIED
    CLOSE r14
    EXPECT WIT_STATUS_OK
    ; The pin ends with its handle; a second pin of the same pages is fine while the first lives.
    PIN r12, r13, 4096, 4096, 8
    EXPECT WIT_STATUS_OK
    mov r14, rdx
    UNPIN rdi
    EXPECT WIT_STATUS_OK
    UNPIN rdi
    EXPECT WIT_STATUS_BAD_HANDLE
    CLOSE r13 ; the object lives on by the second pin
    EXPECT WIT_STATUS_OK
    UNPIN r14
    EXPECT WIT_STATUS_OK
    CLOSE r12
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
    cmp DWORD PTR [rsi + 12], WIT_DEVICE_DESCRIPTOR_SIZE
    jne failed
    mov eax, [rsi + 8]
    test eax, eax
    je failed
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

; The pin request at data page + 128: rcx device, rdx object, r8 offset, r9 bytes, r10d capacity; ranges at 192.
dma_pin:
    lea rax, [rbx + 128]
    mov DWORD PTR [rax], WIT_DMA_PIN_VERSION
    mov DWORD PTR [rax + 4], 56
    mov [rax + 8], rcx
    mov [rax + 16], rdx
    mov [rax + 24], r8
    mov [rax + 32], r9
    lea rcx, [rbx + 192]
    mov [rax + 40], rcx
    mov [rax + 48], r10d
    mov DWORD PTR [rax + 52], 0
raw_pin:
    mov edx, 56
raw_pin_sized:
    lea rcx, [rbx + 128]
    xor r8d, r8d
    CALL0 WIT_CALL_DMA_PIN
    ret

; HANDLE_DUPLICATE: rcx source, edx rights -> rax, rdx the new handle.
duplicate_handle:
    mov r8d, edx
    lea rdx, [rbx + 1200]
    CALL0 WIT_CALL_HANDLE_DUPLICATE
    mov rdx, [rbx + 1200]
    ret

failed:
    mov [rbx + 1304], rax ; the status the failed check saw
    mov ecx, 241
    jmp exit_process
passed:
    mov rcx, rsi
    xor edx, edx
    xor r8d, r8d
    CALL0 WIT_CALL_MEMORY_RELEASE
    EXPECT WIT_STATUS_OK
    CLOSE QWORD PTR [rbx + 1296]
    EXPECT WIT_STATUS_OK
    mov ecx, WIT_TEST_EXIT_CODE
exit_process:
    CALL0 WIT_CALL_PROCESS_EXIT
    ud2
wit_user_start ENDP
END
