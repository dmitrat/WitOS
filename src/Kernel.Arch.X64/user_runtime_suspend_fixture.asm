option casemap:none
EXTERN SuspendThread:PROC
EXTERN ResumeThread:PROC
.code
PUBLIC wit_test_suspend
wit_test_suspend PROC
 jmp SuspendThread
wit_test_suspend ENDP
PUBLIC wit_test_resume
wit_test_resume PROC
 jmp ResumeThread
wit_test_resume ENDP
END
