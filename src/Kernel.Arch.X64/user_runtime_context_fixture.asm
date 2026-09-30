option casemap:none
.code
PUBLIC wit_test_context_cs
wit_test_context_cs PROC
 xor eax,eax
 mov ax,cs
 ret
wit_test_context_cs ENDP
PUBLIC wit_test_context_ss
wit_test_context_ss PROC
 xor eax,eax
 mov ax,ss
 ret
wit_test_context_ss ENDP
END
