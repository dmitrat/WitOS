option casemap:none
EXTERN CloseHandle:PROC
EXTERN Sleep:PROC
.code
PUBLIC wit_services_direct_close
wit_services_direct_close PROC
    jmp CloseHandle
wit_services_direct_close ENDP
PUBLIC wit_services_direct_sleep
wit_services_direct_sleep PROC
    jmp Sleep
wit_services_direct_sleep ENDP
END
