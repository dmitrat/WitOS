option casemap:none
include user_abi.inc
EXTERN wit_test_exception_transfer_prepare:PROC
EXTERN RaiseException:PROC
EXTERN RaiseFailFastException:PROC
EXTERN wit_test_noncont_language_handler:PROC
.code
PUBLIC wit_test_exception_trigger
PUBLIC wit_exception_landing
PUBLIC wit_exception_invalid
PUBLIC wit_exception_divide
PUBLIC wit_exception_page
PUBLIC wit_exception_breakpoint
PUBLIC wit_exception_general
PUBLIC wit_exception_low_stack
wit_test_exception_trigger PROC FRAME
 push r12
 .pushreg r12
 sub rsp,576
 .allocstack 576
 .endprolog
 db 048h
 fxsave [rsp+32]
 mov r12,01122334455667788h
 movq xmm6,r12
 punpcklqdq xmm6,xmm6
 fld1
 cmp ecx,1
 je divide_case
 cmp ecx,2
 je page_case
 cmp ecx,3
 je breakpoint_case
 cmp ecx,4
 je general_case
 cmp ecx,5
 je fetch_case
 cmp ecx,6
 je low_case
wit_exception_invalid LABEL BYTE
 ud2
 jmp failed
 divide_case:
 xor edx,edx
 mov eax,1
 xor r10d,r10d
wit_exception_divide LABEL BYTE
 div r10
 jmp failed
 page_case:
 mov rax,0000400000000000h
wit_exception_page LABEL BYTE
 mov rax,[rax]
 jmp failed
 breakpoint_case:
 int 3
wit_exception_breakpoint LABEL BYTE
 jmp failed
 general_case:
 mov ax,0FFF8h
wit_exception_general LABEL BYTE
 mov ds,ax
 jmp failed
 fetch_case:
 xor eax,eax
 jmp rax
 low_case:
 mov rsp,WIT_USER_STACK_BOTTOM + 2048
wit_exception_low_stack LABEL BYTE
 ud2
 jmp failed
wit_exception_landing LABEL BYTE
 pushfq
 pop r10
 mov r11,0FEDCBA9876543210h
 cmp rax,r11
 jne failed
 mov r11,0778899AABBCCDD11h
 cmp r12,r11
 jne failed
 and r10,0FFFh
 cmp r10,0247h
 jne failed
 movq r10,xmm6
 cmp r10,r11
 jne failed
 fstp TBYTE PTR [rsp+544]
 mov r11,08000000000000000h
 cmp QWORD PTR [rsp+544],r11
 jne failed
 cmp WORD PTR [rsp+552],03FFFh
 jne failed
 mov eax,1
 jmp done
failed:
 xor eax,eax
done:
 db 048h
 fxrstor [rsp+32]
 add rsp,576
 pop r12
 ret
wit_test_exception_trigger ENDP
PUBLIC wit_exception_transfer_probe
wit_exception_transfer_probe PROC FRAME
 push r12
 .pushreg r12
 sub rsp,576
 .allocstack 576
 .endprolog
 db 048h
 fxsave [rsp+32]
 mov [rsp+544],rcx
 mov r12,0778899AABBCCDD11h
 movq xmm6,r12
 mov rax,rcx
 mov rcx,rdx
 mov rdx,rax
 mov eax,WIT_CALL_THREAD_CONTEXT_GET
 int 80h
 pushfq
 pop r10
 cmp rax,42
 je transfer_resumed
 test rax,rax
 jne transfer_failed
 mov rcx,[rsp+544]
 call wit_test_exception_transfer_prepare
 jmp transfer_failed
transfer_resumed:
 and r10,0FFFh
 cmp r10,0247h
 jne transfer_failed
 mov r11,0778899AABBCCDD11h
 cmp r12,r11
 jne transfer_failed
 movq r10,xmm6
 cmp r10,r11
 jne transfer_failed
 mov eax,42
 jmp transfer_done
transfer_failed:
 xor eax,eax
transfer_done:
 db 048h
 fxrstor [rsp+32]
 add rsp,576
 pop r12
 ret
wit_exception_transfer_probe ENDP
PUBLIC wit_raise_direct
wit_raise_direct PROC
 jmp RaiseException
wit_raise_direct ENDP
PUBLIC wit_failfast_direct
wit_failfast_direct PROC
 jmp RaiseFailFastException
wit_failfast_direct ENDP
PUBLIC wit_seh_hardware_fault
wit_seh_hardware_fault PROC
 ud2
 ret
wit_seh_hardware_fault ENDP
PUBLIC wit_test_noncontinuable_frame
wit_test_noncontinuable_frame:
 sub rsp,40
 mov ecx,0E0421234h
 mov edx,1
 xor r8d,r8d
 xor r9d,r9d
 call RaiseException
 add rsp,40
 ret
wit_noncont_frame_end:
raise_xdata SEGMENT READONLY ALIGN(4) ALIAS('.xdata')
raise_frame_info DB 9,4,1,0,4,042h,0,0
 DD imagerel wit_test_noncont_language_handler
raise_xdata ENDS
raise_pdata SEGMENT READONLY ALIGN(4) ALIAS('.pdata')
 DD imagerel wit_test_noncontinuable_frame, imagerel wit_noncont_frame_end, imagerel raise_frame_info
raise_pdata ENDS
END
