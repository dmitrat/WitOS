option casemap:none
EXTERN wit_native_virtual_alloc:PROC
EXTERN wit_native_virtual_free:PROC
.code
PUBLIC VirtualAlloc
VirtualAlloc PROC
    jmp wit_native_virtual_alloc
VirtualAlloc ENDP
PUBLIC VirtualFree
VirtualFree PROC
    jmp wit_native_virtual_free
VirtualFree ENDP
.const
ALIGN 8
PUBLIC __imp_VirtualAlloc
PUBLIC __imp_VirtualFree
__imp_VirtualAlloc DQ VirtualAlloc
__imp_VirtualFree DQ VirtualFree
END
