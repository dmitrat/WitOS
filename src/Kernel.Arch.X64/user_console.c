#include "user.h"
#include "witos/platform.h"
/* Completion writes target only the caller's validated DWORD. No console data
 * is emitted until the handle and complete source range are validated. */
WitU64 wit_user_console_write(WitUserProcess* process,WitU64 address,WitU64 size,WitU64 reserved)
{
    WitConsoleWriteRequest request;WitU8 buffer[WIT_ABI_MAX_WRITE];WitU32 written=0;
    if(reserved||size!=sizeof(request))return WIT_STATUS_INVALID_ARGUMENT;
    if(!wit_user_copy_from(&process->Space,address,(WitU8*)&request,sizeof(request)))return WIT_STATUS_BAD_ADDRESS;
    if(request.Version!=WIT_CONSOLE_WRITE_VERSION||request.Size!=sizeof(request))return WIT_STATUS_UNSUPPORTED;
    if(!wit_user_buffer_writable(&process->Space,request.Written,sizeof(written)))return WIT_STATUS_BAD_ADDRESS;
    if(!wit_user_copy_to(&process->Space,request.Written,(const WitU8*)&written,sizeof(written)))wit_panic("Console completion validation changed");
    const WitU64 status=wit_handle_check(&process->Handles,request.Handle,WIT_HANDLE_CONSOLE,WIT_RIGHT_WRITE);
    if(status!=WIT_STATUS_OK)return status;
    if(request.Length>WIT_CONSOLE_MAX_WRITE)return WIT_STATUS_TOO_LARGE;
    if(!wit_user_buffer_readable(&process->Space,request.Buffer,(WitU32)request.Length))return WIT_STATUS_BAD_ADDRESS;
    if(request.Length){
        wit_console_write("[USER] ");
        while(written<(WitU32)request.Length){
            WitU32 count=(WitU32)request.Length-written;if(count>sizeof(buffer))count=sizeof(buffer);
            if(!wit_user_copy_from(&process->Space,request.Buffer+written,buffer,count))wit_panic("Validated console source changed");
            wit_console_write_buffer(buffer,count);written+=count;
        }
        ++process->Writes;
    }
    if(!wit_user_copy_to(&process->Space,request.Written,(const WitU8*)&written,sizeof(written)))wit_panic("Console completion copy failed");
    return WIT_STATUS_OK;
}
