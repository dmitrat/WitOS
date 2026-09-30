option casemap:none
.code
PUBLIC unwind_simple
PUBLIC unwind_simple_push
PUBLIC unwind_simple_body
PUBLIC unwind_simple_epilog
PUBLIC unwind_simple_pop
PUBLIC unwind_simple_ret
unwind_simple PROC FRAME
 push rbx
 .pushreg rbx
unwind_simple_push LABEL BYTE
 sub rsp,32
 .allocstack 32
 .endprolog
unwind_simple_body LABEL BYTE
 nop
 nop
unwind_simple_epilog LABEL BYTE
 add rsp,32
unwind_simple_pop LABEL BYTE
 pop rbx
unwind_simple_ret LABEL BYTE
 ret
unwind_simple ENDP
PUBLIC unwind_frame
PUBLIC unwind_frame_body
PUBLIC unwind_frame_epilog
unwind_frame PROC FRAME
 push rbp
 .pushreg rbp
 sub rsp,128
 .allocstack 128
 lea rbp,[rsp+64]
 .setframe rbp,64
 movdqa XMMWORD PTR [rsp+32],xmm6
 .savexmm128 xmm6,32
 .endprolog
unwind_frame_body LABEL BYTE
 nop
 nop
 movdqa xmm6,XMMWORD PTR [rsp+32]
unwind_frame_epilog LABEL BYTE
 lea rsp,[rbp+64]
 pop rbp
 ret
unwind_frame ENDP
PUBLIC unwind_large
PUBLIC unwind_large_body
unwind_large PROC FRAME
 sub rsp,4096
 .allocstack 4096
 .endprolog
unwind_large_body LABEL BYTE
 nop
 nop
 add rsp,4096
 ret
unwind_large ENDP
; Readonly manual records exercise real PE chain and handler contracts.
PUBLIC unwind_primary
PUBLIC unwind_primary_body
PUBLIC unwind_primary_end
unwind_primary:
 push rbx
 sub rsp,32
unwind_primary_body:
 nop
 nop
 add rsp,32
 pop rbx
 ret
unwind_primary_end:
PUBLIC unwind_child
PUBLIC unwind_child_end
unwind_child:
 nop
 nop
 ret
unwind_child_end:
PUBLIC unwind_handled
PUBLIC unwind_handled_body
PUBLIC unwind_handled_end
unwind_handled:
 push rbx
 sub rsp,32
unwind_handled_body:
 nop
 nop
 add rsp,32
 pop rbx
 ret
unwind_handled_end:
PUBLIC unwind_handler
unwind_handler:
 ret
PUBLIC unwind_v2
PUBLIC unwind_v2_body
PUBLIC unwind_v2_epilog
PUBLIC unwind_v2_pop
PUBLIC unwind_v2_ret
unwind_v2:
 push rdi
 push rsi
unwind_v2_body:
 nop
 nop
unwind_v2_epilog:
 pop rsi
unwind_v2_pop:
 pop rdi
unwind_v2_ret:
 ret
unwind_v2_end:
unwind_xdata SEGMENT READONLY ALIGN(4) ALIAS('.xdata')
v2_info DB 2,2,4,0, 3,016h,0,006h,2,060h,1,070h
primary_info DB 1,5,2,0, 5,032h,1,030h
child_info DB 021h,0,0,0
 DD imagerel unwind_primary, imagerel unwind_primary_end, imagerel primary_info
handled_info DB 009h,5,2,0, 5,032h,1,030h
 DD imagerel unwind_handler
 DD 0A123B456h
unwind_xdata ENDS
unwind_pdata SEGMENT READONLY ALIGN(4) ALIAS('.pdata')
 DD imagerel unwind_primary, imagerel unwind_primary_end, imagerel primary_info
 DD imagerel unwind_child, imagerel unwind_child_end, imagerel child_info
 DD imagerel unwind_handled, imagerel unwind_handled_end, imagerel handled_info
 DD imagerel unwind_v2, imagerel unwind_v2_end, imagerel v2_info
unwind_pdata ENDS
END
