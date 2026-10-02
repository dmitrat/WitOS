#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <string.h>
static int(*value)(void);static void(*set)(int);static void*(*address)(void);static int(*zero)(void);
static void* mainAddress;static void* workerAddress;
static DWORD WINAPI worker(void* arg)
{
    (void)arg;if(value()!=736||zero())return 1;
    workerAddress=address();if(!workerAddress||workerAddress==mainAddress)return 2;
    set(7);return value()==12?42:3;
}
int main(int argc,char** argv)
{
    if(argc!=2)return 1;HMODULE module=LoadLibraryExA(argv[1],0,LOAD_WITH_ALTERED_SEARCH_PATH);if(!module)return 2;
    FARPROC raw=GetProcAddress(module,"TlsValue");memcpy(&value,&raw,sizeof(value));
    raw=GetProcAddress(module,"TlsSet");memcpy(&set,&raw,sizeof(set));
    raw=GetProcAddress(module,"TlsAddress");memcpy(&address,&raw,sizeof(address));
    raw=GetProcAddress(module,"TlsZero");memcpy(&zero,&raw,sizeof(zero));
    if(!value||!set||!address||!zero)return 3;
    const IMAGE_DOS_HEADER* dos=(const IMAGE_DOS_HEADER*)module;
    const IMAGE_NT_HEADERS64* nt=(const IMAGE_NT_HEADERS64*)((const unsigned char*)module+dos->e_lfanew);
    const IMAGE_TLS_DIRECTORY64* tls=(const IMAGE_TLS_DIRECTORY64*)((const unsigned char*)module+nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_TLS].VirtualAddress);
    printf("TLS directory start=%llx end=%llx indexAddress=%llx storedIndex=%lu bytes=%llu flags=%lx\n",tls->StartAddressOfRawData,tls->EndAddressOfRawData,tls->AddressOfIndex,*(DWORD*)(uintptr_t)tls->AddressOfIndex,tls->EndAddressOfRawData-tls->StartAddressOfRawData,tls->Characteristics);
    unsigned(*index)(void)=0;raw=GetProcAddress(module,"TlsIndex");memcpy(&index,&raw,sizeof(index));
    unsigned(*calls)(void)=0;raw=GetProcAddress(module,"EntryCalls");memcpy(&calls,&raw,sizeof(calls));
    printf("TLS module=%p index=%u entryCalls=%u initial=%d zero=%d address=%p\n",(void*)module,index?index():999,calls?calls():999,value(),zero(),address());
    if(value()!=736||zero())return 3;
    mainAddress=address();set(20);if(value()!=25)return 4;
    HANDLE thread=CreateThread(0,0,worker,0,0,0);DWORD code=0;
    if(!thread||WaitForSingleObject(thread,30000)!=WAIT_OBJECT_0||!GetExitCodeThread(thread,&code)||code!=42)return 5;
    CloseHandle(thread);if(value()!=25||address()!=mainAddress||zero())return 6;
    if(!FreeLibrary(module))return 7;
    puts("PASS: actual Windows DLL static TLS isolation, zero bytes and relocated template pointer");return 0;
}
