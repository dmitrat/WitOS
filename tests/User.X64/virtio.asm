option casemap:none
include user_abi.inc

; Virtio block fixture (plan step K3.2): a user-space driver over the modern virtio PCI transport of QEMU's
; virtio-blk-pci, built on the devices of ABI-1 alone: the device table, DEVICE_ACQUIRE, DEVICE_MEMORY of the
; configuration page and of the modern BAR, DMA_PIN of the queue and request pages, INTERRUPT_BIND of the INTx line
; to an event and INTERRUPT_ACK. It reads sector 0 of the boot disk and checks the FAT boot sector the tool wrote
; (the signature AA55h and the FAT16 type). The kernel knows none of this; the fixture holds all of it. The data
; page: the map request at 16, the pin request at 128, its ranges at 192, the capabilities (BAR, offset) at 256,
; the notify multiplier at 288, the queue notify offset at 296, the physical pages at 320, the wait request at
; 400, the handles at 840, the pins at 896, the table mapping at 944, the output of HANDLE_DUPLICATE at 1200, the
; table handle at 1296, the status of a failed check at 1304 and the number of checks passed at 1312. Registers:
; rbx the data page, r15 the table handle, rbp the descriptor, r13 its index, r12 the device handle, r14 the
; mapped BAR, rsi the common configuration, rdi the mapped DMA object.

VIRTIO_BLOCK EQU 10011AF4h
DESCRIPTOR_IDENTITY EQU 8
DESCRIPTOR_REGION_COUNT EQU 24
DESCRIPTOR_REGIONS EQU 32
REGION_SIZE EQU 24
READ_WRITE EQU WIT_MEMORY_READ + WIT_MEMORY_WRITE
CAP_COMMON EQU 256
CAP_NOTIFY EQU 264
CAP_ISR EQU 272
CAP_DEVICE EQU 280
PHYSICAL EQU 320
H_REGION0 EQU 840
H_BAR EQU 848
CONFIG_VIEW EQU 856
H_OBJECT EQU 864
H_EVENT EQU 872
H_INTERRUPT EQU 880
H_PINS EQU 896
TABLE_VIEW EQU 944
QUEUE_SIZE EQU 8

EXPECT MACRO value
    inc QWORD PTR [rbx + 1312]
    cmp eax, value
    jne failed
ENDM
CALL0 MACRO operation
    mov eax, operation
    int 80h
ENDM
MAP MACRO handle, bytes, protection
    mov rcx, handle
    mov r8, bytes
    mov r10d, protection
    call object_map
ENDM
CLOSE MACRO handle
    mov rcx, handle
    CALL0 WIT_CALL_HANDLE_CLOSE
ENDM
RELEASE MACRO base
    mov rcx, base
    xor edx, edx
    xor r8d, r8d
    CALL0 WIT_CALL_MEMORY_RELEASE
ENDM
; DEVICE_MEMORY of region edx -> rax, rdx handle.
REGION MACRO index
    mov rdx, index
    mov rcx, r12
    xor r8d, r8d
    CALL0 WIT_CALL_DEVICE_MEMORY
ENDM

.code
PUBLIC wit_user_start
wit_user_start PROC
    mov rbx, WIT_USER_DATA
    cmp DWORD PTR [rcx], WIT_ABI_VERSION
    jne failed
    cmp QWORD PTR [rcx + WIT_TEST_MODE_OFFSET], WIT_INTERRUPT_TEST_VIRTIO
    jne failed
    mov r15, [rcx + WIT_TEST_TABLE_OFFSET]
    mov [rbx + 1296], r15
    call map_table
    mov rcx, r15
    mov rdx, r13
    xor r8d, r8d
    CALL0 WIT_CALL_DEVICE_ACQUIRE
    EXPECT WIT_STATUS_OK
    mov r12, rdx
    ; The configuration page, writable: memory decoding and bus mastering on, then the vendor capabilities.
    REGION 0
    EXPECT WIT_STATUS_OK
    mov [rbx + H_REGION0], rdx
    MAP rdx, 4096, READ_WRITE
    EXPECT WIT_STATUS_OK
    mov [rbx + CONFIG_VIEW], rdx
    mov r14, rdx
    or WORD PTR [r14 + 4], 6
    test BYTE PTR [r14 + 6], 10h
    je failed
    movzx ecx, BYTE PTR [r14 + 34h]
    xor edx, edx ; the types found
