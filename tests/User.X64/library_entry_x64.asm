option casemap:none
.code
PUBLIC LibraryCurrentCs
LibraryCurrentCs PROC
    xor eax,eax
    mov ax,cs
    ret
LibraryCurrentCs ENDP
END
