#include "library.h"
#include "protocol.h"
#include "../../src/Kernel.Arch.X64/user_layout.h"
#pragma optimize("",off)
#define CHECK(v,c) do{if(!(v))return c;}while(0)
static int snapshot(WitUserMemoryInfo* v){return wit_native_call(WIT_CALL_MEMORY_QUERY,(WitU64)v,sizeof(*v),WIT_MEMORY_INFO_VERSION,0)==WIT_STATUS_OK;}
static int same(const WitUserMemoryInfo* a,const WitUserMemoryInfo* b){return a->OwnedBytes==b->OwnedBytes&&a->PhysicalAvailableBytes==b->PhysicalAvailableBytes&&a->ReservedBytes==b->ReservedBytes&&a->DynamicCommittedBytes==b->DynamicCommittedBytes&&a->ReservationCount==b->ReservationCount&&a->PrivatePageTableBytes==b->PrivatePageTableBytes;}
WitU64 wit_native_library_readers_test(void)
{
    WitUserMemoryInfo before,after;CHECK(snapshot(&before),3001);
    const char path[]="/native/dependent.dll";WitU64 root=0,pc=0;
    CHECK(wit_native_library_load(path,sizeof(path)-1,&root)==WIT_STATUS_OK,3002);
    CHECK(wit_native_library_symbol(root,"DependentAdd",12,0,&pc)==WIT_STATUS_OK,3003);
    WitLibraryInfo info;WitU64 reader=99;
    volatile WitU8* tail=(volatile WitU8*)(WIT_USER_DATA_END-16);for(WitU32 i=0;i<16;++i)tail[i]=0xA5;
    CHECK(wit_native_library_acquire_reader(pc,(WitLibraryInfo*)tail,&reader)==WIT_STATUS_BAD_ADDRESS&&reader==99,3004);
    for(WitU32 i=0;i<16;++i)CHECK(tail[i]==0xA5,3005);
    CHECK(wit_native_library_acquire_reader(pc,&info,&reader)==WIT_STATUS_OK&&reader!=root&&info.UnwindBytes&&info.References==1,3006);
    WitU64 other=99;info.References=77;
    CHECK(wit_native_library_acquire_reader(info.Base,&info,&other)==WIT_STATUS_NOT_FOUND&&other==99&&info.References==77,3007);
    CHECK(wit_native_call(WIT_CALL_CLOSE,reader,0,0,0)==WIT_STATUS_WRONG_TYPE&&wit_native_library_unload(reader)==WIT_STATUS_WRONG_TYPE&&wit_native_library_release_reader(root)==WIT_STATUS_WRONG_TYPE,3008);
    WitU64 readers[WIT_LIBRARY_READER_CAPACITY];WitU32 count=0;WitU64 status=WIT_STATUS_OK;
    while(count<WIT_LIBRARY_READER_CAPACITY){other=99;info.References=77;status=wit_native_library_acquire_reader(pc,&info,&other);if(status!=WIT_STATUS_OK)break;readers[count++]=other;}
    CHECK(count>=2&&status==WIT_STATUS_NO_MEMORY&&other==99&&info.References==77,3009);
    for(WitU32 i=0;i<count;++i)CHECK(wit_native_library_release_reader(readers[i])==WIT_STATUS_OK,3010);
    CHECK(wit_native_library_unload(root)==WIT_STATUS_OK&&wit_native_library_unload(root)==WIT_STATUS_DENIED,3011);
    CHECK(wit_native_library_query_reader(reader,&info)==WIT_STATUS_OK&&!info.References&&((int(*)(int,int))pc)(3,4)==7,3012);
    CHECK(wit_native_library_query_reader(reader,(WitLibraryInfo*)tail)==WIT_STATUS_BAD_ADDRESS,3013);
    for(WitU32 i=0;i<16;++i)CHECK(tail[i]==0xA5,3014);
    CHECK(wit_native_library_release_reader(reader)==WIT_STATUS_OK,3015);
    info.References=77;
    CHECK(wit_native_library_query_reader(reader,&info)==WIT_STATUS_BAD_HANDLE&&info.References==77&&wit_native_library_release_reader(reader)==WIT_STATUS_BAD_HANDLE&&wit_native_library_info(root,&info)==WIT_STATUS_BAD_HANDLE,3016);
    CHECK(snapshot(&after)&&same(&before,&after),3017);
    return 42;
}
