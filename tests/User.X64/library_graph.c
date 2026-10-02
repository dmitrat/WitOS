#include "library.h"
#include "protocol.h"
#include "../../src/Kernel.Arch.X64/user_layout.h"
#pragma optimize("",off)
#define CHECK(v,c) do{if(!(v))return c;}while(0)
#define LOAD(p,h) wit_native_library_load(p,sizeof(p)-1,h)
#define FIND(p,h) wit_native_library_find(p,sizeof(p)-1,1,h)
#define SYMBOL(h,n,p) wit_native_library_symbol(h,n,sizeof(n)-1,0,p)
static int snapshot(WitUserMemoryInfo* v){return wit_native_call(WIT_CALL_MEMORY_QUERY,(WitU64)v,sizeof(*v),WIT_MEMORY_INFO_VERSION,0)==WIT_STATUS_OK;}
static int same(const WitUserMemoryInfo* a,const WitUserMemoryInfo* b){return a->OwnedBytes==b->OwnedBytes&&a->PhysicalAvailableBytes==b->PhysicalAvailableBytes&&a->ReservedBytes==b->ReservedBytes&&a->DynamicCommittedBytes==b->DynamicCommittedBytes&&a->ReservationCount==b->ReservationCount&&a->PrivatePageTableBytes==b->PrivatePageTableBytes;}
WitU64 wit_native_library_graph_test(WitU64 mode)
{
    WitUserMemoryInfo before,after;CHECK(snapshot(&before),2801);
    WitU64 root=99,provider=0,function=0;const WitU64 loaded=LOAD("/native/dependent.dll",&root);
    if(mode==9||mode==11){
        if(loaded==WIT_STATUS_OK)CHECK(wit_native_library_unload(root)==WIT_STATUS_OK,2802);
        else CHECK(loaded==WIT_STATUS_NO_MEMORY&&root==99,2803);
        CHECK(snapshot(&after)&&same(&before,&after),2804);return loaded==WIT_STATUS_OK?42:43;
    }
    if(loaded!=WIT_STATUS_OK)return 2900+loaded;
    CHECK(SYMBOL(root,"DependentAdd",&function)==WIT_STATUS_OK&&((int(*)(int,int))function)(731,11)==742,2805);
    WitU64 slotFunction=0;CHECK(SYMBOL(root,"DependentIatSlot",&slotFunction)==WIT_STATUS_OK,2806);
    WitU64 iat=(WitU64)((void*(*)(void))slotFunction)();CHECK(iat&&*(WitU64*)iat,2807);
    if(mode==10){((volatile WitU64*)WIT_GC_INFO_REPORT)[4]=iat;*(volatile WitU64*)iat=0;return 2899;}
    CHECK(FIND("WitLibraryFixture.dll",&provider)==WIT_STATUS_OK,2808);
    WitLibraryInfo info;CHECK(wit_native_library_info(provider,&info)==WIT_STATUS_OK&&info.References==1,2809);
    CHECK(wit_native_library_unload(provider)==WIT_STATUS_OK,2810);
    CHECK(wit_native_library_info(provider,&info)==WIT_STATUS_OK&&info.References==0,2811);
    CHECK(wit_native_library_unload(provider)==WIT_STATUS_DENIED&&((int(*)(int,int))function)(3,4)==7,2812);
    WitU64 a=0,b=0,cycle=0;
    CHECK(LOAD("/native/CycleA.dll",&a)==WIT_STATUS_OK,2813);
    CHECK(SYMBOL(a,"CycleA",&cycle)==WIT_STATUS_OK&&((int(*)(int))cycle)(4)==5,2814);
    WitU64 extra=99;CHECK(LOAD("/native/lib.dll",&extra)==WIT_STATUS_NO_MEMORY&&extra==99,2815);
    CHECK(FIND("CycleB.dll",&b)==WIT_STATUS_OK&&wit_native_library_unload(a)==WIT_STATUS_OK,2816);
    CHECK(((int(*)(int))cycle)(2)==3&&wit_native_library_unload(a)==WIT_STATUS_DENIED,2817);
    CHECK(wit_native_library_unload(b)==WIT_STATUS_OK&&SYMBOL(a,"CycleA",&cycle)==WIT_STATUS_BAD_HANDLE,2818);
    CHECK(((int(*)(int,int))function)(9,10)==19,2819);
    CHECK(wit_native_library_unload(root)==WIT_STATUS_OK,2820);
    CHECK(wit_native_library_info(provider,&info)==WIT_STATUS_BAD_HANDLE,2821);
    CHECK(snapshot(&after)&&same(&before,&after),2822);
    extra=99;CHECK(LOAD("/test/dependent.dll",&extra)==WIT_STATUS_NOT_FOUND&&extra==99,2823);
    CHECK(snapshot(&after)&&same(&before,&after),2824);
    CHECK(LOAD("/native/missing.dll",&extra)==WIT_STATUS_NOT_FOUND&&extra==99,2825);
    CHECK(snapshot(&after)&&same(&before,&after),2826);
    CHECK(LOAD("/native/WitLibraryFixture.dll",&provider)==WIT_STATUS_OK,2827);
    CHECK(snapshot(&before),2828);
    CHECK(LOAD("/native/missing.dll",&extra)==WIT_STATUS_NOT_FOUND&&extra==99,2829);
    CHECK(snapshot(&after)&&same(&before,&after)&&wit_native_library_info(provider,&info)==WIT_STATUS_OK&&info.References==1,2830);
    CHECK(LOAD("/native/dependent.dll",&root)==WIT_STATUS_OK&&wit_native_library_unload(root)==WIT_STATUS_OK,2831);
    CHECK(wit_native_library_info(provider,&info)==WIT_STATUS_OK&&info.References==1&&wit_native_library_unload(provider)==WIT_STATUS_OK,2832);
    return 42;
}