capabilities:
    test ecx, ecx
    je capabilities_done
    cmp BYTE PTR [r14 + rcx], 9 ; a vendor capability
    jne capability_next
    movzx eax, BYTE PTR [r14 + rcx + 3] ; cfg_type 1 common, 2 notify, 3 ISR, 4 device
    cmp eax, 1
    jb capability_next
    cmp eax, 4
    ja capability_next
    bts edx, eax
    lea r8, [rbx + CAP_COMMON - 8 + rax * 8]
    movzx r9d, BYTE PTR [r14 + rcx + 4]
    mov [r8], r9d
    mov r9d, [r14 + rcx + 8]
    mov [r8 + 4], r9d
    cmp eax, 2
    jne capability_next
    mov r9d, [r14 + rcx + 16]
    mov [rbx + 288], r9d ; notify_off_multiplier
capability_next:
    movzx ecx, BYTE PTR [r14 + rcx + 1]
    jmp capabilities
capabilities_done:
    cmp edx, 1Eh
    jne failed
    mov eax, [rbx + CAP_COMMON]
    cmp eax, [rbx + CAP_NOTIFY]
    jne failed
    cmp eax, [rbx + CAP_ISR]
    jne failed
    ; The BAR the capabilities name, found among the descriptor's regions by its firmware-assigned base.
    mov eax, [r14 + 10h + rax * 4]
    mov edx, eax
    and eax, 0FFFFFFF0h
    and edx, 6
    cmp edx, 4
    jne bar_found
    mov edx, [rbx + CAP_COMMON]
    mov edx, [r14 + 14h + rdx * 4]
    shl rdx, 32
    or rax, rdx
bar_found:
    mov ecx, 1
regions:
    cmp ecx, [rbp + DESCRIPTOR_REGION_COUNT]
    jae failed
    imul edx, ecx, REGION_SIZE
    cmp [rbp + DESCRIPTOR_REGIONS + rdx], rax
    je region_found
    inc ecx
    jmp regions
region_found:
    mov r8, [rbp + DESCRIPTOR_REGIONS + rdx + 8] ; the region's size
    mov [rbx + 304], r8
    REGION rcx
    EXPECT WIT_STATUS_OK
    mov [rbx + H_BAR], rdx
    MAP rdx, QWORD PTR [rbx + 304], READ_WRITE
    EXPECT WIT_STATUS_OK
    mov r14, rdx
    ; Five pages for the descriptors, the available and used rings, the request and the data, pinned one by one.
    mov ecx, 5 * 4096
    xor edx, edx
    xor r8d, r8d
    CALL0 WIT_CALL_MEMORY_OBJECT_CREATE
    EXPECT WIT_STATUS_OK
    mov [rbx + H_OBJECT], rdx
    MAP rdx, 5 * 4096, READ_WRITE
    EXPECT WIT_STATUS_OK
    mov rdi, rdx
    xor r13d, r13d
pins:
    mov rcx, r13
    shl rcx, 12
    call dma_pin
    EXPECT WIT_STATUS_OK
    mov [rbx + H_PINS + r13 * 8], rdx
    mov rax, [rbx + 192]
    mov [rbx + PHYSICAL + r13 * 8], rax
    inc r13d
    cmp r13d, 5
    jb pins
    ; The common configuration: reset, acknowledge, VERSION_1 alone, FEATURES_OK, queue 0 of eight entries on the
    ; pinned pages, DRIVER_OK.
    mov esi, [rbx + CAP_COMMON + 4]
    add rsi, r14
    mov BYTE PTR [rsi + 20], 0
