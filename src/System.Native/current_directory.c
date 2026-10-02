#include "path.h"
static WitNativePath current={1,1,{'/',0}};
static volatile WitU32 gate;
static void lock(void){while(!wit_native_try_lock(&gate))if(wit_native_call(WIT_CALL_THREAD_YIELD,0,0,0,0)!=WIT_STATUS_OK)wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);}
static void unlock(void){wit_native_unlock(&gate);}
WitU64 wit_native_cwd_get(WitNativePath* output)
{if(!output)return WIT_STATUS_INVALID_ARGUMENT;lock();*output=current;unlock();return WIT_STATUS_OK;}
WitU64 wit_native_path_resolve(const char* input,WitU32 bytes,WitNativePath* output)
{
    WitNativePath snapshot;wit_native_cwd_get(&snapshot);
    return wit_path_resolve(snapshot.Text,snapshot.Bytes,input,bytes,output);
}
WitU64 wit_native_path_full(const char* input,WitU32 bytes,WitNativePath* output)
{
    if(!output)return WIT_STATUS_INVALID_ARGUMENT;
    WitNativePath path;WitU64 status=wit_native_path_resolve(input,bytes,&path);
    if(status!=WIT_STATUS_OK)return status;
    WitStorageInfo info;status=wit_native_storage_stat(path.Text+1,path.Bytes-1,&info);
    if(status!=WIT_STATUS_OK)return status;
    if(path.RequireDirectory&&info.Kind!=WIT_STORAGE_DIRECTORY)return WIT_STATUS_WRONG_TYPE;
    *output=path;return WIT_STATUS_OK;
}
WitU64 wit_native_cwd_set(const char* input,WitU32 bytes)
{
    WitNativePath path;lock();
    WitU64 status=wit_path_resolve(current.Text,current.Bytes,input,bytes,&path);
    if(status==WIT_STATUS_OK){
        WitStorageInfo info;status=wit_native_storage_stat(path.Text+1,path.Bytes-1,&info);
        if(status==WIT_STATUS_OK&&info.Kind!=WIT_STORAGE_DIRECTORY)status=WIT_STATUS_WRONG_TYPE;
        if(status==WIT_STATUS_OK){path.RequireDirectory=1;current=path;}
    }
    unlock();return status;
}
