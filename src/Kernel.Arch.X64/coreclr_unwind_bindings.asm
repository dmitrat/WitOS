option casemap:none
EXTERN wit_coreclr_add_function_table:PROC
EXTERN wit_coreclr_install_function_table:PROC
EXTERN wit_coreclr_delete_function_table:PROC
EXTERN wit_coreclr_lookup_function_entry:PROC
EXTERN wit_coreclr_virtual_unwind:PROC
EXTERN wit_native_rtl_unwind:PROC
EXTERN wit_native_handler_bridge:PROC
.code
PUBLIC RtlAddFunctionTable
RtlAddFunctionTable PROC
    jmp wit_coreclr_add_function_table
RtlAddFunctionTable ENDP
PUBLIC RtlInstallFunctionTableCallback
RtlInstallFunctionTableCallback PROC
    jmp wit_coreclr_install_function_table
RtlInstallFunctionTableCallback ENDP
PUBLIC RtlDeleteFunctionTable
RtlDeleteFunctionTable PROC
    jmp wit_coreclr_delete_function_table
RtlDeleteFunctionTable ENDP
PUBLIC RtlLookupFunctionEntry
RtlLookupFunctionEntry PROC
    jmp wit_coreclr_lookup_function_entry
RtlLookupFunctionEntry ENDP
PUBLIC RtlVirtualUnwind
RtlVirtualUnwind PROC
    jmp wit_coreclr_virtual_unwind
RtlVirtualUnwind ENDP
PUBLIC RtlUnwind
RtlUnwind PROC FRAME
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
RtlUnwind_clear_prefix:
 mov [r10],rax
 add r10,8
 dec r11d
 jnz RtlUnwind_clear_prefix
 lea r10,[rsp+304]
 mov r11d,122
RtlUnwind_clear_extended:
 mov [r10],rax
 add r10,8
 dec r11d
 jnz RtlUnwind_clear_extended
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
 mov r9,[rsp+1296]
 mov rax,[rsp+1304]
 mov [rsp+32],rax
 xor eax,eax
 mov [rsp+40],rax
 call wit_native_rtl_unwind
 ud2
RtlUnwind ENDP
PUBLIC RtlUnwindEx
RtlUnwindEx PROC FRAME
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
RtlUnwindEx_clear_prefix:
 mov [r10],rax
 add r10,8
 dec r11d
 jnz RtlUnwindEx_clear_prefix
 lea r10,[rsp+304]
 mov r11d,122
RtlUnwindEx_clear_extended:
 mov [r10],rax
 add r10,8
 dec r11d
 jnz RtlUnwindEx_clear_extended
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
 mov r9,[rsp+1296]
 mov rax,[rsp+1304]
 mov [rsp+32],rax
 mov rax,[rsp+1376]
 mov [rsp+40],rax
 call wit_native_rtl_unwind
 ud2
RtlUnwindEx ENDP
PUBLIC wit_native_handler_invoke
wit_native_handler_invoke:
 sub rsp,56
 mov [rsp+32],rcx
 mov rax,rdx
 mov rcx,r8
 mov rdx,r9
 mov r8,[rsp+96]
 mov r9,[rsp+104]
 call rax
 add rsp,56
 ret
wit_native_handler_invoke_end:
coreclr_bridge_xdata SEGMENT READONLY ALIGN(4) ALIAS('.xdata')
coreclr_bridge_info DB 019h,4,1,0,4,062h,0,0
 DD imagerel wit_native_handler_bridge
coreclr_bridge_xdata ENDS
coreclr_bridge_pdata SEGMENT READONLY ALIGN(4) ALIAS('.pdata')
 DD imagerel wit_native_handler_invoke,imagerel wit_native_handler_invoke_end,imagerel coreclr_bridge_info
coreclr_bridge_pdata ENDS
.const
ALIGN 8
PUBLIC __imp_RtlAddFunctionTable
__imp_RtlAddFunctionTable DQ RtlAddFunctionTable
PUBLIC __imp_RtlInstallFunctionTableCallback
__imp_RtlInstallFunctionTableCallback DQ RtlInstallFunctionTableCallback
PUBLIC __imp_RtlDeleteFunctionTable
__imp_RtlDeleteFunctionTable DQ RtlDeleteFunctionTable
PUBLIC __imp_RtlLookupFunctionEntry
__imp_RtlLookupFunctionEntry DQ RtlLookupFunctionEntry
PUBLIC __imp_RtlVirtualUnwind
__imp_RtlVirtualUnwind DQ RtlVirtualUnwind
PUBLIC __imp_RtlUnwind
__imp_RtlUnwind DQ RtlUnwind
PUBLIC __imp_RtlUnwindEx
__imp_RtlUnwindEx DQ RtlUnwindEx
END
