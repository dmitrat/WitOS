#include "file.h"
static WitU64 perform(WitFileRequest* request,WitU64* output)
{
    WitU64 result=0;
    request->Version=WIT_FILE_IO_VERSION;request->Size=sizeof(*request);
    const WitU64 status=wit_native_call(WIT_CALL_FILE,(WitU64)request,sizeof(*request),0,&result);
    if(status==WIT_STATUS_OK&&output)*output=result;
    return status;
}
WitU64 wit_native_file_open(const char* name,WitU32 bytes,WitU64* handle)
{if(!handle)return WIT_STATUS_INVALID_ARGUMENT;WitFileRequest r={0};r.Operation=WIT_FILE_OPEN;r.Address=(WitU64)name;r.Bytes=bytes;return perform(&r,handle);}
WitU64 wit_native_file_length(WitU64 handle,WitU64* bytes)
{WitFileRequest r={0};r.Operation=WIT_FILE_LENGTH;r.Handle=handle;return perform(&r,bytes);}
WitU64 wit_native_file_read(WitU64 handle,void* buffer,WitU32 bytes,WitU64* read)
{WitFileRequest r={0};r.Operation=WIT_FILE_READ;r.Handle=handle;r.Address=(WitU64)buffer;r.Bytes=bytes;return perform(&r,read);}
WitU64 wit_native_file_read_at(WitU64 handle,void* buffer,WitU32 bytes,WitU64 offset,WitU64* read)
{WitFileRequest r={0};r.Operation=WIT_FILE_READ_AT;r.Handle=handle;r.Address=(WitU64)buffer;r.Bytes=bytes;r.Offset=offset;return perform(&r,read);}
WitU64 wit_native_file_seek(WitU64 handle,WitU64 offset,WitU32 origin,WitU64* position)
{WitFileRequest r={0};r.Operation=WIT_FILE_SEEK;r.Handle=handle;r.Offset=offset;r.Flags=origin;return perform(&r,position);}
WitU64 wit_native_file_close(WitU64 handle){return wit_native_call(WIT_CALL_CLOSE,handle,0,0,0);}

static WitU64 query(const char* path,WitU32 bytes,WitU32 operation,WitU32 cursor,WitStorageInfo* info,WitU64* copied)
{
    const WitStorageQuery request={WIT_STORAGE_QUERY_VERSION,sizeof(request),operation,0,(WitU64)path,bytes,cursor,(WitU64)info,sizeof(*info),0};
    WitU64 result=0;const WitU64 status=wit_native_call(WIT_CALL_STORAGE_QUERY,(WitU64)&request,sizeof(request),0,&result);
    if(status==WIT_STATUS_OK&&copied)*copied=result;
    return status;
}
WitU64 wit_native_storage_stat(const char* path,WitU32 bytes,WitStorageInfo* info)
{return query(path,bytes,WIT_STORAGE_STAT,0,info,0);}
WitU64 wit_native_storage_list(const char* path,WitU32 bytes,WitU32 cursor,WitStorageInfo* info,WitU64* copied)
{return query(path,bytes,WIT_STORAGE_LIST,cursor,info,copied);}
