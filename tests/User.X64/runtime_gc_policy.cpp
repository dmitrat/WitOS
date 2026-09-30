#include "gcenv.witos.h"
#include "pal.witos.h"
#include "native_security.h"
#include "tls.h"
#include "protocol.h"
#include <errno.h>
extern "C" void wit_native_gc_breakpoint_resume();
static bool snapshot(WitUserMemoryInfo& info)
{
    return wit_native_call(WIT_CALL_MEMORY_QUERY,(WitU64)&info,sizeof(info),WIT_MEMORY_INFO_VERSION,nullptr)==WIT_STATUS_OK;
}
static bool verify()
{
    WitUserMemoryInfo before,after;
    if(!snapshot(before)||GCToOSInterface::SupportsWriteWatch())return false;
    void* pages[2]={(void*)0x1234,(void*)0x5678};uintptr_t count=2;
    for(unsigned reset=0;reset<2;++reset)
        if(GCToOSInterface::GetWriteWatch(reset!=0,(void*)~0ULL,SIZE_MAX,pages,&count)||count!=2||pages[0]!=(void*)0x1234||pages[1]!=(void*)0x5678||
            GCToOSInterface::GetWriteWatch(reset!=0,nullptr,0,nullptr,nullptr))return false;
    const size_t sizes[]={0,4096,2*1024*1024,SIZE_MAX};
    const uint16_t nodes[]={0,1,NUMA_NODE_UNDEFINED};
    for(const auto size:sizes)for(const auto node:nodes)if(GCToOSInterface::VirtualReserveAndCommitLargePages(size,node))return false;
    if(GCToOSInterface::VirtualReserve(4096,0,VirtualReserveFlags::WriteWatch,NUMA_NODE_UNDEFINED))return false;
    if(!snapshot(after)||after.OwnedBytes!=before.OwnedBytes||after.ReservedBytes!=before.ReservedBytes||after.DynamicCommittedBytes!=before.DynamicCommittedBytes||after.ReservationCount!=before.ReservationCount)return false;
    void* memory=GCToOSInterface::VirtualReserve(4096,0,VirtualReserveFlags::None,NUMA_NODE_UNDEFINED);
    if(!memory||!GCToOSInterface::VirtualCommit(memory,4096,NUMA_NODE_UNDEFINED))return false;
    *(WitU64*)memory=0xFEDCBA9876543210ULL;
    if(GCToOSInterface::GetWriteWatch(true,memory,4096,pages,&count)||count!=2||pages[0]!=(void*)0x1234||*(WitU64*)memory!=0xFEDCBA9876543210ULL)return false;
    if(wit_native_call(WIT_CALL_MEMORY_PROTECT,(WitU64)memory,4096,WIT_MEMORY_NONE,nullptr)!=WIT_STATUS_OK||
        GCToOSInterface::GetWriteWatch(false,memory,4096,pages,&count)||count!=2||
        wit_native_call(WIT_CALL_MEMORY_PROTECT,(WitU64)memory,4096,WIT_MEMORY_READ|WIT_MEMORY_WRITE,nullptr)!=WIT_STATUS_OK||
        *(WitU64*)memory!=0xFEDCBA9876543210ULL||!GCToOSInterface::VirtualRelease(memory,4096))return false;
    return true;
}
static WitU64 worker(WitU64)
{
    SetLastError(8200);errno=8300;
    return verify()&&GetLastError()==8200&&errno==8300?WIT_TEST_EXIT_CODE:3401;
}
extern "C" WitU64 wit_test_gc_policy(const WitUserStartup* startup,WitU64 mode)
{
    auto report=(WitU64*)WIT_GC_INFO_REPORT;report[0]=mode;report[1]=274877906944ULL;
    wit_native_security_initialize_system();wit_native_process_image_initialize(startup);
    if(mode==85){report[2]=(WitU64)&wit_native_gc_breakpoint_resume;GCToOSInterface::DebugBreak();return 3402;}
    if(mode==84){
        void* memory=GCToOSInterface::VirtualReserve(4096,0,VirtualReserveFlags::None,NUMA_NODE_UNDEFINED);
        if(!memory||!GCToOSInterface::VirtualCommit(memory,4096,NUMA_NODE_UNDEFINED))return 3403;
        *(WitU64*)memory=0xFEDCBA9876543210ULL;report[2]=(WitU64)memory;
        GCToOSInterface::ResetWriteWatch(memory,4096);return 3404;
    }
    const bool tls=mode==82;if(tls)wit_native_tls_initialize(startup);
    SetLastError(0x67812345);if(tls)errno=217;
    if(!verify())return 3405;
    if(tls){
        for(unsigned i=0;i<3;++i){WitU64 handle=0,result=0;
            if(wit_native_thread_create(worker,i,&handle)!=WIT_STATUS_OK||wit_native_call(WIT_CALL_THREAD_JOIN,handle,0,0,&result)!=WIT_STATUS_OK||result!=WIT_TEST_EXIT_CODE)return 3406;
        }
        wit_native_tls_leave();
    }
    if(GetLastError()!=0x67812345||(tls&&errno!=217))return 3407;
    return WIT_TEST_EXIT_CODE;
}
