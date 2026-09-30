option casemap:none
.code
PUBLIC wit_native_gc_breakpoint
PUBLIC wit_native_gc_breakpoint_resume
wit_native_gc_breakpoint PROC
 db 0CCh
wit_native_gc_breakpoint_resume LABEL BYTE
 ret
wit_native_gc_breakpoint ENDP
END
