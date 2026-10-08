#include "user_abi_a64.h"
; ARM64 virtio block fixture, the port of tests/User.X64/virtio.asm (plan step K3.2): a user-space driver over the
; modern virtio PCI transport of QEMU's virtio-blk-pci, built on the devices of ABI-1 alone: the device table,
; DEVICE_ACQUIRE, DEVICE_MEMORY of the configuration page and of the modern BAR, DMA_PIN of the queue and request
; pages, INTERRUPT_BIND of the INTx line to an event and INTERRUPT_ACK. It reads sector 0 of the boot disk and
; checks the FAT boot sector the tool wrote. The data page: the map request at 16, the pin request at 128, its
; ranges at 192, the capabilities (BAR, offset) at 256, the notify multiplier at 288, the queue notify offset at
; 296, the BAR size at 304, the physical pages at 320, the wait request at 400, the handles at 840, the pins at
; 896, the table mapping at 944, the table handle at 1296, the status of a failed check at 1304 and the number of
; checks passed at 1312. Registers: x22 the data page, x20 the table handle, x24 the descriptor, x25 its index,
; x26 the device handle, x21 the configuration page, x27 the mapped BAR, x28 the common configuration, x23 the
; mapped DMA object.

#define VIRTIO_BLOCK 0x10011AF4
#define DESCRIPTOR_IDENTITY 8
#define DESCRIPTOR_REGION_COUNT 24
#define DESCRIPTOR_REGIONS 32
#define REGION_SIZE 24
#define READ_WRITE (WIT_MEMORY_READ + WIT_MEMORY_WRITE)
#define CAP_COMMON 256
#define CAP_NOTIFY 264
#define CAP_ISR 272
#define CAP_DEVICE 280
#define PHYSICAL 320
#define H_REGION0 840
#define H_BAR 848
#define CONFIG_VIEW 856
#define H_OBJECT 864
#define H_EVENT 872
#define H_INTERRUPT 880
#define H_PINS 896
#define TABLE_VIEW 944
#define QUEUE_SIZE 8

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

    ; MEMORY_OBJECT_MAP: handle register, bytes register, protection -> x0, x1 address.
    MACRO
    MAPVIEW $handle, $bytes, $protection
    mov x0, $handle
    mov x2, $bytes
    mov x4, #$protection
    bl object_map
    MEND

    MACRO
    CLOSE $handle
    mov x0, $handle
    SYSCALL WIT_CALL_HANDLE_CLOSE
    MEND

    MACRO
    RELEASE $base
    mov x0, $base
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_MEMORY_RELEASE
    MEND

    ; DEVICE_MEMORY of a region -> x0, x1 handle.
    MACRO
    REGION $index
    mov x1, $index
    mov x0, x26
    mov x2, #0
    SYSCALL WIT_CALL_DEVICE_MEMORY
    MEND

    EXPORT wit_user_start
