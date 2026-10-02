option casemap:none
EXTERN QueryPerformanceCounter:PROC
EXTERN QueryPerformanceFrequency:PROC
EXTERN GetTickCount64:PROC
.code
PUBLIC wit_clock_direct_counter
wit_clock_direct_counter PROC
    jmp QueryPerformanceCounter
wit_clock_direct_counter ENDP
PUBLIC wit_clock_direct_frequency
wit_clock_direct_frequency PROC
    jmp QueryPerformanceFrequency
wit_clock_direct_frequency ENDP
PUBLIC wit_clock_direct_ticks
wit_clock_direct_ticks PROC
    jmp GetTickCount64
wit_clock_direct_ticks ENDP
END
