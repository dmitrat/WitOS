option casemap:none
EXTERN wit_native_rtl_virtual_unwind:PROC
.code
PUBLIC RtlVirtualUnwind
RtlVirtualUnwind PROC
    jmp wit_native_rtl_virtual_unwind
RtlVirtualUnwind ENDP
.const
ALIGN 8
PUBLIC __imp_RtlVirtualUnwind
__imp_RtlVirtualUnwind QWORD RtlVirtualUnwind
END
