#include "user_abi_a64.h"
; ARM64 memory object fixture, the port of tests/User.X64/memory_objects.asm over the memory objects of ABI-1
; (RFC 0011 v3 section 7.2, plan step K5.1): MEMORY_OBJECT_CREATE, MEMORY_OBJECT_MAP at chosen and fixed addresses
; under every protection, views that share pages, protection changes within the handle's rights, attenuated
; handles, an object that outlives its handle and ends with its last mapping, a dual mapping that runs published
; code, an object moved through a channel, and the object, page and mapping quotas. The requests live in the data
; page: the map request at 16, a channel request at 96, the handles to move at 768, the received handles at 800, the
; handles of a scenario at 832, the mappings of the limits scenario at 1040, the output of HANDLE_DUPLICATE at 1200
; and the status of a failed check at 1304. Registers: x20 startup block, x21 test mode, x22 the data page, x23 the
; object handle, x24 and x25 mappings, x26 a scenario handle, x27 a counter.

#define READ_WRITE (WIT_MEMORY_READ + WIT_MEMORY_WRITE)
#define READ_EXECUTE (WIT_MEMORY_READ + WIT_MEMORY_EXECUTE)
#define FIXED_DATA (WIT_USER_MEMORY_BASE + 0x100000)
#define FIXED_CODE (WIT_USER_CODE_BASE + 0x10000)
#define OBJECT_BYTES (WIT_MEMORY_OBJECT_PAGES * 4096)

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

    ; MEMORY_OBJECT_CREATE: size -> x0, x1 handle.
    MACRO
    CREATE $size
    ldr x0, =$size
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_MEMORY_OBJECT_CREATE
    MEND

    ; MEMORY_OBJECT_MAP: handle register, offset, bytes, address (0: chosen), protection -> x0, x1 address. (MAP is a
    ; directive of armasm64, so the macro is MAPVIEW.)
    MACRO
    MAPVIEW $handle, $offset, $bytes, $address, $protection
    mov x0, $handle
    mov x1, #$offset
    mov x2, #$bytes
    ldr x3, =$address
    mov x4, #$protection
    bl object_map
    MEND

    ; MEMORY_RELEASE of a mapping or a reservation.
    MACRO
    RELEASE $base
    mov x0, $base
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_MEMORY_RELEASE
    MEND

    ; MEMORY_PROTECT: base register, size, protection.
    MACRO
    PROTECT $base, $size, $protection
    mov x0, $base
    mov x1, #$size
    mov x2, #$protection
    SYSCALL WIT_CALL_MEMORY_PROTECT
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

    ; CODE_PUBLISH: base register, size -> x0.
    MACRO
    PUBLISH $base, $size
    mov x0, $base
    mov x1, #$size
    mov x2, #0
    SYSCALL WIT_CALL_CODE_PUBLISH
    MEND

    EXPORT wit_user_start
