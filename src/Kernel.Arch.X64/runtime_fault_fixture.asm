option casemap:none
.code
PUBLIC wit_runtime_invalid_instruction
wit_runtime_invalid_instruction PROC
    ud2
    jmp wit_runtime_invalid_instruction
wit_runtime_invalid_instruction ENDP
END
