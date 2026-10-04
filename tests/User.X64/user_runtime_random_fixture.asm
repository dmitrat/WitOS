option casemap:none
EXTERN BCryptGenRandom:PROC
.code
PUBLIC wit_random_direct
wit_random_direct PROC
    jmp BCryptGenRandom
wit_random_direct ENDP
END