wit_user_start PROC
    ldr x22, =WIT_USER_DATA
    ldr w9, [x0]
    cmp w9, #WIT_ABI_VERSION
    b.ne failed
    ldr x9, [x0, #WIT_TEST_MODE_OFFSET]
    cmp x9, #WIT_INTERRUPT_TEST_VIRTIO
    b.ne failed
    ldr x20, [x0, #WIT_TEST_TABLE_OFFSET]
    str x20, [x22, #1296]
    bl map_table
    mov x0, x20
    mov x1, x25
    mov x2, #0
    SYSCALL WIT_CALL_DEVICE_ACQUIRE
    EXPECT WIT_STATUS_OK
    mov x26, x1
    ; The configuration page, writable: memory decoding and bus mastering on, then the vendor capabilities.
    REGION #0
    EXPECT WIT_STATUS_OK
    str x1, [x22, #H_REGION0]
    mov x9, #4096
    MAPVIEW x1, x9, READ_WRITE
    EXPECT WIT_STATUS_OK
    str x1, [x22, #CONFIG_VIEW]
    mov x21, x1
    ldrh w9, [x21, #4]
    orr w9, w9, #6
    strh w9, [x21, #4]
    ldrb w9, [x21, #6]
    tst w9, #0x10
    b.eq failed
    ldrb w10, [x21, #0x34]
    mov w11, #0 ; the types found
capabilities
    cbz w10, capabilities_done
    add x12, x21, x10
    ldrb w9, [x12]
    cmp w9, #9 ; a vendor capability
    b.ne capability_next
    ldrb w9, [x12, #3] ; cfg_type 1 common, 2 notify, 3 ISR, 4 device
    cmp w9, #1
    b.lo capability_next
    cmp w9, #4
    b.hi capability_next
    mov w13, #1
    lsl w13, w13, w9
    orr w11, w11, w13
    sub x13, x9, #1
    add x13, x22, x13, lsl #3
    ldrb w14, [x12, #4]
    str w14, [x13, #CAP_COMMON]
    ldr w14, [x12, #8]
    str w14, [x13, #(CAP_COMMON + 4)]
    cmp w9, #2
    b.ne capability_next
    ldr w14, [x12, #16]
    str w14, [x22, #288] ; notify_off_multiplier
capability_next
    ldrb w10, [x12, #1]
    b capabilities
capabilities_done
    cmp w11, #0x1E
    b.ne failed
    ldr w9, [x22, #CAP_COMMON]
    ldr w10, [x22, #CAP_NOTIFY]
    cmp w9, w10
    b.ne failed
    ldr w10, [x22, #CAP_ISR]
    cmp w9, w10
    b.ne failed
    ; The BAR the capabilities name, found among the descriptor's regions by its firmware-assigned base.
    add x10, x21, x9, lsl #2
    ldr w11, [x10, #0x10]
    and x12, x11, #0xFFFFFFF0
    and w11, w11, #6
    cmp w11, #4
    b.ne bar_found
    ldr w11, [x10, #0x14]
    orr x12, x12, x11, lsl #32
bar_found
    mov x13, #1
regions
    ldr w9, [x24, #DESCRIPTOR_REGION_COUNT]
    cmp x13, x9
    b.hs failed
    mov x10, #REGION_SIZE
    mul x10, x13, x10
    add x10, x10, x24
    ldr x11, [x10, #DESCRIPTOR_REGIONS]
    cmp x11, x12
    b.eq region_found
    add x13, x13, #1
    b regions
region_found
    ldr x11, [x10, #(DESCRIPTOR_REGIONS + 8)] ; the region's size
    str x11, [x22, #304]
    REGION x13
    EXPECT WIT_STATUS_OK
    str x1, [x22, #H_BAR]
    ldr x9, [x22, #304]
    MAPVIEW x1, x9, READ_WRITE
    EXPECT WIT_STATUS_OK
    mov x27, x1
    ; Five pages for the descriptors, the available and used rings, the request and the data, pinned one by one.
    mov x0, #(5 * 4096)
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_MEMORY_OBJECT_CREATE
    EXPECT WIT_STATUS_OK
    str x1, [x22, #H_OBJECT]
    mov x9, #(5 * 4096)
    MAPVIEW x1, x9, READ_WRITE
    EXPECT WIT_STATUS_OK
    mov x23, x1
    mov x25, #0
pins
    lsl x0, x25, #12
    bl dma_pin
    EXPECT WIT_STATUS_OK
    add x10, x22, #H_PINS
    str x1, [x10, x25, lsl #3]
    ldr x9, [x22, #192]
    add x10, x22, #PHYSICAL
    str x9, [x10, x25, lsl #3]
    add x25, x25, #1
    cmp x25, #5
    b.lo pins
    ; The common configuration: reset, acknowledge, VERSION_1 alone, FEATURES_OK, queue 0 of eight entries on the
    ; pinned pages, DRIVER_OK.
    ldr w9, [x22, #(CAP_COMMON + 4)]
    add x28, x27, x9
    strb wzr, [x28, #20]
reset_wait
    ldrb w9, [x28, #20]
    cbnz w9, reset_wait
    mov w9, #3
    strb w9, [x28, #20]
    mov w9, #1
    str w9, [x28]
    ldr w9, [x28, #4]
    tst w9, #1 ; VIRTIO_F_VERSION_1
    b.eq failed
    mov w9, #1
    str w9, [x28, #8]
    str w9, [x28, #12]
    str wzr, [x28, #8]
    str wzr, [x28, #12]
    mov w9, #0x0B
    strb w9, [x28, #20]
    ldrb w9, [x28, #20]
    tst w9, #8
    b.eq failed
    strh wzr, [x28, #22]
    ldrh w9, [x28, #24]
    cmp w9, #QUEUE_SIZE
    b.lo failed
    mov w9, #QUEUE_SIZE
    strh w9, [x28, #24]
    ldr x9, [x22, #PHYSICAL]
    str x9, [x28, #32]
    ldr x9, [x22, #(PHYSICAL + 8)]
    str x9, [x28, #40]
    ldr x9, [x22, #(PHYSICAL + 16)]
    str x9, [x28, #48]
    ldrh w9, [x28, #30]
    str w9, [x22, #296]
    mov w9, #1
    strh w9, [x28, #28]
    mov w9, #0x0F
    strb w9, [x28, #20]
    ; The line to an auto-reset event.
    mov x0, #0
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_EVENT_CREATE
    EXPECT WIT_STATUS_OK
    str x1, [x22, #H_EVENT]
    mov x0, x26
    mov x2, x1
    mov x1, #0
    SYSCALL WIT_CALL_INTERRUPT_BIND
    EXPECT WIT_STATUS_OK
    str x1, [x22, #H_INTERRUPT]
    ; One request: read sector 0 into the data page through three descriptors, then notify the queue.
    mov x11, #(3 * 4096)
    add x10, x23, x11
    str wzr, [x10] ; VIRTIO_BLK_T_IN
    str wzr, [x10, #4]
    str xzr, [x10, #8] ; sector 0
    mov w9, #0xFF
    strb w9, [x10, #512]
    ldr x9, [x22, #(PHYSICAL + 24)]
    str x9, [x23]
    mov w9, #16
    str w9, [x23, #8]
    mov w9, #1 ; NEXT
    strh w9, [x23, #12]
    strh w9, [x23, #14]
    ldr x9, [x22, #(PHYSICAL + 32)]
    str x9, [x23, #16]
    mov w9, #512
    str w9, [x23, #24]
    mov w9, #3 ; NEXT | WRITE
    strh w9, [x23, #28]
    mov w9, #2
    strh w9, [x23, #30]
    ldr x9, [x22, #(PHYSICAL + 24)]
    add x9, x9, #512
    str x9, [x23, #32]
    mov w9, #1
    str w9, [x23, #40]
    mov w9, #2 ; WRITE
    strh w9, [x23, #44]
    strh wzr, [x23, #46]
    mov x11, #4096
    add x10, x23, x11
    strh wzr, [x10]
    strh wzr, [x10, #4]
    dsb sy
    mov w9, #1
    strh w9, [x10, #2]
    dsb sy
    ldr w9, [x22, #296]
    ldr w10, [x22, #288]
    mul w9, w9, w10
    ldr w10, [x22, #(CAP_NOTIFY + 4)]
    add w9, w9, w10
    add x9, x27, x9
    strh wzr, [x9]
    ; The completion arrives as the bound interrupt; the ISR register says so and the used ring holds the request.
    ldr x0, [x22, #H_EVENT]
    bl wait_object
    EXPECT WIT_STATUS_OK
    ldr w9, [x22, #(CAP_ISR + 4)]
    add x9, x27, x9
    ldrb w9, [x9]
    tst w9, #1
    b.eq failed
    mov x11, #(2 * 4096)
    add x10, x23, x11
    ldrh w9, [x10, #2]
    cmp w9, #1
    b.ne failed
    ldr w9, [x10, #4]
    cbnz w9, failed
    mov x11, #(3 * 4096)
    add x10, x23, x11
    ldrb w9, [x10, #512]
    cbnz w9, failed
    mov x11, #(4 * 4096)
    add x10, x23, x11
    ldrh w9, [x10, #510]
    mov w11, #0xAA55
    cmp w9, w11
    b.ne failed
    ldr w9, [x10, #54]
    ldr w11, =0x31544146 ; "FAT1"
    cmp w9, w11
    b.ne failed
    ldr x0, [x22, #H_INTERRUPT]
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_INTERRUPT_ACK
    EXPECT WIT_STATUS_OK
    ; Quiesce the device, then give everything back.
    strb wzr, [x28, #20]
    ldr x9, [x22, #H_INTERRUPT]
    CLOSE x9
    EXPECT WIT_STATUS_OK
    ldr x9, [x22, #H_EVENT]
    CLOSE x9
    EXPECT WIT_STATUS_OK
    mov x25, #0
unpins
    add x10, x22, #H_PINS
    ldr x0, [x10, x25, lsl #3]
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_DMA_UNPIN
    EXPECT WIT_STATUS_OK
    add x25, x25, #1
    cmp x25, #5
    b.lo unpins
    RELEASE x23
    EXPECT WIT_STATUS_OK
    ldr x9, [x22, #H_OBJECT]
    CLOSE x9
    EXPECT WIT_STATUS_OK
    RELEASE x27
    EXPECT WIT_STATUS_OK
    ldr x9, [x22, #CONFIG_VIEW]
    RELEASE x9
    EXPECT WIT_STATUS_OK
    ldr x9, [x22, #H_BAR]
    CLOSE x9
    EXPECT WIT_STATUS_OK
    ldr x9, [x22, #H_REGION0]
    CLOSE x9
    EXPECT WIT_STATUS_OK
    CLOSE x26
    EXPECT WIT_STATUS_OK
    ldr x9, [x22, #TABLE_VIEW]
    RELEASE x9
    EXPECT WIT_STATUS_OK
    ldr x9, [x22, #1296]
    CLOSE x9
    EXPECT WIT_STATUS_OK
    mov x0, #WIT_TEST_EXIT_CODE
    b exit_process

; Maps the table read-only and finds the block function: x24 its descriptor, x25 its index.
map_table
    mov x19, x30
    mov x9, #4096
    MAPVIEW x20, x9, WIT_MEMORY_READ
    EXPECT WIT_STATUS_OK
    str x1, [x22, #TABLE_VIEW]
    ldr w9, [x1, #8]
    cbz w9, failed
    mov x25, #0
    add x24, x1, #WIT_DEVICE_TABLE_SIZE
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

; DMA_PIN of one page at offset x0 of the object for the device; the range lands at 192.
dma_pin
    add x9, x22, #128
    mov w10, #WIT_DMA_PIN_VERSION
    str w10, [x9]
    mov w10, #56
    str w10, [x9, #4]
    str x26, [x9, #8]
    ldr x10, [x22, #H_OBJECT]
    str x10, [x9, #16]
    str x0, [x9, #24]
    mov x10, #4096
    str x10, [x9, #32]
    add x10, x22, #192
    str x10, [x9, #40]
    mov w10, #2
    str w10, [x9, #48]
    str wzr, [x9, #52]
    mov x0, x9
    mov x1, #56
    mov x2, #0
    SYSCALL WIT_CALL_DMA_PIN
    ret

; OBJECT_WAIT on one handle x0 without a deadline: x0 status.
wait_object
    add x9, x22, #400
    str x0, [x9, #40]
    add x10, x9, #40
    mov w11, #WIT_WAIT_OBJECTS_VERSION
    str w11, [x9]
    mov w11, #32
    str w11, [x9, #4]
    str x10, [x9, #8]
    mov w11, #1
    str w11, [x9, #16]
    str wzr, [x9, #20]
    mov x11, #-1
    str x11, [x9, #24]
    mov x0, x9
    mov x1, #32
    mov x2, #0
    SYSCALL WIT_CALL_OBJECT_WAIT
    ret

failed
    str x0, [x22, #1304] ; the status the failed check saw
    mov x0, #241
exit_process
    SYSCALL WIT_CALL_PROCESS_EXIT
    DCD 0x00000000 ; UDF #0
    LTORG
    ENDP
    END
