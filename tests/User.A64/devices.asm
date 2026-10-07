#include "user_abi_a64.h"
; ARM64 device fixture, the port of tests/User.X64/devices.asm over the devices of ABI-1 (RFC 0011 v3 section 7.7,
; plan step K3.1): the device table as a read-only memory object, DEVICE_ACQUIRE over it and DEVICE_MEMORY of the
; acquired device's regions. The fixture knows what the kernel does not: the identity of the board's virtio block
; function (vendor 0x1AF4, device 0x1001) and that its first region is the function's configuration space, whose
; first words repeat that identity. The data page: the map request at 16, a channel request at 96, the handles to
; move at 768, the received handles at 800, the created endpoints at 832, the output of HANDLE_DUPLICATE at 1200,
; the table handle at 1296 and the status of a failed check at 1304. Registers: x22 the data page, x20 the table
; handle, x21 the test mode, x23 the mapped table, x24 the descriptor of the block function, x25 its index, x26 the
; device handle, x27 and x28 scratch handles and addresses.

#define VIRTIO_BLOCK 0x10011AF4
#define DESCRIPTOR_IDENTITY 8
#define DESCRIPTOR_REGION_COUNT 24
#define DESCRIPTOR_REGIONS 32
#define REGION_SIZE 24
#define REGION_FLAGS 16
#define READ_WRITE (WIT_MEMORY_READ + WIT_MEMORY_WRITE)
#define READ_EXECUTE (WIT_MEMORY_READ + WIT_MEMORY_EXECUTE)

    AREA |.text|, CODE, READONLY

    MACRO
    SYSCALL $number
    mov x8, #$number
    svc #0
    MEND

    MACRO
    EXPECT $status
    cmp x0, #$status
    b.ne failed
    MEND

    ; MEMORY_OBJECT_MAP: handle register, bytes, protection -> x0, x1 address (chosen by the kernel).
    MACRO
    MAPVIEW $handle, $bytes, $protection
    mov x0, $handle
    mov x2, #$bytes
    mov x4, #$protection
    bl object_map
    MEND

    ; DEVICE_ACQUIRE: table handle register, index register or immediate -> x0, x1 device handle.
    MACRO
    ACQUIRE $table, $index
    mov x0, $table
    mov x1, $index
    mov x2, #0
    SYSCALL WIT_CALL_DEVICE_ACQUIRE
    MEND

    ; DEVICE_MEMORY: device handle register, region -> x0, x1 object handle.
    MACRO
    REGION $device, $index
    mov x0, $device
    mov x1, $index
    mov x2, #0
    SYSCALL WIT_CALL_DEVICE_MEMORY
    MEND

    MACRO
    RELEASE $base
    mov x0, $base
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_MEMORY_RELEASE
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
    cmp x21, #WIT_DEVICE_TEST_AUTHORITY
    b.eq authority_test
    cmp x21, #WIT_DEVICE_TEST_TABLE
    b.ne failed

    ; The table is read-only: a writable view is refused by the handle's rights, and so is an executable one.
    MAPVIEW x20, 4096, READ_WRITE
    EXPECT WIT_STATUS_DENIED
    MAPVIEW x20, 4096, READ_EXECUTE
    EXPECT WIT_STATUS_DENIED
    ; The block function's first region is its configuration space; its words repeat the identity of the table.
    ldr w9, [x24, #DESCRIPTOR_REGION_COUNT]
    cbz w9, failed
    ldr w9, [x24, #(DESCRIPTOR_REGIONS + REGION_FLAGS)]
    tst w9, #WIT_DEVICE_REGION_CONFIG
    b.eq failed
    ACQUIRE x20, x25
    EXPECT WIT_STATUS_OK
    mov x26, x1
    REGION x26, #0
    EXPECT WIT_STATUS_OK
    mov x27, x1
    MAPVIEW x27, 4096, WIT_MEMORY_READ
    EXPECT WIT_STATUS_OK
    mov x28, x1
    ldr w9, [x28]
    ldr w10, =VIRTIO_BLOCK
    cmp w9, w10
    b.ne failed
    ldr w9, [x24, #(DESCRIPTOR_IDENTITY + 4)] ; class and revision
    ldr w10, [x28, #8]
    cmp w9, w10
    b.ne failed
    ; A writable view is within the region handle's rights; an executable one never is; the view is not committable.
    MAPVIEW x27, 4096, READ_WRITE
    EXPECT WIT_STATUS_OK
    str x1, [x22, #840]
    ldr w9, [x1]
    ldr w10, =VIRTIO_BLOCK
    cmp w9, w10
    b.ne failed
    ldr x9, [x22, #840]
    RELEASE x9
    EXPECT WIT_STATUS_OK
    MAPVIEW x27, 4096, READ_EXECUTE
    EXPECT WIT_STATUS_DENIED
    mov x0, x28
    mov x1, #4096
    mov x2, #READ_WRITE
    SYSCALL WIT_CALL_MEMORY_COMMIT
    EXPECT WIT_STATUS_DENIED
    mov x0, x28
    mov x1, #4096
    mov x2, #READ_EXECUTE
    SYSCALL WIT_CALL_MEMORY_PROTECT
    EXPECT WIT_STATUS_DENIED
    ; The region object outlives the device handle and the view outlives the object's handle; the device is free
    ; again only when every handle to it is gone.
    CLOSE x26
    EXPECT WIT_STATUS_OK
    ACQUIRE x20, x25
    EXPECT WIT_STATUS_OK
    mov x26, x1
    CLOSE x27
    EXPECT WIT_STATUS_OK
    ldr w9, [x28]
    ldr w10, =VIRTIO_BLOCK
    cmp w9, w10
    b.ne failed
    RELEASE x28
    EXPECT WIT_STATUS_OK
    CLOSE x26
    EXPECT WIT_STATUS_OK
    b passed

authority_test
    ; A table handle without ACQUIRE acquires nothing; indexes and reserved arguments are checked first.
    DUPLICATE x20, (WIT_RIGHT_MAP + WIT_RIGHT_QUERY)
    EXPECT WIT_STATUS_OK
    mov x27, x1
    ACQUIRE x27, x25
    EXPECT WIT_STATUS_DENIED
    CLOSE x27
    EXPECT WIT_STATUS_OK
    mov x9, #0xFFFF
    ACQUIRE x20, x9
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    mov x0, x20
    mov x1, x25
    mov x2, #1
    SYSCALL WIT_CALL_DEVICE_ACQUIRE
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    ; One holder at a time; a region index beyond the descriptor, a reserved argument and a port region are refused.
    ACQUIRE x20, x25
    EXPECT WIT_STATUS_OK
    mov x26, x1
    ACQUIRE x20, x25
    EXPECT WIT_STATUS_BUSY
    REGION x26, #99
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    mov x0, x26
    mov x1, #0
    mov x2, #1
    SYSCALL WIT_CALL_DEVICE_MEMORY
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    mov x27, #0
port_regions
    ldr w9, [x24, #DESCRIPTOR_REGION_COUNT]
    cmp x27, x9
    b.hs port_regions_done
    mov x10, #REGION_SIZE
    mul x10, x27, x10
    add x10, x10, x24
    ldr w9, [x10, #(DESCRIPTOR_REGIONS + REGION_FLAGS)]
    tst w9, #WIT_DEVICE_REGION_PORT
    b.eq port_regions_next
    REGION x26, x27
    EXPECT WIT_STATUS_UNSUPPORTED
port_regions_next
    add x27, x27, #1
    b port_regions
port_regions_done
    ; A device handle without BIND maps no region; the device stays held through it.
    DUPLICATE x26, WIT_RIGHT_QUERY
    EXPECT WIT_STATUS_OK
    mov x27, x1
    REGION x27, #0
    EXPECT WIT_STATUS_DENIED
    CLOSE x26
    EXPECT WIT_STATUS_OK
    ACQUIRE x20, x25
    EXPECT WIT_STATUS_BUSY
    CLOSE x27
    EXPECT WIT_STATUS_OK
    ; The device moves through a channel: in flight it is held, received it works, and a dropped message frees it.
    add x0, x22, #832
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_CHANNEL_CREATE
    EXPECT WIT_STATUS_OK
    ACQUIRE x20, x25
    EXPECT WIT_STATUS_OK
    mov x26, x1
    str x26, [x22, #768]
    ldr x0, [x22, #832]
    add x3, x22, #768
    bl channel_request
    SYSCALL WIT_CALL_CHANNEL_SEND
    EXPECT WIT_STATUS_OK
    REGION x26, #0
    EXPECT WIT_STATUS_BAD_HANDLE
    ACQUIRE x20, x25
    EXPECT WIT_STATUS_BUSY
    ldr x0, [x22, #840]
    add x3, x22, #800
    bl channel_request
    SYSCALL WIT_CALL_CHANNEL_RECEIVE
    EXPECT WIT_STATUS_OK
    ldr x26, [x22, #800]
    REGION x26, #0
    EXPECT WIT_STATUS_OK
    CLOSE x1
    EXPECT WIT_STATUS_OK
    str x26, [x22, #768]
    ldr x0, [x22, #832]
    add x3, x22, #768
    bl channel_request
    SYSCALL WIT_CALL_CHANNEL_SEND
    EXPECT WIT_STATUS_OK
    ldr x9, [x22, #840] ; the receiving endpoint: its queue is dropped with the device handle
    CLOSE x9
    EXPECT WIT_STATUS_OK
    ACQUIRE x20, x25
    EXPECT WIT_STATUS_OK
    CLOSE x1
    EXPECT WIT_STATUS_OK
    ldr x9, [x22, #832]
    CLOSE x9
    EXPECT WIT_STATUS_OK
    b passed

; Maps the table read-only into x23, checks its header and finds the block function: x24 its descriptor, x25 its
; index. The table handle is x20.
map_table
    mov x19, x30
    MAPVIEW x20, 4096, WIT_MEMORY_READ
    EXPECT WIT_STATUS_OK
    mov x23, x1
    ldr w9, [x23]
    cmp w9, #WIT_DEVICE_TABLE_VERSION
    b.ne failed
    ldr w9, [x23, #4]
    cmp w9, #WIT_DEVICE_TABLE_SIZE
    b.ne failed
    ldr w9, [x23, #12]
    cmp w9, #WIT_DEVICE_DESCRIPTOR_SIZE
    b.ne failed
    ldr w9, [x23, #8]
    cbz w9, failed
    cmp w9, #WIT_DEVICE_CAPACITY
    b.hi failed
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

; The channel request at data page + 96 for one handle at x3, no payload: x0 the endpoint, x1 the request, x2 its
; size, ready for CHANNEL_SEND or CHANNEL_RECEIVE.
channel_request
    add x9, x22, #96
    mov w10, #WIT_CHANNEL_MESSAGE_VERSION
    str w10, [x9]
    mov w10, #40
    str w10, [x9, #4]
    str xzr, [x9, #8]
    str x3, [x9, #16]
    str wzr, [x9, #24]
    mov w10, #1
    str w10, [x9, #28]
    str wzr, [x9, #32]
    str wzr, [x9, #36]
    mov x1, x9
    mov x2, #40
    ret

failed
    str x0, [x22, #1304] ; the status the failed check saw
    mov x0, #241
    b exit_process
passed
    RELEASE x23
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