reset_wait:
    cmp BYTE PTR [rsi + 20], 0
    jne reset_wait
    mov BYTE PTR [rsi + 20], 3
    mov DWORD PTR [rsi], 1
    test DWORD PTR [rsi + 4], 1 ; VIRTIO_F_VERSION_1
    je failed
    mov DWORD PTR [rsi + 8], 1
    mov DWORD PTR [rsi + 12], 1
    mov DWORD PTR [rsi + 8], 0
    mov DWORD PTR [rsi + 12], 0
    mov BYTE PTR [rsi + 20], 0Bh
    test BYTE PTR [rsi + 20], 8
    je failed
    mov WORD PTR [rsi + 22], 0
    cmp WORD PTR [rsi + 24], QUEUE_SIZE
    jb failed
    mov WORD PTR [rsi + 24], QUEUE_SIZE
    mov rax, [rbx + PHYSICAL]
    mov [rsi + 32], rax
    mov rax, [rbx + PHYSICAL + 8]
    mov [rsi + 40], rax
    mov rax, [rbx + PHYSICAL + 16]
    mov [rsi + 48], rax
    movzx eax, WORD PTR [rsi + 30]
    mov [rbx + 296], eax
    mov WORD PTR [rsi + 28], 1
    mov BYTE PTR [rsi + 20], 0Fh
    ; The line to an auto-reset event.
    xor ecx, ecx
    xor edx, edx
    xor r8d, r8d
    CALL0 WIT_CALL_EVENT_CREATE
    EXPECT WIT_STATUS_OK
    mov [rbx + H_EVENT], rdx
    mov rcx, r12
    xor edx, edx
    mov r8, [rbx + H_EVENT]
    CALL0 WIT_CALL_INTERRUPT_BIND
    EXPECT WIT_STATUS_OK
    mov [rbx + H_INTERRUPT], rdx
    ; One request: read sector 0 into the data page through three descriptors, then notify the queue.
    mov DWORD PTR [rdi + 3 * 4096], 0 ; VIRTIO_BLK_T_IN
    mov DWORD PTR [rdi + 3 * 4096 + 4], 0
    mov QWORD PTR [rdi + 3 * 4096 + 8], 0 ; sector 0
    mov BYTE PTR [rdi + 3 * 4096 + 512], 0FFh
    mov rax, [rbx + PHYSICAL + 24]
    mov [rdi], rax
    mov DWORD PTR [rdi + 8], 16
    mov WORD PTR [rdi + 12], 1 ; NEXT
    mov WORD PTR [rdi + 14], 1
    mov rax, [rbx + PHYSICAL + 32]
    mov [rdi + 16], rax
    mov DWORD PTR [rdi + 24], 512
    mov WORD PTR [rdi + 28], 3 ; NEXT | WRITE
    mov WORD PTR [rdi + 30], 2
    mov rax, [rbx + PHYSICAL + 24]
    add rax, 512
    mov [rdi + 32], rax
    mov DWORD PTR [rdi + 40], 1
    mov WORD PTR [rdi + 44], 2 ; WRITE
    mov WORD PTR [rdi + 46], 0
    mov WORD PTR [rdi + 4096], 0
    mov WORD PTR [rdi + 4096 + 4], 0
    mfence
    mov WORD PTR [rdi + 4096 + 2], 1
    mfence
    mov eax, [rbx + 296]
    imul eax, [rbx + 288]
    add eax, [rbx + CAP_NOTIFY + 4]
    mov WORD PTR [r14 + rax], 0
    ; The completion arrives as the bound interrupt; the ISR register says so and the used ring holds the request.
    mov rcx, [rbx + H_EVENT]
    call wait_object
    EXPECT WIT_STATUS_OK
    mov eax, [rbx + CAP_ISR + 4]
    movzx eax, BYTE PTR [r14 + rax]
    test eax, 1
    je failed
    cmp WORD PTR [rdi + 2 * 4096 + 2], 1
    jne failed
    cmp DWORD PTR [rdi + 2 * 4096 + 4], 0
    jne failed
    cmp BYTE PTR [rdi + 3 * 4096 + 512], 0
    jne failed
    cmp WORD PTR [rdi + 4 * 4096 + 510], 0AA55h
    jne failed
    cmp DWORD PTR [rdi + 4 * 4096 + 54], 31544146h ; "FAT1"
    jne failed
    mov rcx, [rbx + H_INTERRUPT]
    xor edx, edx
    xor r8d, r8d
    CALL0 WIT_CALL_INTERRUPT_ACK
    EXPECT WIT_STATUS_OK
    ; Quiesce the device, then give everything back.
    mov BYTE PTR [rsi + 20], 0
    CLOSE QWORD PTR [rbx + H_INTERRUPT]
    EXPECT WIT_STATUS_OK
    CLOSE QWORD PTR [rbx + H_EVENT]
    EXPECT WIT_STATUS_OK
    xor r13d, r13d
