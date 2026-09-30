#include <windows.h>
extern "C" void wit_seh_gs_action(volatile unsigned char*,unsigned);
extern "C" LONG wit_seh_gs_select(EXCEPTION_POINTERS*,volatile unsigned char*,unsigned);
extern "C" void wit_seh_gs_finally(int);
extern "C" void wit_seh_gs_caught();
#if defined(WITOS_SEH_GS_ALIGNED)
#define WIT_GS_FRAME wit_seh_gs_aligned_frame
#define WIT_GS_ALIGN __declspec(align(32))
#else
#define WIT_GS_FRAME wit_seh_gs_frame
#define WIT_GS_ALIGN
#endif
extern "C" __declspec(noinline) int WIT_GS_FRAME(unsigned mode)
{
    WIT_GS_ALIGN volatile unsigned char buffer[128];
    for(unsigned i=0;i<128;++i)buffer[i]=(unsigned char)i;
    buffer[0]=17;buffer[127]=25;
    __try {
        __try {wit_seh_gs_action(buffer,mode);}
        __finally {wit_seh_gs_finally(AbnormalTermination()!=FALSE);}
    } __except(wit_seh_gs_select(GetExceptionInformation(),buffer,mode)) {
        wit_seh_gs_caught();return buffer[0]+buffer[127];
    }
    return buffer[0]+buffer[127];
}
