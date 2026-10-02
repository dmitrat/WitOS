option casemap:none
EXTERN wit_native_call:PROC
.code
PUBLIC wit_test_context_registers
wit_test_context_registers PROC FRAME
 push r12
 .pushreg r12
 push r13
 .pushreg r13
 sub rsp,584
 .allocstack 584
 db 048h
 fxsave [rsp+48]
 .savexmm128 xmm6,304
 .endprolog
 mov QWORD PTR [rsp+32],0
 fninit
 fld1
 ldmxcsr DWORD PTR [test_mxcsr]
 mov r12,055AA001122334455h
 mov r13,0AA55112233445566h
 movq xmm6,r12
 punpcklqdq xmm6,xmm6
 call wit_native_call
 db 048h
 fxrstor [rsp+48]
 add rsp,584
 pop r13
 pop r12
 ret
wit_test_context_registers ENDP
.const
ALIGN 4
test_mxcsr DD 07F80h
END