unpins:
    mov rcx, [rbx + H_PINS + r13 * 8]
    xor edx, edx
    xor r8d, r8d
    CALL0 WIT_CALL_DMA_UNPIN
    EXPECT WIT_STATUS_OK
    inc r13d
    cmp r13d, 5
    jb unpins
    RELEASE rdi
    EXPECT WIT_STATUS_OK
    CLOSE QWORD PTR [rbx + H_OBJECT]
    EXPECT WIT_STATUS_OK
    RELEASE r14
    EXPECT WIT_STATUS_OK
    RELEASE QWORD PTR [rbx + CONFIG_VIEW]
    EXPECT WIT_STATUS_OK
    CLOSE QWORD PTR [rbx + H_BAR]
    EXPECT WIT_STATUS_OK
    CLOSE QWORD PTR [rbx + H_REGION0]
    EXPECT WIT_STATUS_OK
    CLOSE r12
    EXPECT WIT_STATUS_OK
    RELEASE QWORD PTR [rbx + TABLE_VIEW]
    EXPECT WIT_STATUS_OK
    CLOSE QWORD PTR [rbx + 1296]
    EXPECT WIT_STATUS_OK
    mov ecx, WIT_TEST_EXIT_CODE
    jmp exit_process

; Maps the table read-only and finds the block function: rbp its descriptor, r13 its index.
map_table:
    MAP r15, 4096, WIT_MEMORY_READ
    EXPECT WIT_STATUS_OK
    mov [rbx + TABLE_VIEW], rdx
    mov eax, [rdx + 8]
    test eax, eax
    je failed
    xor r13d, r13d
    lea rbp, [rdx + WIT_DEVICE_TABLE_SIZE]
find_device:
    cmp DWORD PTR [rbp + DESCRIPTOR_IDENTITY], VIRTIO_BLOCK
    je found_device
    add rbp, WIT_DEVICE_DESCRIPTOR_SIZE
    inc r13d
    cmp r13d, [rdx + 8]
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

; DMA_PIN of one page at offset rcx of the object for the device; the range lands at 192.
dma_pin:
    lea rax, [rbx + 128]
    mov DWORD PTR [rax], WIT_DMA_PIN_VERSION
    mov DWORD PTR [rax + 4], 56
    mov [rax + 8], r12
    mov rdx, [rbx + H_OBJECT]
    mov [rax + 16], rdx
    mov [rax + 24], rcx
    mov QWORD PTR [rax + 32], 4096
    lea rcx, [rbx + 192]
    mov [rax + 40], rcx
    mov DWORD PTR [rax + 48], 2
    mov DWORD PTR [rax + 52], 0
    lea rcx, [rbx + 128]
    mov edx, 56
    xor r8d, r8d
    CALL0 WIT_CALL_DMA_PIN
    ret

; OBJECT_WAIT on one handle rcx without a deadline: rax status.
wait_object:
    lea rax, [rbx + 400]
    mov [rax + 40], rcx
    lea r9, [rax + 40]
    mov DWORD PTR [rax], WIT_WAIT_OBJECTS_VERSION
    mov DWORD PTR [rax + 4], 32
    mov [rax + 8], r9
    mov DWORD PTR [rax + 16], 1
    mov DWORD PTR [rax + 20], 0
    mov rcx, WIT_WAIT_INFINITE
    mov [rax + 24], rcx
    mov rcx, rax
    mov edx, 32
    xor r8d, r8d
    CALL0 WIT_CALL_OBJECT_WAIT
    ret

failed:
    mov [rbx + 1304], rax ; the status the failed check saw
    mov ecx, 241
exit_process:
    CALL0 WIT_CALL_PROCESS_EXIT
    ud2
wit_user_start ENDP
END
