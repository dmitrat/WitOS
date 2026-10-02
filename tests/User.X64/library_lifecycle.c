#include "library.h"
#include "protocol.h"
#include "../../src/Kernel.Arch.X64/user_layout.h"
#pragma optimize("",off)
#define CHECK(v,c) do{if(!(v))return c;}while(0)
#define LOAD(p,h) wit_native_library_load(p,sizeof(p)-1,h)
static int snapshot(WitUserMemoryInfo* v){return wit_native_call(WIT_CALL_MEMORY_QUERY,(WitU64)v,sizeof(*v),WIT_MEMORY_INFO_VERSION,0)==WIT_STATUS_OK;}
static int same(const WitUserMemoryInfo* a,const WitUserMemoryInfo* b){return a->OwnedBytes==b->OwnedBytes&&a->PhysicalAvailableBytes==b->PhysicalAvailableBytes&&a->ReservedBytes==b->ReservedBytes&&a->DynamicCommittedBytes==b->DynamicCommittedBytes&&a->ReservationCount==b->ReservationCount&&a->PrivatePageTableBytes==b->PrivatePageTableBytes;}
static volatile WitU32 workerGate=1;
static WIT_NORETURN void wait_worker(WitU64 value){(void)value;while(!wit_native_try_lock(&workerGate))(void)wit_native_call(WIT_CALL_THREAD_YIELD,0,0,0,0);(void)wit_native_call(WIT_CALL_THREAD_EXIT,42,0,0,0);wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);}
static WIT_NORETURN void never_started(WitU64 value){(void)value;(void)wit_native_call(WIT_CALL_THREAD_EXIT,999,0,0,0);wit_native_fail_fast(WIT_NATIVE_FAIL_FAST_EXIT);}
WitU64 wit_native_library_lifecycle_test(WitU64 mode)
{
    WitUserMemoryInfo before,after;CHECK(snapshot(&before),3201);
    WitU64 root=99,provider=0,second=0,data=0,pc=0;
    if(mode==12){const WitU64 status=LOAD("/native/init.dll",&root);
        if(status==WIT_STATUS_OK)CHECK(wit_native_library_unload(root)==WIT_STATUS_OK,3202);
        else CHECK(status==WIT_STATUS_NO_MEMORY&&root==99,3203);
        CHECK(snapshot(&after)&&same(&before,&after),3204);return status==WIT_STATUS_OK?42:43;
    }
    CHECK(LOAD("/native/WitLibraryFixture.dll",&provider)==WIT_STATUS_OK,3205);
    CHECK(wit_native_library_symbol(provider,"LibraryData",11,0,&data)==WIT_STATUS_OK,3206);
    volatile int* trace=(volatile int*)data;*trace=731;
    WitU64 peer=0,peerResult=0;workerGate=1;
    CHECK(wit_native_call(WIT_CALL_THREAD_CREATE,(WitU64)wait_worker,0,0,&peer)==WIT_STATUS_OK,3232);
    CHECK(LOAD("/native/init.dll",&root)==WIT_STATUS_UNSUPPORTED&&root==99&&*trace==731,3233);
    wit_native_unlock(&workerGate);CHECK(wit_native_call(WIT_CALL_THREAD_JOIN,peer,0,0,&peerResult)==WIT_STATUS_OK&&peerResult==42,3234);
    WitU64 status=LOAD("/native/init.dll",&root);if(status!=WIT_STATUS_OK)return 3300+status;
    CHECK(*trace==7311,3207);
    CHECK(LOAD("/native/init.dll",&second)==WIT_STATUS_OK&&second==root&&*trace==7311,3208);
    CHECK(wit_native_library_unload(second)==WIT_STATUS_OK&&*trace==7311,3209);
    WitU64 thread=0;CHECK(wit_native_call(WIT_CALL_THREAD_CREATE,(WitU64)never_started,0,0,&thread)==WIT_STATUS_UNSUPPORTED,3210);
    CHECK(wit_native_library_unload(root)==WIT_STATUS_OK&&*trace==73112,3211);
    *trace=731;root=99;CHECK(LOAD("/native/initfail.dll",&root)==WIT_STATUS_INITIALIZATION_FAILED&&root==99&&*trace==73134,3212);
    *trace=731;CHECK(LOAD("/native/initparent.dll",&root)==WIT_STATUS_OK&&*trace==73115,3213);
    CHECK(wit_native_library_unload(root)==WIT_STATUS_OK&&*trace==7311562,3214);
    *trace=731;root=99;CHECK(LOAD("/native/initparentfail.dll",&root)==WIT_STATUS_INITIALIZATION_FAILED&&root==99&&*trace==7311562,3231);
    // The reader owns the final graph until it explicitly drives detach.
    *trace=731;CHECK(LOAD("/native/init.dll",&root)==WIT_STATUS_OK,3215);
    CHECK(wit_native_library_symbol(root,"InitValue",9,0,&pc)==WIT_STATUS_OK,3216);
    WitLibraryInfo info;WitU64 reader=0;CHECK(wit_native_library_acquire_reader(pc,&info,&reader)==WIT_STATUS_OK,3217);
    CHECK(wit_native_library_unload(root)==WIT_STATUS_OK&&*trace==7311,3218);
    CHECK(wit_native_library_release_reader(reader)==WIT_STATUS_OK&&*trace==73112,3219);
    // Inspect a genuine protected prepare result before running any callback.
    *trace=731;const char key[]="native/init.dll";WitU64 address=0;
    WitLibraryRequest request={WIT_LIBRARY_VERSION,sizeof(request),WIT_LIBRARY_LOAD,WIT_LIBRARY_USER_LIFECYCLE,0,(WitU64)key,sizeof(key)-1,0,(WitU64)&address,8};
    CHECK(wit_native_call(WIT_CALL_LIBRARY,(WitU64)&request,sizeof(request),0,&root)==WIT_STATUS_OK&&address&&*trace==731,3220);
    const WitLibraryLifecycle* plan=(const WitLibraryLifecycle*)address;
    CHECK(plan->Version==WIT_LIBRARY_VERSION&&plan->Size==sizeof(*plan)&&plan->Attach==1&&plan->Count==1&&plan->Root==root,3221);
    CHECK(wit_native_call(WIT_CALL_MEMORY_PROTECT,address,4096,3,0)==WIT_STATUS_DENIED&&wit_native_call(WIT_CALL_MEMORY_RELEASE,address,0,0,0)==WIT_STATUS_DENIED,3222);
    CHECK(wit_native_call(WIT_CALL_CLOSE,plan->Token,0,0,0)==WIT_STATUS_WRONG_TYPE,3223);
    second=99;CHECK(LOAD("/native/lib.dll",&second)==WIT_STATUS_BUSY&&second==99,3224);
    WitLibraryRequest finish={WIT_LIBRARY_VERSION,sizeof(finish),WIT_LIBRARY_FINISH_LIFECYCLE,0,plan->Token,0,0,2,0,0};
    CHECK(wit_native_call(WIT_CALL_LIBRARY,(WitU64)&finish,sizeof(finish),0,0)==WIT_STATUS_INVALID_ARGUMENT&&*trace==731,3225);
    const WitU64 token=plan->Token;
    CHECK(((int(*)(void*,WitU32,void*))plan->Entries[0].Entry)((void*)plan->Entries[0].Base,1,0)&&*trace==7311,3226);
    finish.Ordinal=1;CHECK(wit_native_call(WIT_CALL_LIBRARY,(WitU64)&finish,sizeof(finish),0,0)==WIT_STATUS_OK,3227);
    CHECK(wit_native_call(WIT_CALL_LIBRARY,(WitU64)&finish,sizeof(finish),0,0)==WIT_STATUS_BAD_HANDLE&&token,3228);
    CHECK(wit_native_library_unload(root)==WIT_STATUS_OK&&*trace==73112,3229);
    CHECK(wit_native_library_unload(provider)==WIT_STATUS_OK&&snapshot(&after)&&same(&before,&after),3230);
    return 42;
}
