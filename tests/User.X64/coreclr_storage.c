#include "file.h"
#include "../../src/Kernel.Arch.X64/user_layout.h"
#include "protocol.h"
#include "storage_manifest.h"
extern WitU64 wit_file_views_test(WitU64);
extern WitU64 wit_host_pal_file_probe(void);
extern WitU64 wit_native_paths_test(void);
extern WitU64 wit_native_directory_test(void);
extern WitU64 wit_native_library_test(WitU64);
extern WitU64 wit_native_library_graph_test(WitU64);
extern WitU64 wit_native_library_readers_test(void);
extern WitU64 wit_native_library_lifecycle_test(WitU64);
extern WitU64 wit_native_library_tls_test(WitU64);
static WitU8 buffer[4096];
static WitU32 length(const char* s){WitU32 n=0;while(s[n])++n;return n;}
#define CHECK(value,code) do { if(!(value))return (code); } while(0)
static WitStorageInfo info;
static int unchanged_info(void){for(WitU32 i=0;i<sizeof(info);++i)if(((WitU8*)&info)[i]!=0xA5)return 0;return 1;}
// Keep this validation-only fixture within the explicit plain unwind profile.
#pragma optimize("", off)
static __declspec(noinline) WitU64 namespace_workload(void)
{
    WitU64 copied=0;WitU32 cursor=0;
    const char* roots[]={"app","native","p","shared","test"};
    for(WitU32 i=0;i<5;++i){
        CHECK(wit_native_storage_list(0,0,cursor,&info,&copied)==WIT_STATUS_OK&&copied==sizeof(info)&&info.Version==WIT_STORAGE_QUERY_VERSION&&info.Size==sizeof(info),2101);
        CHECK(info.Kind==(i==2?WIT_STORAGE_FILE:WIT_STORAGE_DIRECTORY)&&info.NameBytes==length(roots[i])&&info.NextCursor>cursor&&info.NextCursor<=WIT_STORAGE_FILE_COUNT,2102);
        for(WitU32 c=0;c<info.NameBytes;++c)CHECK(info.Name[c]==(WitU8)roots[i][c],2103);
        cursor=(WitU32)info.NextCursor;
    }
    for(WitU32 i=0;i<sizeof(info);++i)((WitU8*)&info)[i]=0xA5;
    CHECK(wit_native_storage_list(0,0,cursor,&info,&copied)==WIT_STATUS_OK&&!copied&&unchanged_info(),2104);
    CHECK(wit_native_storage_list("missing",7,0,&info,&copied)==WIT_STATUS_NOT_FOUND&&unchanged_info(),2105);
    CHECK(wit_native_storage_list("test/empty",10,0,&info,&copied)==WIT_STATUS_WRONG_TYPE&&unchanged_info(),2106);
    CHECK(wit_native_storage_stat("missing",7,&info)==WIT_STATUS_NOT_FOUND&&unchanged_info(),2107);
    CHECK(wit_native_storage_stat("../app",6,&info)==WIT_STATUS_INVALID_ARGUMENT&&unchanged_info(),2108);
    CHECK(wit_native_storage_list(0,0,WIT_STORAGE_FILE_COUNT+1,&info,&copied)==WIT_STATUS_INVALID_ARGUMENT&&unchanged_info(),2109);
    CHECK(wit_native_storage_stat(0,0,&info)==WIT_STATUS_OK&&info.Kind==WIT_STORAGE_DIRECTORY&&!info.NameBytes&&!info.Length,2110);
    for(WitU32 file=0;file<WIT_STORAGE_FILE_COUNT;++file){
        const WitU32 size=length(wit_storage_files[file].Name);
        CHECK(wit_native_storage_stat(wit_storage_files[file].Name,size,&info)==WIT_STATUS_OK&&info.Kind==WIT_STORAGE_FILE&&
            info.NameBytes==size&&info.Length==wit_storage_files[file].Bytes&&!info.NextCursor,2111);
        for(WitU32 c=0;c<size;++c)CHECK(info.Name[c]==(WitU8)wit_storage_files[file].Name[c],2112);
    }
    // The complete response crosses an unmapped boundary. No prefix may change.
    WitU8* tail=(WitU8*)(WIT_USER_DATA_END-32);for(WitU32 i=0;i<32;++i)tail[i]=0xA5;
    CHECK(wit_native_storage_stat(0,0,(WitStorageInfo*)tail)==WIT_STATUS_BAD_ADDRESS,2113);
    for(WitU32 i=0;i<32;++i)CHECK(tail[i]==0xA5,2114);
    for(WitU32 i=0;i<sizeof(info);++i)((WitU8*)&info)[i]=0xA5;
    CHECK(wit_native_storage_stat((const char*)WIT_USER_DATA_END-2,4,&info)==WIT_STATUS_BAD_ADDRESS&&unchanged_info(),2115);
    CHECK(wit_native_storage_stat(0,WIT_STORAGE_NAME_BYTES+1,&info)==WIT_STATUS_TOO_LARGE&&unchanged_info(),2116);
    WitStorageQuery query={WIT_STORAGE_QUERY_VERSION,sizeof(query),WIT_STORAGE_STAT,1,0,0,0,(WitU64)&info,sizeof(info),0};
    CHECK(wit_native_call(WIT_CALL_STORAGE_QUERY,(WitU64)&query,sizeof(query),0,&copied)==WIT_STATUS_INVALID_ARGUMENT&&!copied&&unchanged_info(),2117);
    query.Reserved=0;query.BufferBytes=sizeof(info)-1;
    CHECK(wit_native_call(WIT_CALL_STORAGE_QUERY,(WitU64)&query,sizeof(query),0,0)==WIT_STATUS_INVALID_ARGUMENT&&unchanged_info(),2118);
    query.BufferBytes=sizeof(info);query.Path=(WitU64)info.Name;query.PathBytes=3;
    info.Name[0]='a';info.Name[1]='p';info.Name[2]='p';
    CHECK(wit_native_call(WIT_CALL_STORAGE_QUERY,(WitU64)&query,sizeof(query),0,&copied)==WIT_STATUS_OK&&copied==sizeof(info)&&info.Kind==WIT_STORAGE_DIRECTORY&&info.NameBytes==3,2119);
    return 0;
}
#pragma optimize("", on)
static __declspec(noinline) WitU64 file_workload(const WitUserStartup* startup)
{
    const WitUserTestConfig* config=(const WitUserTestConfig*)startup;
    volatile WitU64* report=(volatile WitU64*)WIT_GC_INFO_REPORT;
    report[0]=report[1]=report[2]=report[3]=0;
    WitU64 handle=0,bytes=0,read=0,position=0;
    for(WitU32 file=0;file<WIT_STORAGE_FILE_COUNT;++file){
        CHECK(wit_native_file_open(wit_storage_files[file].Name,length(wit_storage_files[file].Name),&handle)==WIT_STATUS_OK,2001);
        CHECK(wit_native_file_length(handle,&bytes)==WIT_STATUS_OK&&bytes==wit_storage_files[file].Bytes,2002);
        WitU64 hash=14695981039346656037ULL,total=0;
        for(;;){
            CHECK(wit_native_file_read(handle,buffer,sizeof(buffer),&read)==WIT_STATUS_OK&&read<=sizeof(buffer),2003);
            if(!read)break;
            for(WitU32 i=0;i<read;++i)hash=(hash^buffer[i])*1099511628211ULL;
            total+=read;
        }
        CHECK(total==bytes&&hash==wit_storage_files[file].Hash,2004);
        CHECK(wit_native_file_close(handle)==WIT_STATUS_OK,2005);
        ++report[0];report[1]+=bytes;
    }
    const WitU64 namespaceStatus=namespace_workload();if(namespaceStatus)return namespaceStatus;
    const WitU64 tlsStatus=wit_native_library_tls_test(0);if(tlsStatus!=42)return tlsStatus;
    const WitU64 lifecycleStatus=wit_native_library_lifecycle_test(0);if(lifecycleStatus!=42)return lifecycleStatus;
    const WitU64 readerStatus=wit_native_library_readers_test();if(readerStatus!=42)return readerStatus;
    const WitU64 graphStatus=wit_native_library_graph_test(0);if(graphStatus!=42)return graphStatus;
    const WitU64 libraryStatus=wit_native_library_test(0);if(libraryStatus!=42)return libraryStatus;
    const WitU64 directoryStatus=wit_native_directory_test();if(directoryStatus!=42)return directoryStatus;
    const WitU64 pathStatus=wit_native_paths_test();if(pathStatus!=42)return pathStatus;
    const WitU64 palStatus=wit_host_pal_file_probe();if(palStatus!=42)return palStatus;
    const WitU64 viewStatus=wit_file_views_test(0);if(viewStatus!=42)return viewStatus;
    const char name[]="app/CoreClrProbe.dll";
    CHECK(wit_native_file_open(name,sizeof(name)-1,&handle)==WIT_STATUS_OK,2010);
    CHECK(wit_native_file_length(handle,&bytes)==WIT_STATUS_OK&&bytes>4096,2011);
    CHECK(wit_native_file_read_at(handle,buffer,2,0,&read)==WIT_STATUS_OK&&read==2&&buffer[0]=='M'&&buffer[1]=='Z',2012);
    CHECK(wit_native_file_seek(handle,0,WIT_FILE_SEEK_CURRENT,&position)==WIT_STATUS_OK&&!position,2013);
    CHECK(wit_native_file_read(handle,buffer,2,&read)==WIT_STATUS_OK&&read==2&&buffer[0]=='M'&&buffer[1]=='Z',2014);
    CHECK(wit_native_file_seek(handle,~0ULL,WIT_FILE_SEEK_END,&position)==WIT_STATUS_OK&&position==bytes-1,2015);
    CHECK(wit_native_file_read(handle,buffer,sizeof(buffer),&read)==WIT_STATUS_OK&&read==1,2016);
    buffer[0]=0xA5;CHECK(wit_native_file_read(handle,buffer,sizeof(buffer),&read)==WIT_STATUS_OK&&!read&&buffer[0]==0xA5,2017);
    CHECK(wit_native_file_seek(handle,5,WIT_FILE_SEEK_CURRENT,&position)==WIT_STATUS_OK&&position==bytes+5,2018);
    CHECK(wit_native_file_seek(handle,0x8000000000000000ULL,WIT_FILE_SEEK_BEGIN,&position)==WIT_STATUS_INVALID_ARGUMENT&&position==bytes+5,2019);
    CHECK(wit_native_file_seek(handle,0,WIT_FILE_SEEK_CURRENT,&position)==WIT_STATUS_OK&&position==bytes+5,2020);
    CHECK(wit_native_file_seek(handle,0,WIT_FILE_SEEK_BEGIN,&position)==WIT_STATUS_OK,2021);
    WitU8* tail=(WitU8*)(WIT_USER_DATA_END-4);for(WitU32 i=0;i<4;++i)tail[i]=0xA5;
    read=99;
    CHECK(wit_native_file_read(handle,tail,8,&read)==WIT_STATUS_BAD_ADDRESS&&read==99,2022);
    for(WitU32 i=0;i<4;++i)CHECK(tail[i]==0xA5,2023);
    CHECK(wit_native_file_seek(handle,0,WIT_FILE_SEEK_CURRENT,&position)==WIT_STATUS_OK&&!position,2024);
    CHECK(wit_native_file_read(handle,(void*)WIT_USER_INFO,2,&read)==WIT_STATUS_BAD_ADDRESS,2025);
    CHECK(wit_native_file_read(handle,buffer,WIT_FILE_MAX_READ+1,&read)==WIT_STATUS_TOO_LARGE,2026);
    CHECK(wit_native_file_read_at(handle,(void*)WIT_USER_DATA_END,1,bytes,&read)==WIT_STATUS_BAD_ADDRESS,2027);
    CHECK(wit_native_file_read_at(handle,(void*)~0ULL,0,0,&read)==WIT_STATUS_OK&&!read,2028);
    WitFileRequest request={WIT_FILE_IO_VERSION,sizeof(request),WIT_FILE_READ_AT,0,handle,0,2,0,0,0};
    request.Address=(WitU64)&request;
    CHECK(wit_native_call(WIT_CALL_FILE,(WitU64)&request,sizeof(request),0,&read)==WIT_STATUS_OK&&read==2&&((WitU8*)&request)[0]=='M'&&((WitU8*)&request)[1]=='Z',2029);
    CHECK(wit_native_call(WIT_CALL_FILE,WIT_USER_DATA_END-32,sizeof(request),0,0)==WIT_STATUS_BAD_ADDRESS,2030);
    request=(WitFileRequest){WIT_FILE_IO_VERSION,sizeof(request),WIT_FILE_LENGTH,0,handle,0,0,0,1,0};
    CHECK(wit_native_call(WIT_CALL_FILE,(WitU64)&request,sizeof(request),0,0)==WIT_STATUS_INVALID_ARGUMENT,2031);
    request.Reserved0=0;request.Operation=99;
    CHECK(wit_native_call(WIT_CALL_FILE,(WitU64)&request,sizeof(request),0,0)==WIT_STATUS_UNSUPPORTED,2032);
    CHECK(wit_native_file_length(startup->ConsoleHandle,&read)==WIT_STATUS_WRONG_TYPE,2033);
    CHECK(wit_native_file_length(config->ForeignHandle,&read)==WIT_STATUS_BAD_HANDLE,2034);
    WitU64 stale=handle;CHECK(wit_native_file_close(handle)==WIT_STATUS_OK,2035);
    CHECK(wit_native_file_open(name,sizeof(name)-1,&handle)==WIT_STATUS_OK&&handle!=stale,2036);
    CHECK(wit_native_file_length(stale,&read)==WIT_STATUS_BAD_HANDLE&&wit_native_file_close(stale)==WIT_STATUS_BAD_HANDLE,2037);
    CHECK(wit_native_file_close(handle)==WIT_STATUS_OK,2038);
    handle=99;CHECK(wit_native_file_open("missing",7,&handle)==WIT_STATUS_NOT_FOUND&&handle==99,2039);
    CHECK(wit_native_file_open("../bad",6,&handle)==WIT_STATUS_INVALID_ARGUMENT&&handle==99,2040);
    CHECK(wit_native_file_open("app",3,&handle)==WIT_STATUS_WRONG_TYPE&&handle==99,2045);
    WitU64 handles[32];WitU32 opened=0;
    for(;;){
        const WitU64 status=wit_native_file_open(name,sizeof(name)-1,&handle);
        if(status==WIT_STATUS_NO_MEMORY)break;
        CHECK(status==WIT_STATUS_OK&&opened<32,2041);handles[opened++]=handle;
    }
    CHECK(opened>0,2042);
    for(WitU32 i=0;i<opened;++i)CHECK(wit_native_file_close(handles[i])==WIT_STATUS_OK,2043);
    CHECK(wit_native_file_open(name,sizeof(name)-1,&handle)==WIT_STATUS_OK,2044);
    report[2]=opened;report[3]=handle; // Intentionally live: component exit must reap it.
    return 42;
}

WitU64 wit_native_main(const WitUserStartup* startup)
{
    const WitUserTestConfig* config=(const WitUserTestConfig*)startup;
    if(config->Mode==1)return *(volatile WitU8*)config->KernelProbe;
    if(config->Mode==2){*(volatile WitU8*)config->KernelProbe=0;return 42;}
    if(config->Mode==3)return ((WitU64(*)(void))config->KernelProbe)();
    if(config->Mode==13)return wit_native_library_tls_test(1);
    if(config->Mode==12)return wit_native_library_lifecycle_test(12);
    if(config->Mode>=9)return wit_native_library_graph_test(config->Mode);
    if(config->Mode>=7)return wit_native_library_test(config->Mode);
    if(config->Mode>=4)return wit_file_views_test(config->Mode);
    return file_workload(startup);
}