wit_user_start PROC
    mov x20, x0
    ldr x22, =WIT_USER_DATA
    ldr w9, [x20]
    cmp w9, #WIT_ABI_VERSION
    b.ne failed
    ldr x21, [x20, #WIT_TEST_MODE_OFFSET]
    cmp x21, #WIT_MEMORY_OBJECT_TEST_CODE
    b.eq code_test
    cmp x21, #WIT_MEMORY_OBJECT_TEST_TRANSFER
    b.eq transfer_test
    cmp x21, #WIT_MEMORY_OBJECT_TEST_LIMITS
    b.eq limits_test
    cmp x21, #WIT_MEMORY_OBJECT_TEST_EXIT
    b.eq exit_test
    cmp x21, #WIT_MEMORY_OBJECT_TEST_BASIC
    b.ne failed

    ; Creation validates the size and the flags before it takes pages.
    CREATE 0
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    CREATE 4097
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    mov x0, #4096
    mov x1, #1
    mov x2, #0
    SYSCALL WIT_CALL_MEMORY_OBJECT_CREATE
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    CREATE 8192
    EXPECT WIT_STATUS_OK
    mov x23, x1
    ; The map request: a foreign version, a wrong size, an absent target, WRITE with EXECUTE, a window beyond the
    ; object and an empty window are refused before any reservation is taken.
    MAPVIEW x23, 0, 8192, 0, READ_WRITE
    EXPECT WIT_STATUS_OK ; the first view, at an address the kernel chose in the data arena
    mov x24, x1
    ldr x9, =WIT_USER_MEMORY_BASE
    cmp x24, x9
    b.lo failed
    mov w9, #2 ; the request the map left: a foreign version
    str w9, [x22, #16]
    mov x1, #56
    bl raw_map
    EXPECT WIT_STATUS_UNSUPPORTED
    mov w9, #WIT_MEMORY_MAP_VERSION
    str w9, [x22, #16]
    str xzr, [x22, #16 + 48] ; a target that is no handle of ours (a process handle names another, K5.2c)
    mov x1, #56
    bl raw_map
    EXPECT WIT_STATUS_BAD_HANDLE
    mov x9, #-3 ; WIT_PROCESS_SELF
    str x9, [x22, #16 + 48]
    mov x1, #55 ; a wrong size
    bl raw_map
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    MAPVIEW x23, 0, 4096, 0, 6 ; WRITE with EXECUTE
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    MAPVIEW x23, 4096, 8192, 0, WIT_MEMORY_READ
    EXPECT WIT_STATUS_TOO_LARGE
    MAPVIEW x23, 0, 0, 0, WIT_MEMORY_READ
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    ; Two views share the pages; a protection change within the rights takes effect on both.
    mov x9, #0x1122
    str x9, [x24]
    mov x9, #0x3344
    str x9, [x24, #4096]
    MAPVIEW x23, 4096, 4096, 0, WIT_MEMORY_READ
    EXPECT WIT_STATUS_OK
    mov x25, x1
    ldr x9, [x25]
    mov x10, #0x3344
    cmp x9, x10
    b.ne failed
    PROTECT x25, 4096, READ_WRITE
    EXPECT WIT_STATUS_OK
    mov x9, #0x5566
    str x9, [x25]
    ldr x9, [x24, #4096]
    mov x10, #0x5566
    cmp x9, x10
    b.ne failed
    ; An attenuated handle maps read-only views only, and its views cannot be made writable or committed.
    DUPLICATE x23, (WIT_RIGHT_MAP + WIT_RIGHT_QUERY)
    EXPECT WIT_STATUS_OK
    mov x26, x1
    MAPVIEW x26, 0, 4096, 0, READ_WRITE
    EXPECT WIT_STATUS_DENIED
    MAPVIEW x26, 0, 4096, 0, WIT_MEMORY_READ
    EXPECT WIT_STATUS_OK
    str x1, [x22, #832]
    ldr x9, [x1]
    mov x10, #0x1122
    cmp x9, x10
    b.ne failed
    ldr x9, [x22, #832]
    PROTECT x9, 4096, READ_WRITE
    EXPECT WIT_STATUS_DENIED
    ldr x0, [x22, #832]
    mov x1, #4096
    mov x2, #READ_WRITE
    SYSCALL WIT_CALL_MEMORY_COMMIT
    EXPECT WIT_STATUS_DENIED
    ldr x9, [x22, #832]
    RELEASE x9
    EXPECT WIT_STATUS_OK
    CLOSE x26
    EXPECT WIT_STATUS_OK
    ; A fixed address must be free; a free one is taken as asked.
    mov x0, x23
    mov x1, #0
    mov x2, #4096
    mov x3, x24
    mov x4, #WIT_MEMORY_READ
    bl object_map
    EXPECT WIT_STATUS_BUSY
    MAPVIEW x23, 0, 4096, FIXED_DATA, WIT_MEMORY_READ
    EXPECT WIT_STATUS_OK
    ldr x9, =FIXED_DATA
    cmp x1, x9
    b.ne failed
    RELEASE x1
    EXPECT WIT_STATUS_OK
    ; The object outlives its handle and ends with its last mapping; the object quota is whole again afterward.
    CLOSE x23
    EXPECT WIT_STATUS_OK
    ldr x9, [x24]
    mov x10, #0x1122
    cmp x9, x10
    b.ne failed
    ldr x9, [x25]
    mov x10, #0x5566
    cmp x9, x10
    b.ne failed
    RELEASE x24
    EXPECT WIT_STATUS_OK
    RELEASE x25
    EXPECT WIT_STATUS_OK
    mov x27, #0
objects
    CREATE 4096
    EXPECT WIT_STATUS_OK
    add x10, x22, #832
    str x1, [x10, x27, lsl #3]
    add x27, x27, #1
    cmp x27, #WIT_MEMORY_OBJECT_CAPACITY
    b.lo objects
    CREATE 4096
    EXPECT WIT_STATUS_NO_MEMORY
    mov x27, #0
close_objects
    add x10, x22, #832
    ldr x9, [x10, x27, lsl #3]
    CLOSE x9
    EXPECT WIT_STATUS_OK
    add x27, x27, #1
    cmp x27, #WIT_MEMORY_OBJECT_CAPACITY
    b.lo close_objects
    b passed

code_test
    ; A writable view receives the instructions, an executable view at a fixed address of the code arena runs them
    ; once published; publication through the writable view is refused.
    CREATE 4096
    EXPECT WIT_STATUS_OK
    mov x23, x1
    MAPVIEW x23, 0, 4096, 0, READ_WRITE
    EXPECT WIT_STATUS_OK
    mov x24, x1
    MAPVIEW x23, 0, 4096, FIXED_CODE, READ_EXECUTE
    EXPECT WIT_STATUS_OK
    mov x25, x1
    ldr x9, =FIXED_CODE
    cmp x25, x9
    b.ne failed
    ldr w9, =0x52824680 ; mov w0, #0x1234
    str w9, [x24]
    ldr w9, =0xD65F03C0 ; ret
    str w9, [x24, #4]
    PUBLISH x24, 4096
    EXPECT WIT_STATUS_DENIED
    PUBLISH x25, 4096
    EXPECT WIT_STATUS_OK
    blr x25
    mov w10, #0x1234
    cmp w0, w10
    b.ne failed
    PROTECT x24, 4096, 6 ; WRITE with EXECUTE
    EXPECT WIT_STATUS_INVALID_ARGUMENT
    PROTECT x24, 4096, READ_EXECUTE
    EXPECT WIT_STATUS_OK
    PUBLISH x24, 4096
    EXPECT WIT_STATUS_OK
    blr x24
    mov w10, #0x1234
    cmp w0, w10
    b.ne failed
    RELEASE x25
    EXPECT WIT_STATUS_OK
    RELEASE x24
    EXPECT WIT_STATUS_OK
    CLOSE x23
    EXPECT WIT_STATUS_OK
    b passed

transfer_test
    ; An object moves through a channel: the sender's handle is gone, its view stays, the receiver's view reads.
    CREATE 4096
    EXPECT WIT_STATUS_OK
    mov x23, x1
    MAPVIEW x23, 0, 4096, 0, READ_WRITE
    EXPECT WIT_STATUS_OK
    mov x24, x1
    mov x9, #0x7777
    str x9, [x24]
    mov x0, x22
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_CHANNEL_CREATE
    EXPECT WIT_STATUS_OK
    str x23, [x22, #768]
    ldr x0, [x22]
    add x3, x22, #768
    bl channel_request
    SYSCALL WIT_CALL_CHANNEL_SEND
    EXPECT WIT_STATUS_OK
    MAPVIEW x23, 0, 4096, 0, WIT_MEMORY_READ
    EXPECT WIT_STATUS_BAD_HANDLE
    ldr x0, [x22, #8]
    add x3, x22, #800
    bl channel_request
    SYSCALL WIT_CALL_CHANNEL_RECEIVE
    EXPECT WIT_STATUS_OK
    ldr x26, [x22, #800]
    MAPVIEW x26, 0, 4096, 0, WIT_MEMORY_READ
    EXPECT WIT_STATUS_OK
    mov x25, x1
    ldr x9, [x25]
    mov x10, #0x7777
    cmp x9, x10
    b.ne failed
    RELEASE x24
    EXPECT WIT_STATUS_OK
    RELEASE x25
    EXPECT WIT_STATUS_OK
    CLOSE x26
    EXPECT WIT_STATUS_OK
    ldr x9, [x22]
    CLOSE x9
    EXPECT WIT_STATUS_OK
    ldr x9, [x22, #8]
    CLOSE x9
    EXPECT WIT_STATUS_OK
    b passed

limits_test
    ; An object beyond its page quota is refused; a second large object exceeds the component's pages and takes
    ; none; the mappings of one object are bounded by the reservations.
    CREATE (OBJECT_BYTES + 4096)
    EXPECT WIT_STATUS_TOO_LARGE
    CREATE OBJECT_BYTES
    EXPECT WIT_STATUS_OK
    mov x23, x1
    CREATE OBJECT_BYTES
    EXPECT WIT_STATUS_NO_MEMORY
    CLOSE x23
    EXPECT WIT_STATUS_OK
    CREATE OBJECT_BYTES
    EXPECT WIT_STATUS_OK
    CLOSE x1
    EXPECT WIT_STATUS_OK
    CREATE 4096
    EXPECT WIT_STATUS_OK
    mov x23, x1
    mov x27, #0
mappings
    MAPVIEW x23, 0, 4096, 0, WIT_MEMORY_READ
    EXPECT WIT_STATUS_OK
    add x10, x22, #1040
    str x1, [x10, x27, lsl #3]
    add x27, x27, #1
    cmp x27, #WIT_USER_RESERVATION_CAPACITY
    b.lo mappings
    MAPVIEW x23, 0, 4096, 0, WIT_MEMORY_READ
    EXPECT WIT_STATUS_NO_MEMORY
    mov x27, #0
release_mappings
    add x10, x22, #1040
    ldr x9, [x10, x27, lsl #3]
    RELEASE x9
    EXPECT WIT_STATUS_OK
    add x27, x27, #1
    cmp x27, #WIT_USER_RESERVATION_CAPACITY
    b.lo release_mappings
    CLOSE x23
    EXPECT WIT_STATUS_OK
    b passed

exit_test
    ; The component exits with everything live: an object behind a view, its duplicate handle in flight in a
    ; channel, and a second object behind its handle alone. The kernel releases the handles and the capability at
    ; the exit, the view at the teardown.
    CREATE 4096
    EXPECT WIT_STATUS_OK
    mov x23, x1
    MAPVIEW x23, 0, 4096, 0, READ_WRITE
    EXPECT WIT_STATUS_OK
    DUPLICATE x23, 0
    EXPECT WIT_STATUS_OK
    str x1, [x22, #768]
    mov x0, x22
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_CHANNEL_CREATE
    EXPECT WIT_STATUS_OK
    ldr x0, [x22]
    add x3, x22, #768
    bl channel_request
    SYSCALL WIT_CALL_CHANNEL_SEND
    EXPECT WIT_STATUS_OK
    CREATE 8192
    EXPECT WIT_STATUS_OK
    b passed

; The map request at data page + 16: x0 object, x1 offset, x2 bytes, x3 address, x4 protection; then the call.
object_map
    add x9, x22, #16
    mov w10, #WIT_MEMORY_MAP_VERSION
    str w10, [x9]
    mov w10, #56
    str w10, [x9, #4]
    str x0, [x9, #8]
    str x1, [x9, #16]
    str x2, [x9, #24]
    str x3, [x9, #32]
    str w4, [x9, #40]
    str wzr, [x9, #44]
    mov x10, #-3 ; WIT_PROCESS_SELF
    str x10, [x9, #48]
    mov x1, #56
; MEMORY_OBJECT_MAP of the request as it lies at data page + 16, with the size in x1.
raw_map
    add x0, x22, #16
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
    mov x0, #WIT_TEST_EXIT_CODE
exit_process
    SYSCALL WIT_CALL_PROCESS_EXIT
    DCD 0x00000000 ; UDF #0
    LTORG
    ENDP
    END
