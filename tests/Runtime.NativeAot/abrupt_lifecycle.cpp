#include "common.h"
#include "gcenv.h"
#include "regdisplay.h"
#include "StackFrameIterator.h"
#include "thread.h"
#include "threadstore.h"
#include "threadstore.inl"
#include "thread.inl"
#include "pal.witos.h"
#include "tls.h"
#include "native_process.h"
#include <stdlib.h>
extern "C" [[noreturn]] void wit_runtime_invalid_instruction();
extern "C" { volatile WitU64 wit_runtime_abrupt_report[4]={}; }
namespace {
using Callback=int(*)(int);
struct Start {Callback Managed;HANDLE Ready;HANDLE Observer;bool Fault;};
struct Trace {bool Armed=false;~Trace(){if(Armed)++wit_runtime_abrupt_report[1];}};
thread_local Trace trace;
void check(bool ok){if(!ok)wit_native_fail_fast(0xFFFF0101);}
void cleanup(void*){++wit_runtime_abrupt_report[2];}
void process_cleanup(){++wit_runtime_abrupt_report[3];}
WitU64 worker(WitU64 argument)
{
    auto& start=*(Start*)argument;
    ThreadStore::AttachCurrentThread();
    auto thread=ThreadStore::RawGetCurrentThread();
    check(thread->IsInitialized()&&!thread->IsDetached());
    check(wit_native_thread_on_cleanup(cleanup,nullptr)==WIT_STATUS_OK);
    trace.Armed=true;
    check(start.Managed(215)==215);
    auto alloc=thread->GetAllocContext();
    check(alloc->alloc_ptr&&alloc->alloc_limit>alloc->alloc_ptr);
    check(DuplicateHandle(GetCurrentProcess(),GetCurrentThread(),GetCurrentProcess(),&start.Observer,0,FALSE,DUPLICATE_SAME_ACCESS)!=0);
    wit_runtime_abrupt_report[0]=1;
    check(SetEvent(start.Ready)!=0);
    if(start.Fault)wit_runtime_invalid_instruction();
    // Deliberate bypass of System.Native TLS/runtime exit notification.
    (void)wit_native_call(WIT_CALL_THREAD_EXIT,0x1234,0,0,nullptr);
    wit_native_fail_fast(0xFFFF0102);
}
int run(Callback callback,bool detached,bool fault)
{
    check(wit_native_tls_code_pointer((WitU64)callback)!=0);
    check(atexit(process_cleanup)==0);
    Start start={callback,PalCreateEventW(nullptr,FALSE,FALSE,nullptr),nullptr,fault};
    check(start.Ready!=nullptr);
    WitU64 join=0;
    check((detached?wit_native_thread_create_detached(worker,(WitU64)&start):wit_native_thread_create(worker,(WitU64)&start,&join))==WIT_STATUS_OK);
    check(PalWaitForSingleObjectEx(start.Ready,10000,FALSE)==WAIT_OBJECT_0);
    check(WaitForMultipleObjectsEx(1,&start.Observer,FALSE,10000,FALSE)==WAIT_OBJECT_0);
    // A full-runtime component must never continue with the dead record/TLS.
    wit_native_fail_fast(0xFFFF0103);
}
}
extern "C" int wit_runtime_raw_join(Callback callback){return run(callback,false,false);}
extern "C" int wit_runtime_raw_detached(Callback callback){return run(callback,true,false);}
extern "C" int wit_runtime_fault_join(Callback callback){return run(callback,false,true);}
extern "C" int wit_runtime_fault_detached(Callback callback){return run(callback,true,true);}
