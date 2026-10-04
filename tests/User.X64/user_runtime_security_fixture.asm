option casemap:none
EXTERN __security_check_cookie:PROC
EXTERN __GSHandlerCheck:PROC
.const
align 8
gs_sig0 DQ 0123456789ABCDEFh
gs_sig1 DQ 01234567890ABCDEFh
gs_sig2 DQ 02234567890ABCDEFh
gs_sig3 DQ 03234567890ABCDEFh
gs_sig4 DQ 04234567890ABCDEFh
gs_sig5 DQ 05234567890ABCDEFh
; Direct-handler ABI fixtures; these are immutable data, not published PE
; exception-table registrations or evidence of exception dispatch support.
align 4
PUBLIC wit_gs_plain_function
wit_gs_plain_function DD imagerel wit_gs_check_abi, imagerel wit_gs_check_end, imagerel gs_plain_unwind
gs_plain_unwind DB 019h,0,0,0
DD imagerel __GSHandlerCheck
PUBLIC wit_gs_plain_data
wit_gs_plain_data DD 11 ; cookie offset 8, EH/UH metadata flags
PUBLIC wit_gs_aligned_function
wit_gs_aligned_function DD imagerel wit_gs_check_abi, imagerel wit_gs_check_end, imagerel gs_aligned_unwind
gs_aligned_unwind DB 019h,1,1,025h
DB 1,3 ; SET_FPREG; the helper consumes the frame-register/offset descriptor
DW 0
DD imagerel __GSHandlerCheck
PUBLIC wit_gs_aligned_data
wit_gs_aligned_data DD 15,24,16 ; offset 8, aligned base (frame+24)&~15
PUBLIC wit_gs_bad_version_function
wit_gs_bad_version_function DD imagerel wit_gs_check_abi, imagerel wit_gs_check_end, imagerel gs_bad_version_unwind
gs_bad_version_unwind DB 01Ah,0,0,0
DD imagerel __GSHandlerCheck
PUBLIC wit_gs_bad_version_data
wit_gs_bad_version_data DD 11
PUBLIC wit_gs_bad_alignment_function
wit_gs_bad_alignment_function DD imagerel wit_gs_check_abi, imagerel wit_gs_check_end, imagerel gs_bad_alignment_unwind
gs_bad_alignment_unwind DB 019h,0,0,0
DD imagerel __GSHandlerCheck
PUBLIC wit_gs_bad_alignment_data
wit_gs_bad_alignment_data DD 15,24,3
.code
PUBLIC wit_gs_check_abi
wit_gs_check_abi PROC FRAME
    sub rsp, 40
    .allocstack 40
    .endprolog
    mov rax, QWORD PTR [gs_sig0]
    movq xmm0, rax
    mov rdx, QWORD PTR [gs_sig1]
    mov r8, QWORD PTR [gs_sig2]
    mov r9, QWORD PTR [gs_sig3]
    mov r10, QWORD PTR [gs_sig4]
    mov r11, QWORD PTR [gs_sig5]
    call __security_check_cookie
    cmp rax, QWORD PTR [gs_sig0]
    jne bad
    cmp rdx, QWORD PTR [gs_sig1]
    jne bad
    cmp r8, QWORD PTR [gs_sig2]
    jne bad
    cmp r9, QWORD PTR [gs_sig3]
    jne bad
    cmp r10, QWORD PTR [gs_sig4]
    jne bad
    cmp r11, QWORD PTR [gs_sig5]
    jne bad
    movq rcx, xmm0
    cmp rcx, QWORD PTR [gs_sig0]
    jne bad
    mov eax, 1
    jmp done
bad:
    xor eax, eax
done:
    add rsp, 40
    ret
wit_gs_check_end LABEL BYTE
wit_gs_check_abi ENDP
END
