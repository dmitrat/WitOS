#include "user_abi_a64.h"
; ARM64 root task fixture, the port of tests/User.X64/root.asm (RFC 0011 v3 section 7.11, plan step K4): the first
; component the kernel starts from the boot disk's flat image, with nothing but its startup descriptor
; (witos/root.h) in x0. It checks the descriptor, writes to the kernel log through the log handle, maps the first
; page of the boot package and checks its magic, maps the device table and checks that the board published devices,
; then exits with the test code. The data page holds the map request at 16, the log lines at 256 and the status of
; a failed check at 1304.

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

    ; DEBUG_WRITE of a line at data page + 256 of a length -> x0 the bytes written.
    MACRO
    LOG $length
    ldr x0, [x20, #(64 + 8 * WIT_ROOT_HANDLE_LOG)]
    add x1, x22, #256
    mov x2, #$length
    SYSCALL WIT_CALL_DEBUG_WRITE
    EXPECT WIT_STATUS_OK
    cmp x1, #$length ; the bytes written
    b.ne failed
    MEND

    MACRO
    RELEASE $base
    mov x0, $base
    mov x1, #0
    mov x2, #0
    SYSCALL WIT_CALL_MEMORY_RELEASE
    MEND

    EXPORT wit_user_start
wit_user_start PROC
    mov x20, x0 ; WitRootStartup
    ldr x22, =WIT_USER_DATA
    ldr w9, [x20]
    cmp w9, #WIT_ROOT_STARTUP_VERSION
    b.ne failed
    ldr w9, [x20, #4]
    cmp w9, #WIT_ROOT_STARTUP_SIZE
    b.ne failed
    ldr w9, [x20, #8]
    cmp w9, #WIT_ABI_VERSION
    b.ne failed
    ldr w9, [x20, #12] ; the feature mask: channels and devices are present
    and w9, w9, #(WIT_ABI_FEATURE_CHANNELS + WIT_ABI_FEATURE_DEVICES)
    cmp w9, #(WIT_ABI_FEATURE_CHANNELS + WIT_ABI_FEATURE_DEVICES)
    b.ne failed
    ldr x9, [x20, #16]
    ldr x10, =WIT_USER_MEMORY_BASE
    cmp x9, x10
    b.ne failed
    ldr x9, [x20, #32]
    ldr x10, =WIT_USER_CODE_BASE
    cmp x9, x10
    b.ne failed
    ldr x9, [x20, #48] ; PackageBytes
    cbz x9, failed
    ldr w9, [x20, #56] ; HandleCount: the log, the package and the device table
    cmp w9, #3
    b.lo failed
    ; "[ROOT] started" through the log handle.
    ldr w9, =0x4F4F525B ; "[ROO"
    str w9, [x22, #256]
    ldr w9, =0x73205D54 ; "T] s"
    str w9, [x22, #260]
    ldr w9, =0x74726174 ; "tart"
    str w9, [x22, #264]
    ldr w9, =0x000A6465 ; "ed\n"
    str w9, [x22, #268]
    LOG 15
    ; The boot package: its first page is the package header with the magic "WITPAK01".
    ldr x9, [x20, #(64 + 8 * WIT_ROOT_HANDLE_PACKAGE)]
    MAPVIEW x9, 4096, WIT_MEMORY_READ
    EXPECT WIT_STATUS_OK
    mov x23, x1
    ldr x9, [x23]
    ldr x10, =0x31304B4150544957 ; "WITPAK01"
    cmp x9, x10
    b.ne failed
    ldr x9, [x20, #(64 + 8 * WIT_ROOT_HANDLE_PACKAGE)]
    MAPVIEW x9, 4096, (WIT_MEMORY_READ + WIT_MEMORY_WRITE)
    EXPECT WIT_STATUS_DENIED
    RELEASE x23
    EXPECT WIT_STATUS_OK
    ; The device table: a version 1 table with at least one descriptor.
    ldr x9, [x20, #(64 + 8 * WIT_ROOT_HANDLE_DEVICES)]
    MAPVIEW x9, 4096, WIT_MEMORY_READ
    EXPECT WIT_STATUS_OK
    mov x23, x1
    mov x0, #101 ; the check that failed, for the kernel's diagnostics
    ldr w9, [x23]
    cmp w9, #WIT_DEVICE_TABLE_VERSION
    b.ne failed
    mov x0, #102
    ldr w9, [x23, #8]
    cbz w9, failed
    RELEASE x23
    EXPECT WIT_STATUS_OK
    ldr w9, =0x4F4F525B ; "[ROO"
    str w9, [x22, #256]
    ldr w9, =0x70205D54 ; "T] p"
    str w9, [x22, #260]
    ldr w9, =0x616B6361 ; "acka"
    str w9, [x22, #264]
    ldr w9, =0x61206567 ; "ge a"
    str w9, [x22, #268]
    ldr w9, =0x6420646E ; "nd d"
    str w9, [x22, #272]
    ldr w9, =0x63697665 ; "evic"
    str w9, [x22, #276]
    ldr w9, =0x0A207365 ; "es \n"
    str w9, [x22, #280]
    LOG 28
    mov x0, #0 ; a root task exits with zero; the kernel treats anything else as failure
    b exit_process

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

failed
    str x0, [x22, #1304] ; the status the failed check saw
    mov x0, #241
exit_process
    SYSCALL WIT_CALL_PROCESS_EXIT
    DCD 0x00000000 ; UDF #0
    LTORG
    ENDP
    END
