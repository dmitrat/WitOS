option casemap:none
.code
; Microsoft x64 compiler helper: RAX is the allocation size. Preserve RAX,
; RSP, argument/nonvolatile/SIMD registers; only R10/R11/flags are scratch.
; WitOS stacks are fixed mappings: an inaccessible page faults the component.
; This does not grow stacks or move the caller's RSP.
PUBLIC __chkstk
PUBLIC wit_chkstk_end
__chkstk PROC
    lea r10, [rsp + 8]
    mov r11, rax
    test r11, r11
    jz probe_done
probe_page:
    cmp r11, 1000h
    jb probe_tail
    sub r10, 1000h
    test BYTE PTR [r10], 0
    sub r11, 1000h
    jnz probe_page
    ret
probe_tail:
    sub r10, r11
    test BYTE PTR [r10], 0
probe_done:
    ret
__chkstk ENDP
wit_chkstk_end LABEL BYTE
END
