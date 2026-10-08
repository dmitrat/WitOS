#include "user_abi_a64.h"
; ARM64 interrupt and DMA fixture, the port of tests/User.X64/interrupts.asm over the devices of ABI-1 (RFC 0011
; v3 section 7.7, plan step K3.2): INTERRUPT_BIND of the virtio block function's line to an event, the binding's
; rights, uniqueness, acknowledgement and lifetime, and DMA_PIN of an anonymous object for the device with its
; ranges and refusals. The data page: the map request at 16, the pin request at 128, its ranges at 192, the output
; of HANDLE_DUPLICATE at 1200, the table handle at 1296, the status of a failed check at 1304 and the number of
; checks passed at 1312. Registers: x22 the data page, x20 the table handle, x21 the test mode, x23 the mapped
; table, x24 the descriptor of the block function, x25 its index, x26 the device handle, x27 and x28 scratch handles.

#define VIRTIO_BLOCK 0x10011AF4
#define DESCRIPTOR_IDENTITY 8
#define DESCRIPTOR_LINE_COUNT 28
#define READ_WRITE (WIT_MEMORY_READ + WIT_MEMORY_WRITE)

    AREA |.text|, CODE, READONLY

    MACRO
    SYSCALL $number
    mov x8, #$number
    svc #0
    MEND

    MACRO
    EXPECT $status
    ldr x9, [x22, #1312]
    add x9, x9, #1
    str x9, [x22, #1312]
    cmp x0, #$status
    b.ne failed
    MEND

    ; MEMORY_OBJECT_MAP: handle register, bytes, protection -> x0, x1 address.
    MACRO
    MAPVIEW $handle, $bytes, $protection
    mov x0, $handle
    mov x2, #$bytes
    mov x4, #$protection
    bl object_map
    MEND

    MACRO
    ACQUIRE $table, $index
    mov x0, $table
    mov x1, $index
    mov x2, #0
    SYSCALL WIT_CALL_DEVICE_ACQUIRE
    MEND

    ; INTERRUPT_BIND: device, line, event -> x0, x1 interrupt handle.
    MACRO
    BIND $device, $line, $event
    mov x0, $device
    mov x1, #$line
    mov x2, $event
    SYSCALL WIT_CALL_INTERRUPT_BIND
    MEND

    MACRO
    ACK $handle
    mov x0, $handle
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_INTERRUPT_ACK
    MEND

    MACRO
    CLOSE $handle
    mov x0, $handle
    SYSCALL WIT_CALL_HANDLE_CLOSE
    MEND

    ; HANDLE_DUPLICATE with rights -> x0, x1 the new handle.
    MACRO
    DUPLICATE $handle, $rights
    mov x0, $handle
    add x1, x22, #1200
    mov x2, #$rights
    SYSCALL WIT_CALL_HANDLE_DUPLICATE
    ldr x1, [x22, #1200]
    MEND

    ; DMA_PIN: device, object, offset, bytes, capacity -> x0, x1 pin handle.
    MACRO
    PIN $device, $object, $offset, $bytes, $capacity
    mov x0, $device
    mov x1, $object
    mov x2, #$offset
    mov x3, #$bytes
    mov x4, #$capacity
    bl dma_pin
    MEND

    MACRO
    UNPIN $handle
    mov x0, $handle
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_DMA_UNPIN
    MEND

    EXPORT wit_user_start
wit_user_start PROC
    ldr x22, =WIT_USER_DATA
    ldr w9, [x0]
    cmp w9, #WIT_ABI_VERSION
    b.ne failed
    ldr x21, [x0, #WIT_TEST_MODE_OFFSET]
    ldr x20, [x0, #WIT_TEST_TABLE_OFFSET]
    str x20, [x22, #1296]
    bl map_table ; x23 the table, x24 the block function's descriptor, x25 its index
    ACQUIRE x20, x25
    EXPECT WIT_STATUS_OK
    mov x26, x1
    cmp x21, #WIT_INTERRUPT_TEST_DMA
    b.eq dma_test
    cmp x21, #WIT_INTERRUPT_TEST_BIND
    b.ne failed

    ; The block function has a line; an auto-reset event with WAIT and SIGNAL receives it.
    ldr w9, [x24, #DESCRIPTOR_LINE_COUNT]
    cbz w9, failed
    mov x0, #0
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_EVENT_CREATE
    EXPECT WIT_STATUS_OK
    mov x25, x1
    BIND x26, 0, x25
    EXPECT WIT_STATUS_OK
    mov x27, x1
    ; One binding per line; a line beyond the descriptor; an event without SIGNAL; a device without BIND.
    BIND x26, 0, x25
    EXPECT WIT_STATUS_BUSY
    BIND x26, 5, x25
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    DUPLICATE x25, WIT_RIGHT_WAIT
    EXPECT WIT_STATUS_OK
    mov x28, x1
    BIND x26, 0, x28
    EXPECT WIT_STATUS_DENIED
    CLOSE x28
    EXPECT WIT_STATUS_OK
    DUPLICATE x26, WIT_RIGHT_QUERY
    EXPECT WIT_STATUS_OK
    mov x28, x1
    BIND x28, 0, x25
    EXPECT WIT_STATUS_DENIED
    CLOSE x28
    EXPECT WIT_STATUS_OK
    ; Acknowledgement needs ACK and no extra arguments; the line can be acknowledged when nothing is pending.
    ACK x27
    EXPECT WIT_STATUS_OK
    mov x0, x27
    mov x1, #1
    mov x2, #0
    SYSCALL WIT_CALL_INTERRUPT_ACK
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    DUPLICATE x27, WIT_RIGHT_QUERY
    EXPECT WIT_STATUS_OK
    mov x28, x1
    ACK x28
    EXPECT WIT_STATUS_DENIED
    CLOSE x28
    EXPECT WIT_STATUS_OK
    ; The binding holds the event and the device: both handles can go, the device stays acquired until the binding
    ; ends with its last handle.
    CLOSE x25
    EXPECT WIT_STATUS_OK
    CLOSE x26
    EXPECT WIT_STATUS_OK
    ldr x25, [x22, #1320] ; the index, kept by map_table
    ACQUIRE x20, x25
    EXPECT WIT_STATUS_BUSY
    CLOSE x27
    EXPECT WIT_STATUS_OK
    ACQUIRE x20, x25
    EXPECT WIT_STATUS_OK
    CLOSE x1
    EXPECT WIT_STATUS_OK
    b passed

dma_test
    ; Two pages of an anonymous object pinned for the device: the ranges cover them, page-aligned, and end with a
    ; zero entry.
    mov x0, #8192
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_MEMORY_OBJECT_CREATE
    EXPECT WIT_STATUS_OK
    mov x25, x1
    PIN x26, x25, 0, 8192, 8
    EXPECT WIT_STATUS_OK
    mov x28, x1
    ldr x9, [x22, #(192 + 8)] ; the first range's size
    ldr x10, [x22, #(192 + 24)] ; plus the second's, zero when the pages are contiguous
    add x9, x9, x10
    mov x10, #8192
    cmp x9, x10
    b.ne failed
    ldr x9, [x22, #192]
    cbz x9, failed
    tst x9, #4095
    b.ne failed
    ldr x9, [x22, #(192 + 40)] ; the third entry is empty
    cbnz x9, failed
    ; Refusals: a window beyond the object, no capacity, a foreign version, a wrong size, a device region as the
    ; object, a device without BIND, an object without MAP.
    PIN x26, x25, 0, 12288, 8
    EXPECT WIT_STATUS_TOO_LARGE
    PIN x26, x25, 4096, 8192, 8
    EXPECT WIT_STATUS_TOO_LARGE
    PIN x26, x25, 0, 4096, 0
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    mov w9, #2
    str w9, [x22, #128]
    mov x1, #56
    bl raw_pin
    EXPECT WIT_STATUS_UNSUPPORTED
    mov w9, #WIT_DMA_PIN_VERSION
    str w9, [x22, #128]
    mov x1, #55
    bl raw_pin
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    mov x0, x26
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_DEVICE_MEMORY
    EXPECT WIT_STATUS_OK
    mov x27, x1
    PIN x26, x27, 0, 4096, 8
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    CLOSE x27
    EXPECT WIT_STATUS_OK
    DUPLICATE x26, WIT_RIGHT_QUERY
    EXPECT WIT_STATUS_OK
    mov x27, x1
    PIN x27, x25, 0, 4096, 8
    EXPECT WIT_STATUS_DENIED
    CLOSE x27
    EXPECT WIT_STATUS_OK
    DUPLICATE x25, WIT_RIGHT_QUERY
    EXPECT WIT_STATUS_OK
    mov x27, x1
    PIN x26, x27, 0, 4096, 8
    EXPECT WIT_STATUS_DENIED
    CLOSE x27
    EXPECT WIT_STATUS_OK
    ; The pin ends with its handle; a second pin of the same pages is fine while the first lives.
    PIN x26, x25, 4096, 4096, 8
    EXPECT WIT_STATUS_OK
    mov x27, x1
    UNPIN x28
    EXPECT WIT_STATUS_OK
    UNPIN x28
    EXPECT WIT_STATUS_BAD_HANDLE
    CLOSE x25 ; the object lives on by the second pin
    EXPECT WIT_STATUS_OK
    UNPIN x27
    EXPECT WIT_STATUS_OK
    CLOSE x26
    EXPECT WIT_STATUS_OK
    b passed

; Maps the table read-only into x23, checks its header and finds the block function: x24 its descriptor, x25 its
; index (also kept at 1320). The table handle is x20.
map_table
    mov x19, x30
    MAPVIEW x20, 4096, WIT_MEMORY_READ
    EXPECT WIT_STATUS_OK
    mov x23, x1
    ldr w9, [x23]
    cmp w9, #WIT_DEVICE_TABLE_VERSION
    b.ne failed
    ldr w9, [x23, #12]
    cmp w9, #WIT_DEVICE_DESCRIPTOR_SIZE
    b.ne failed
    ldr w9, [x23, #8]
    cbz w9, failed
    mov x25, #0
    add x24, x23, #WIT_DEVICE_TABLE_SIZE
find_device
    ldr w10, [x24, #DESCRIPTOR_IDENTITY]
    ldr w11, =VIRTIO_BLOCK
    cmp w10, w11
    b.eq found_device
    add x24, x24, #WIT_DEVICE_DESCRIPTOR_SIZE
    add x25, x25, #1
    cmp x25, x9
    b.lo find_device
    b failed
found_device
    str x25, [x22, #1320]
    mov x30, x19
    ret

; The map request at data page + 16: x0 object, x2 bytes, x4 protection; offset 0, address chosen.
object_map
    add x9, x22, #16
    mov w10, #WIT_MEMORY_MAP_VERSION
    str w10, [x9]
    mov w10, #56
    str w10, [x9, #4]
    str x0, [x9, #8]
    str xzr, [x9, #16]
    str x2, [x9, #24]
    str xzr, [x9, #32]
    str w4, [x9, #40]
    str wzr, [x9, #44]
    mov x10, #-3 ; WIT_PROCESS_SELF
    str x10, [x9, #48]
    mov x0, x9
    mov x1, #56
    mov x2, #0
    SYSCALL WIT_CALL_MEMORY_OBJECT_MAP
    ret

; The pin request at data page + 128: x0 device, x1 object, x2 offset, x3 bytes, x4 capacity; ranges at 192.
dma_pin
    add x9, x22, #128
    mov w10, #WIT_DMA_PIN_VERSION
    str w10, [x9]
    mov w10, #56
    str w10, [x9, #4]
    str x0, [x9, #8]
    str x1, [x9, #16]
    str x2, [x9, #24]
    str x3, [x9, #32]
    add x10, x22, #192
    str x10, [x9, #40]
    str w4, [x9, #48]
    str wzr, [x9, #52]
    mov x1, #56
; DMA_PIN of the request as it lies at data page + 128, with the size in x1.
raw_pin
    add x0, x22, #128
    mov x2, #0
    SYSCALL WIT_CALL_DMA_PIN
    ret

failed
    str x0, [x22, #1304] ; the status the failed check saw
    mov x0, #241
    b exit_process
passed
    mov x0, x23
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_MEMORY_RELEASE
    EXPECT WIT_STATUS_OK
    ldr x9, [x22, #1296]
    CLOSE x9
    EXPECT WIT_STATUS_OK
    mov x0, #WIT_TEST_EXIT_CODE
exit_process
    SYSCALL WIT_CALL_PROCESS_EXIT
    DCD 0x00000000 ; UDF #0
    LTORG
    ENDP
    END
