option casemap:none
include user_abi.inc
EXTERN wit_native_add_vectored_exception_handler:PROC
EXTERN wit_native_remove_vectored_exception_handler:PROC
EXTERN wit_native_raise_exception:PROC
EXTERN wit_native_local_unwind:PROC
EXTERN wit_native_seh_bridge_handler:PROC
EXTERN wit_native_raise_fail_fast:PROC
.code
PUBLIC AddVectoredExceptionHandler
AddVectoredExceptionHandler PROC
 jmp wit_native_add_vectored_exception_handler
AddVectoredExceptionHandler ENDP
PUBLIC RemoveVectoredExceptionHandler
RemoveVectoredExceptionHandler PROC
 jmp wit_native_remove_vectored_exception_handler
RemoveVectoredExceptionHandler ENDP
PUBLIC RaiseException
RaiseException PROC FRAME
 pushfq
 .allocstack 8
 sub rsp,1328
 .allocstack 1328
 .endprolog
 mov [rsp+168],rax
 mov [rsp+176],rcx
 mov [rsp+184],rdx
 mov [rsp+192],rbx
 mov [rsp+208],rbp
 mov [rsp+216],rsi
 mov [rsp+224],rdi
 mov [rsp+232],r8
 mov [rsp+240],r9
 mov [rsp+248],r10
 mov [rsp+256],r11
 mov [rsp+264],r12
 mov [rsp+272],r13
 mov [rsp+280],r14
 mov [rsp+288],r15
 mov [rsp+1280],rcx
 mov [rsp+1288],rdx
 mov [rsp+1296],r8
 mov [rsp+1304],r9
 lea rax,[rsp+1344]
 mov [rsp+200],rax
 mov rax,[rsp+1336]
 mov [rsp+296],rax
 lea r10,[rsp+48]
 mov r11d,15
 xor eax,eax
clear_prefix:
 mov [r10],rax
 add r10,8
 dec r11d
 jnz clear_prefix
 lea r10,[rsp+304]
 mov r11d,122
clear_extended:
 mov [r10],rax
 add r10,8
 dec r11d
 jnz clear_extended
 lea rcx,[rsp+48]
 mov DWORD PTR [rcx+48],010001Bh
 mov rax,[rsp+1328]
 mov [rcx+68],eax
 mov ax,cs
 mov [rcx+56],ax
 mov ax,ss
 mov [rcx+66],ax
 db 048h
 fxsave [rcx+256]
 mov eax,[rcx+280]
 mov [rcx+52],eax
 mov edx,[rsp+1280]
 mov r8d,[rsp+1288]
 mov r9d,[rsp+1296]
 mov rax,[rsp+1304]
 mov [rsp+32],rax
 call wit_native_raise_exception
 ud2
RaiseException ENDP
PUBLIC _local_unwind
_local_unwind PROC FRAME
 pushfq
 .allocstack 8
 sub rsp,1328
 .allocstack 1328
 .endprolog
 mov [rsp+168],rax
 mov [rsp+176],rcx
 mov [rsp+184],rdx
 mov [rsp+192],rbx
 mov [rsp+208],rbp
 mov [rsp+216],rsi
 mov [rsp+224],rdi
 mov [rsp+232],r8
 mov [rsp+240],r9
 mov [rsp+248],r10
 mov [rsp+256],r11
 mov [rsp+264],r12
 mov [rsp+272],r13
 mov [rsp+280],r14
 mov [rsp+288],r15
 mov [rsp+1280],rcx
 mov [rsp+1288],rdx
 mov [rsp+1296],r8
 mov [rsp+1304],r9
 lea rax,[rsp+1344]
 mov [rsp+200],rax
 mov rax,[rsp+1336]
 mov [rsp+296],rax
 lea r10,[rsp+48]
 mov r11d,15
 xor eax,eax
local_clear_prefix:
 mov [r10],rax
 add r10,8
 dec r11d
 jnz local_clear_prefix
 lea r10,[rsp+304]
 mov r11d,122
local_clear_extended:
 mov [r10],rax
 add r10,8
 dec r11d
 jnz local_clear_extended
 lea rcx,[rsp+48]
 mov DWORD PTR [rcx+48],010001Bh
 mov rax,[rsp+1328]
 mov [rcx+68],eax
 mov ax,cs
 mov [rcx+56],ax
 mov ax,ss
 mov [rcx+66],ax
 db 048h
 fxsave [rcx+256]
 mov eax,[rcx+280]
 mov [rcx+52],eax
 mov rdx,[rsp+1280]
 mov r8,[rsp+1288]
 call wit_native_local_unwind
 ud2
_local_unwind ENDP
PUBLIC RaiseFailFastException
RaiseFailFastException PROC FRAME
 ; Arm before reading optional inputs or allocating a diagnostic frame.
 mov r9,rcx
 mov r10,rdx
 mov r11,r8
 mov eax,WIT_CALL_FATAL_ARM
 mov ecx,0C0000602h
 xor edx,edx
 xor r8d,r8d
 int 80h
 mov rcx,r9
 mov rdx,r10
 mov r8,r11
 mov r9,[rsp]
 sub rsp,40
 .allocstack 40
 .endprolog
 call wit_native_raise_fail_fast
 ud2
RaiseFailFastException ENDP
; Keep the paused dispatcher identity in a described frame. Both search and
; unwind handlers bridge nested walks to the original logical language frame.
PUBLIC wit_native_seh_invoke
wit_native_seh_invoke:
 sub rsp,56
 mov [rsp+32],rcx
 mov rax,rdx
 mov rcx,r8
 mov rdx,r9
 call rax
 add rsp,56
 ret
wit_native_seh_invoke_end:
seh_bridge_xdata SEGMENT READONLY ALIGN(4) ALIAS('.xdata')
seh_bridge_info DB 019h,4,1,0,4,062h,0,0
 DD imagerel wit_native_seh_bridge_handler
seh_bridge_xdata ENDS
seh_bridge_pdata SEGMENT READONLY ALIGN(4) ALIAS('.pdata')
 DD imagerel wit_native_seh_invoke,imagerel wit_native_seh_invoke_end,imagerel seh_bridge_info
seh_bridge_pdata ENDS
.const
ALIGN 8
PUBLIC __imp_AddVectoredExceptionHandler
PUBLIC __imp_RemoveVectoredExceptionHandler
__imp_AddVectoredExceptionHandler QWORD AddVectoredExceptionHandler
__imp_RemoveVectoredExceptionHandler QWORD RemoveVectoredExceptionHandler
PUBLIC __imp_RaiseException
__imp_RaiseException QWORD RaiseException
PUBLIC __imp_RaiseFailFastException
__imp_RaiseFailFastException QWORD RaiseFailFastException
END
