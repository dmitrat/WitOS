option casemap:none
.code
PUBLIC wit_gp_fault
PUBLIC wit_gp_resume
PUBLIC wit_gp_end
PUBLIC wit_gp_unsupported
wit_gp_fault PROC FRAME
 sub rsp,40
 .allocstack 40
 .endprolog
 cmp ecx,0
 je segment_case
 cmp ecx,1
 je cli_case
 cmp ecx,2
 je halt_case
 cmp ecx,5
 je alignment_case
 cmp ecx,6
 je unsupported_case
 mov rax,04000000000000000h
 cmp ecx,4
 je write_case
 mov rax,[rax]
 jmp wit_gp_resume
write_case:
 mov [rax],rcx
 jmp wit_gp_resume
segment_case:
 mov ax,0FFF8h
 mov ds,ax
 jmp wit_gp_resume
cli_case:
 cli
 jmp wit_gp_resume
halt_case:
 hlt
 jmp wit_gp_resume
alignment_case:
 movaps xmm0,[rsp+1]
 jmp wit_gp_resume
unsupported_case:
wit_gp_unsupported LABEL BYTE
 fxsave [rsp+1]
wit_gp_resume LABEL NEAR
 mov eax,1
 add rsp,40
 ret
wit_gp_fault ENDP
wit_gp_end LABEL BYTE
END
