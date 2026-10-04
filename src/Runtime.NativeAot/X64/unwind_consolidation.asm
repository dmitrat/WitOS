; STATUS_UNWIND_CONSOLIDATE for RtlUnwindEx (P6.4.f), as Windows' RtlRestoreContext provides it: the consolidation
; callback runs on the current stack, below the frames the unwind left, under a machine frame that unwinds to the
; target context; the target then resumes where the callback returns. The kernel enters this frame from the retired
; exception with the target's nonvolatile registers, RSP = 8 mod 16 and RCX = the WitConsolidation request, whose
; first five fields are the target's machine frame and the sixth its exception record. The prolog saves those
; registers where the unwind codes say, so an exception leaving the callback unwinds straight to the target frame.

EXTERN wit_native_consolidate_finish:PROC

CONSOLIDATION_RECORD EQU 40
RECORD_CALLBACK EQU 32 ; EXCEPTION_RECORD.ExceptionInformation[0]

.CODE

PUBLIC wit_native_consolidate_start
wit_native_consolidate_start PROC FRAME
    sub rsp, 40
    mov rax, [rcx]
    mov [rsp], rax
    mov rax, [rcx + 8]
    mov [rsp + 8], rax
    mov rax, [rcx + 16]
    mov [rsp + 16], rax
    mov rax, [rcx + 24]
    mov [rsp + 24], rax
    mov rax, [rcx + 32]
    mov [rsp + 32], rax
    .pushframe
    sub rsp, 272
    .allocstack 272
    mov [rsp + 32], rbx
    .savereg rbx, 32
    mov [rsp + 40], rbp
    .savereg rbp, 40
    mov [rsp + 48], rsi
    .savereg rsi, 48
    mov [rsp + 56], rdi
    .savereg rdi, 56
    mov [rsp + 64], r12
    .savereg r12, 64
    mov [rsp + 72], r13
    .savereg r13, 72
    mov [rsp + 80], r14
    .savereg r14, 80
    mov [rsp + 88], r15
    .savereg r15, 88
    movaps [rsp + 96], xmm6
    .savexmm128 xmm6, 96
    movaps [rsp + 112], xmm7
    .savexmm128 xmm7, 112
    movaps [rsp + 128], xmm8
    .savexmm128 xmm8, 128
    movaps [rsp + 144], xmm9
    .savexmm128 xmm9, 144
    movaps [rsp + 160], xmm10
    .savexmm128 xmm10, 160
    movaps [rsp + 176], xmm11
    .savexmm128 xmm11, 176
    movaps [rsp + 192], xmm12
    .savexmm128 xmm12, 192
    movaps [rsp + 208], xmm13
    .savexmm128 xmm13, 208
    movaps [rsp + 224], xmm14
    .savexmm128 xmm14, 224
    movaps [rsp + 240], xmm15
    .savexmm128 xmm15, 240
    .endprolog
    mov rbx, rcx
    mov rcx, [rbx + CONSOLIDATION_RECORD]
    call qword ptr [rcx + RECORD_CALLBACK]
    mov rcx, rbx
    mov rdx, rax
    call wit_native_consolidate_finish
    int 3
wit_native_consolidate_start ENDP

END
