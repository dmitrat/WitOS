#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <string.h>
#include "library.h"
// Hosted transport comparison only. Production guest loading is independently
// exercised by library_loader.c. Windows performs the actual DLL load here.
static const char* fixture;
static HMODULE module;
static WitU64 generation=100;
static unsigned references;
static int failPathAllocation;
void host_library_arm_allocation_failure(void);
void host_library_fail_path_allocation(int value){failPathAllocation=value;}
void host_library_fixture(const char* path){fixture=path;}
unsigned host_library_references(void){return references;}
WitU64 host_library_call(WitU64 address,WitU64 bytes,WitU64 reserved,WitU64* output)
{
    WitLibraryRequest* r=(WitLibraryRequest*)address;
    if(bytes!=sizeof(*r)||reserved||r->Version!=WIT_LIBRARY_VERSION)return WIT_STATUS_INVALID_ARGUMENT;
    const DWORD previous=GetLastError();WitU64 status=WIT_STATUS_OK;
    if(r->Flags==WIT_LIBRARY_USER_LIFECYCLE&&r->BufferBytes==8)*(WitU64*)r->Buffer=0;
    if(r->Operation==WIT_LIBRARY_LOAD){
        if(r->NameBytes!=14||memcmp((void*)r->Name,"native/lib.dll",14))return WIT_STATUS_NOT_FOUND;
        if(!module){module=LoadLibraryA(fixture);if(!module)status=WIT_STATUS_UNSUPPORTED;else ++generation;}
        if(module){++references;if(output)*output=generation;}
    }else if(r->Operation==WIT_LIBRARY_FIND){
        const char* expected=r->Flags?"lib.dll":"native/lib.dll";
        if(!module||r->NameBytes!=strlen(expected)||memcmp((void*)r->Name,expected,(size_t)r->NameBytes))status=WIT_STATUS_NOT_FOUND;
        else {++references;if(output)*output=generation;}
    }else if(!module||r->Handle!=generation)status=WIT_STATUS_BAD_HANDLE;
    else if(r->Operation==WIT_LIBRARY_PATH){
        if(r->BufferBytes!=sizeof(WitLibraryPath))return WIT_STATUS_INVALID_ARGUMENT;
        WitLibraryPath* path=(WitLibraryPath*)r->Buffer;memset(path,0,sizeof(*path));
        path->Version=WIT_LIBRARY_VERSION;path->Size=sizeof(*path);path->NameBytes=14;memcpy(path->Name,"native/lib.dll",14);
        if(failPathAllocation){failPathAllocation=0;host_library_arm_allocation_failure();}
        if(output)*output=sizeof(*path);
    }
    else if(r->Operation==WIT_LIBRARY_SYMBOL){
        char name[256];
        if(!r->NameBytes||r->NameBytes>=sizeof(name))status=WIT_STATUS_INVALID_ARGUMENT;
        else {memcpy(name,(void*)r->Name,(size_t)r->NameBytes);name[r->NameBytes]=0;
            FARPROC found=GetProcAddress(module,name);
            if(!found)status=WIT_STATUS_NOT_FOUND;else if(output)memcpy(output,&found,sizeof(found));}
    }else if(r->Operation==WIT_LIBRARY_UNLOAD){
        if(references==1){if(!FreeLibrary(module))status=WIT_STATUS_BUSY;else {module=0;references=0;}}
        else --references;
    }else status=WIT_STATUS_UNSUPPORTED;
    SetLastError(previous);return status;
}
