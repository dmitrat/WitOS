#include "pal.witos.h"

extern "C" BOOL WINAPI wit_native_close_handle(HANDLE handle)
{
    return (BOOL)PalCloseHandle(handle);
}

extern "C" void WINAPI wit_native_sleep(DWORD milliseconds)
{
    PalSleep(milliseconds);
}
