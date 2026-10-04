#include "user_abi_a64.h"
; ARM64 PE loading fixture, the port of tests/User.X64/image.asm. Code reaches the image through PC-relative
; ADR and ADRP, which need no relocation; the data pointers are DIR64 relocations, one of them crossing a page.
; Registers: x20 startup block, x21 test mode, x22 image base.

    IMPORT __ImageBase
    EXPORT wit_pe_start
    EXPORT data_pointer
    EXPORT written
    EXPORT split_pointer
    EXPORT readonly_pointer
    EXPORT bss_data

    AREA |.text|, CODE, READONLY

wit_pe_start PROC
    mov x20, x0
    adrp x22, __ImageBase
    add x22, x22, __ImageBase
    ldrh w9, [x22]
    mov w10, #0x5A4D
    cmp w9, w10
    b.ne failed
    ldr w9, [x20]
    cmp w9, #WIT_ABI_VERSION
    b.ne failed

    adr x9, data_pointer
    ldr x9, [x9]
    adr x10, message
    cmp x9, x10
    b.ne failed
    adr x9, readonly_pointer
    ldr x9, [x9]
    adr x10, helper
    cmp x9, x10
    b.ne failed
    adr x9, split_pointer
    ldr x9, [x9]
    cmp x9, x10
    b.ne failed
    adr x9, readonly_pointer
    ldr x9, [x9]
    blr x9
    mov x10, #0x5A17
    cmp x0, x10
    b.ne failed
    adr x9, bss_data
    mov x10, #8192
bss_zero
    ldrb w11, [x9], #1
    cbnz w11, failed
    subs x10, x10, #1
    b.ne bss_zero
    adr x9, written
    ldr x10, [x9]
    cbnz x10, failed
    ldr x10, [x20, #WIT_TEST_INSTANCE_OFFSET]
    str x10, [x9]

    ldr x21, [x20, #WIT_TEST_MODE_OFFSET]
    cmp x21, #WIT_IMAGE_TEST_WRITE_CODE
    b.eq write_code
    cmp x21, #WIT_IMAGE_TEST_WRITE_RO
    b.eq write_ro
    cmp x21, #WIT_IMAGE_TEST_WRITE_HEADER
    b.eq write_header
    cmp x21, #WIT_IMAGE_TEST_EXECUTE_DATA
    b.eq execute_data
    cmp x21, #WIT_IMAGE_TEST_END
    b.eq read_end
    cmp x21, #WIT_IMAGE_TEST_GAP
    b.eq read_gap
    cbnz x21, failed
    ldr x0, [x20, #8]
    adr x1, message
    mov x2, #message_end - message
    mov x8, #WIT_CALL_WRITE
    svc #0
    cbnz x0, failed
    cmp x1, #message_end - message
    b.ne failed
    mov x0, #WIT_TEST_EXIT_CODE
    b exit_component
write_code
    adr x9, wit_pe_start
    strb wzr, [x9]
    b failed
write_ro
    adr x9, readonly_pointer
    strb wzr, [x9]
    b failed
write_header
    strb wzr, [x22]
    b failed
execute_data
    adr x9, data_pointer
    ldr w10, =0xD65F03C0 ; RET
    str w10, [x9]
    blr x9
    b failed
read_end
    ldr w9, [x22, #0x3C]
    add x9, x22, x9
    ldr w9, [x9, #0x50] ; SizeOfImage
    ldrb w9, [x22, x9]
    b failed
read_gap
    ldr w9, [x22, #0x3C]
    add x9, x22, x9
    ldr w9, [x9, #0xB0] ; relocation directory RVA, one page after the gap
    sub x9, x9, #1, lsl #12
    ldrb w9, [x22, x9]
    b failed
failed
    mov x0, #241
exit_component
    mov x8, #WIT_CALL_EXIT
    svc #0
    DCD 0x00000000 ; UDF #0
helper
    mov x0, #0x5A17
    ret
    LTORG
    ENDP

    AREA |.rdata|, DATA, READONLY, ALIGN=3
readonly_pointer DCQ helper
message DCB "Hello from a relocated PE image.", 10
message_end

    AREA USERDATA, DATA, READWRITE, ALIGN=12
data_pointer DCQ message
written DCQ 0
    SPACE 4076
split_pointer DCQU helper ; DIR64 starts four bytes before a page boundary

    AREA |.bss|, NOINIT, READWRITE
bss_data SPACE 8192

    END
