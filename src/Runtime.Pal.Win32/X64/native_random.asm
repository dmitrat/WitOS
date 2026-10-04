option casemap:none
EXTERN wit_native_bcrypt_random:PROC
.code
PUBLIC BCryptGenRandom
BCryptGenRandom PROC
    jmp wit_native_bcrypt_random
BCryptGenRandom ENDP
.const
PUBLIC __imp_BCryptGenRandom
__imp_BCryptGenRandom DQ BCryptGenRandom
END
