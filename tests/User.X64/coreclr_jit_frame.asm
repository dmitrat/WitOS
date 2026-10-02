option casemap:none
.code
PUBLIC wit_dynamic_frame_begin
PUBLIC wit_dynamic_frame_end
wit_dynamic_frame_begin PROC FRAME
    push rbx
    .pushreg rbx
    sub rsp,32
    .allocstack 32
    .endprolog
    mov rbx,rcx
    lea rcx,[rsp]
    lea rdx,after_callback
    mov r8,[rsp+32]
    mov r9,[rsp+40]
    call rbx
after_callback:
    mov eax,42
    add rsp,32
    pop rbx
    ret
wit_dynamic_frame_begin ENDP
wit_dynamic_frame_end LABEL BYTE
PUBLIC wit_dynamic_fault_begin
PUBLIC wit_dynamic_fault_site
PUBLIC wit_dynamic_fault_resume
PUBLIC wit_dynamic_fault_end
wit_dynamic_fault_begin PROC FRAME
    push rbx
    .pushreg rbx
    sub rsp,32
    .allocstack 32
    .endprolog
    xor eax,eax
wit_dynamic_fault_site LABEL BYTE
    mov eax,[rax]
wit_dynamic_fault_resume LABEL BYTE
    add eax,11
    add rsp,32
    pop rbx
    ret
wit_dynamic_fault_begin ENDP
wit_dynamic_fault_end LABEL BYTE
PUBLIC wit_dynamic_nested_begin
PUBLIC wit_dynamic_nested_inner
PUBLIC wit_dynamic_nested_fault
PUBLIC wit_dynamic_nested_landing
PUBLIC wit_dynamic_nested_end
wit_dynamic_nested_begin PROC FRAME
 push rbx
 .pushreg rbx
 sub rsp,32
 .allocstack 32
 .endprolog
 mov rbx,01122334455667788h
 call wit_dynamic_nested_inner
wit_dynamic_nested_landing LABEL BYTE
 mov r10,01122334455667788h
 cmp rbx,r10
 jne nested_bad
 add eax,11
 add rsp,32
 pop rbx
 ret
nested_bad:
 mov eax,-1
 add rsp,32
 pop rbx
 ret
wit_dynamic_nested_begin ENDP
wit_dynamic_nested_inner PROC FRAME
 push rbx
 .pushreg rbx
 sub rsp,32
 .allocstack 32
 .endprolog
 mov rbx,08877665544332211h
 xor eax,eax
wit_dynamic_nested_fault LABEL BYTE
 mov eax,[rax]
 add rsp,32
 pop rbx
 ret
wit_dynamic_nested_inner ENDP
wit_dynamic_nested_end LABEL BYTE
END
