option casemap:none
EXTERN VirtualAlloc:PROC
EXTERN VirtualFree:PROC
.code
PUBLIC wit_memory_direct_alloc
wit_memory_direct_alloc PROC
    jmp VirtualAlloc
wit_memory_direct_alloc ENDP
PUBLIC wit_memory_direct_free
wit_memory_direct_free PROC
    jmp VirtualFree
wit_memory_direct_free ENDP
END
