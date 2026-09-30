option casemap:none
EXTERN wit_native_call:PROC
EXTERN wit_test_prepare_restore:PROC
EXTERN wit_test_restore_via_pal:PROC
EXTERN wit_context_spin_ready:QWORD
EXTERN wit_context_spin_result:QWORD
EXTERN wit_context_spin_xmm:QWORD
EXTERN wit_context_spin_mxcsr:DWORD
.code
PUBLIC wit_context_spin
PUBLIC wit_context_spin_landing
wit_context_spin PROC FRAME
 push r12
 .pushreg r12
 sub rsp,32
 .allocstack 32
 movdqu XMMWORD PTR [rsp],xmm6
 .savexmm128 xmm6,0
 .endprolog
 stmxcsr DWORD PTR [rsp+16]
 mov r12,01122334455667788h
 mov QWORD PTR [wit_context_spin_ready],1
spin_loop:
 pause
 jmp spin_loop
wit_context_spin_landing LABEL BYTE
 mov QWORD PTR [wit_context_spin_result],r12
 movq rax,xmm6
 mov QWORD PTR [wit_context_spin_xmm],rax
 stmxcsr DWORD PTR [wit_context_spin_mxcsr]
 ldmxcsr DWORD PTR [rsp+16]
 movdqu xmm6,XMMWORD PTR [rsp]
 add rsp,32
 pop r12
 ret
wit_context_spin ENDP
PUBLIC wit_test_restore_roundtrip
wit_test_restore_roundtrip PROC FRAME
 push rbx
 .pushreg rbx
 push r12
 .pushreg r12
 sub rsp,584
 .allocstack 584
 db 048h
 fxsave [rsp+48]
 .savexmm128 xmm6,304
 .endprolog
 mov QWORD PTR [rsp+40],rcx
 mov QWORD PTR [rsp+560],r8
 mov QWORD PTR [rsp+568],r9
 mov QWORD PTR [rsp+32],0
 fninit
 fld1
 mov rcx,rdx
 mov rdx,-2
 mov r8,QWORD PTR [rsp+40]
 call wit_native_call
 test rax,rax
 jne restore_failed
 mov rcx,QWORD PTR [rsp+40]
 lea rdx,restore_landing
 mov r8,rsp
 call wit_test_prepare_restore
 mov rcx,QWORD PTR [rsp+560]
 mov rdx,QWORD PTR [rsp+40]
 mov r8,QWORD PTR [rsp+568]
 mov r9d,DWORD PTR [rdx]
 call wit_native_call
restore_failed:
 xor eax,eax
 jmp restore_done
restore_landing:
 pushfq
 pop r10
 mov r11,012345678ABCDEF01h
 cmp rax,r11
 jne restore_failed
 mov r11,055AA773311225588h
 cmp r12,r11
 jne restore_failed
 and r10,0FFFh
 cmp r10,0247h
 jne restore_failed
 movq r10,xmm6
 cmp r10,r11
 jne restore_failed
 stmxcsr DWORD PTR [rsp+16]
 cmp DWORD PTR [rsp+16],07F80h
 jne restore_failed
 fstp TBYTE PTR [rsp+16]
 mov r10,08000000000000000h
 cmp QWORD PTR [rsp+16],r10
 jne restore_failed
 cmp WORD PTR [rsp+24],03FFFh
 jne restore_failed
 mov eax,1
restore_done:
 db 048h
 fxrstor [rsp+48]
 add rsp,584
 pop r12
 pop rbx
 ret
wit_test_restore_roundtrip ENDP
PUBLIC wit_test_pal_restore_roundtrip
wit_test_pal_restore_roundtrip PROC FRAME
 push rbx
 .pushreg rbx
 push r12
 .pushreg r12
 sub rsp,584
 .allocstack 584
 db 048h
 fxsave [rsp+48]
 .savexmm128 xmm6,304
 .endprolog
 mov QWORD PTR [rsp+40],rcx
 mov QWORD PTR [rsp+560],r8
 mov QWORD PTR [rsp+568],r9
 mov QWORD PTR [rsp+32],0
 fninit
 fld1
 mov rcx,rdx
 mov rdx,-2
 mov r8,QWORD PTR [rsp+40]
 call wit_native_call
 test rax,rax
 jne pal_restore_failed
 mov rcx,QWORD PTR [rsp+40]
 lea rdx,pal_restore_landing
 mov r8,rsp
 call wit_test_prepare_restore
 mov rcx,QWORD PTR [rsp+40]
 call wit_test_restore_via_pal
pal_restore_failed:
 xor eax,eax
 jmp pal_restore_done
pal_restore_landing:
 pushfq
 pop r10
 mov r11,012345678ABCDEF01h
 cmp rax,r11
 jne pal_restore_failed
 mov r11,055AA773311225588h
 cmp r12,r11
 jne pal_restore_failed
 and r10,0FFFh
 cmp r10,0247h
 jne pal_restore_failed
 movq r10,xmm6
 cmp r10,r11
 jne pal_restore_failed
 stmxcsr DWORD PTR [rsp+16]
 cmp DWORD PTR [rsp+16],07F80h
 jne pal_restore_failed
 fstp TBYTE PTR [rsp+16]
 mov r10,08000000000000000h
 cmp QWORD PTR [rsp+16],r10
 jne pal_restore_failed
 cmp WORD PTR [rsp+24],03FFFh
 jne pal_restore_failed
 mov eax,1
pal_restore_done:
 db 048h
 fxrstor [rsp+48]
 add rsp,584
 pop r12
 pop rbx
 ret
wit_test_pal_restore_roundtrip ENDP
END
