option casemap:none
EXTERN wit_native_suspend_thread:PROC
EXTERN wit_native_resume_thread:PROC
.code
PUBLIC SuspendThread
SuspendThread PROC
 jmp wit_native_suspend_thread
SuspendThread ENDP
PUBLIC ResumeThread
ResumeThread PROC
 jmp wit_native_resume_thread
ResumeThread ENDP
.const
ALIGN 8
PUBLIC __imp_SuspendThread
PUBLIC __imp_ResumeThread
__imp_SuspendThread DQ SuspendThread
__imp_ResumeThread DQ ResumeThread
END
