option casemap:none
include user_abi.inc

; Root task fixture (RFC 0011 v3 section 7.11, plan step K4): the first component the kernel starts from the boot
; disk's flat image, with nothing but its startup descriptor (witos/root.h) in RCX. It checks the descriptor,
; writes to the kernel log through the log handle, maps the first page of the boot package and checks its magic,
; maps the device table and checks that the board published devices, then exits with the test code. The data
; page holds the map request at 16, the log lines at 256 and the status of a failed check at 1304.

EXPECT MACRO value
    inc QWORD PTR [rbx + 1312]
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
; DEBUG_WRITE of a line at data page + 256: length -> rax.
LOG MACRO length
    mov rcx, [r15 + 64 + 8 * WIT_ROOT_HANDLE_LOG]
    lea rdx, [rbx + 256]
    mov r8d, length
    CALL0 WIT_CALL_DEBUG_WRITE
    EXPECT WIT_STATUS_OK
    cmp edx, length ; the bytes written
    jne failed
ENDM
RELEASE MACRO base
    mov rcx, base
    xor edx, edx
    xor r8d, r8d
    CALL0 WIT_CALL_MEMORY_RELEASE
ENDM

.code
PUBLIC wit_user_start
wit_user_start PROC
    mov r15, rcx ; WitRootStartup
    mov rbx, WIT_USER_DATA
    cmp DWORD PTR [r15], WIT_ROOT_STARTUP_VERSION
    jne failed
    cmp DWORD PTR [r15 + 4], WIT_ROOT_STARTUP_SIZE
    jne failed
    cmp DWORD PTR [r15 + 8], WIT_ABI_VERSION
    jne failed
    mov eax, [r15 + 12] ; the feature mask: channels and devices are present
    and eax, WIT_ABI_FEATURE_CHANNELS + WIT_ABI_FEATURE_DEVICES
    cmp eax, WIT_ABI_FEATURE_CHANNELS + WIT_ABI_FEATURE_DEVICES
    jne failed
    mov rax, WIT_USER_MEMORY_BASE
    cmp [r15 + 16], rax
    jne failed
    mov rax, WIT_USER_CODE_BASE
    cmp [r15 + 32], rax
    jne failed
    cmp QWORD PTR [r15 + 48], 0 ; PackageBytes
    je failed
    cmp DWORD PTR [r15 + 56], 3 ; HandleCount: the log, the package and the device table
    jb failed
    ; "[ROOT] started" through the log handle.
    mov DWORD PTR [rbx + 256], 4F4F525Bh ; "[ROO"
    mov DWORD PTR [rbx + 260], 73205D54h ; "T] s"
    mov DWORD PTR [rbx + 264], 74726174h ; "tart"
    mov DWORD PTR [rbx + 268], 000A6465h ; "ed\n"
    LOG 15
    ; The boot package: its first page is the package header with the magic "WITPAK01".
    MAP QWORD PTR [r15 + 64 + 8 * WIT_ROOT_HANDLE_PACKAGE], 4096, WIT_MEMORY_READ
    EXPECT WIT_STATUS_OK
    mov rsi, rdx
    mov rax, 31304B4150544957h ; "WITPAK01"
    cmp [rsi], rax
    jne failed
    MAP QWORD PTR [r15 + 64 + 8 * WIT_ROOT_HANDLE_PACKAGE], 4096, WIT_MEMORY_READ + WIT_MEMORY_WRITE
    EXPECT WIT_STATUS_DENIED
    RELEASE rsi
    EXPECT WIT_STATUS_OK
    ; The device table: a version 1 table with at least one descriptor.
    MAP QWORD PTR [r15 + 64 + 8 * WIT_ROOT_HANDLE_DEVICES], 4096, WIT_MEMORY_READ
    EXPECT WIT_STATUS_OK
    mov rsi, rdx
    mov eax, 101 ; the check that failed, for the kernel's diagnostics
    cmp DWORD PTR [rsi], WIT_DEVICE_TABLE_VERSION
    jne failed
    mov eax, 102
    cmp DWORD PTR [rsi + 8], 0
    je failed
    RELEASE rsi
    EXPECT WIT_STATUS_OK
    mov DWORD PTR [rbx + 256], 4F4F525Bh ; "[ROO"
    mov DWORD PTR [rbx + 260], 70205D54h ; "T] p"
    mov DWORD PTR [rbx + 264], 616B6361h ; "acka"
    mov DWORD PTR [rbx + 268], 61206567h ; "ge a"
    mov DWORD PTR [rbx + 272], 6420646Eh ; "nd d"
    mov DWORD PTR [rbx + 276], 63697665h ; "evic"
    mov DWORD PTR [rbx + 280], 0A207365h ; "es \n"
    LOG 28
    xor ecx, ecx ; a root task exits with zero; the kernel treats anything else as failure
    jmp exit_process

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

failed:
    mov [rbx + 1304], rax ; the status the failed check saw
    mov ecx, 241
exit_process:
    CALL0 WIT_CALL_PROCESS_EXIT
    ud2
wit_user_start ENDP
END
