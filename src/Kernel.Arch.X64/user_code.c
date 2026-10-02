#include "user.h"
WitU64 wit_user_code_call(WitUserProcess* process,WitU64 input,WitU64 size,WitU64 reserved,WitU64* result)
{
    *result=0;
    if(reserved||size!=sizeof(WitCodeMemoryRequest))return WIT_STATUS_INVALID_ARGUMENT;
    WitCodeMemoryRequest request;
    if(!wit_user_copy_from(&process->Space,input,(WitU8*)&request,sizeof(request)))return WIT_STATUS_BAD_ADDRESS;
    if(request.Version!=WIT_CODE_MEMORY_VERSION)return WIT_STATUS_UNSUPPORTED;
    if(request.Size!=sizeof(request))return WIT_STATUS_INVALID_ARGUMENT;
    if(request.Operation==WIT_CODE_RESERVE){
        if(request.Address||request.Source||request.Protection)return WIT_STATUS_INVALID_ARGUMENT;
        return wit_user_code_reserve(&process->Space,request.Bytes,request.Alignment,request.Minimum,request.Maximum,result);
    }
    if(request.Alignment||request.Minimum||request.Maximum)return WIT_STATUS_INVALID_ARGUMENT;
    switch(request.Operation){
    case WIT_CODE_VALIDATE:
        if(request.Source)return WIT_STATUS_INVALID_ARGUMENT;
        return wit_user_code_validate(&process->Space,request.Address,request.Bytes,request.Protection);
    case WIT_CODE_RESET_SPARSE:
        if(request.Source||request.Protection)return WIT_STATUS_INVALID_ARGUMENT;
        return wit_user_code_reset_sparse(&process->Space,request.Address,request.Bytes);
    case WIT_CODE_MAP_SPARSE:
        return wit_user_code_map_sparse(&process->Space,request.Address,request.Source,request.Bytes,request.Protection);
    case WIT_CODE_ALIAS:
        return wit_user_code_alias(&process->Space,request.Address,request.Source,request.Bytes,request.Protection);
    case WIT_CODE_PROTECT:
        if(request.Source)return WIT_STATUS_INVALID_ARGUMENT;
        return wit_user_code_protect(&process->Space,request.Address,request.Bytes,request.Protection);
    case WIT_CODE_PUBLISH:
        if(request.Source||request.Protection)return WIT_STATUS_INVALID_ARGUMENT;
        return wit_user_code_publish(&process->Space,request.Address,request.Bytes);
    default:return WIT_STATUS_UNSUPPORTED;
    }
}
