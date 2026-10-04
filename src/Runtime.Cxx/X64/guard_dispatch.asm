; Control Flow Guard call dispatch for code built with /guard:cf (P6.4.g). The compiler calls indirect targets through
; __guard_dispatch_icall_fptr with the target in RAX. WitOS images carry no CFG metadata and the guest does not
; enforce CFG, so the dispatch is a plain jump: guarded indirect calls behave as unguarded ones.

.CODE

wit_cxx_guard_dispatch PROC
    jmp rax
wit_cxx_guard_dispatch ENDP

CONST SEGMENT READONLY ALIGN(8) 'CONST'
PUBLIC __guard_dispatch_icall_fptr
__guard_dispatch_icall_fptr DQ wit_cxx_guard_dispatch
CONST ENDS

END
