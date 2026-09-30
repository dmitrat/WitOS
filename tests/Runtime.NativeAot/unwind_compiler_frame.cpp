#include <windows.h>
extern "C" void capture_parent(volatile char*);
extern "C" __declspec(noinline) int protected_frame()
{
    volatile char buffer[128];
    for(unsigned i=0;i<128;++i)buffer[i]=(char)(i^0x5A);
    buffer[0]=42;
    __try {capture_parent(buffer);}
    __finally {buffer[2]=1;}
    return buffer[0]+buffer[1];
}
