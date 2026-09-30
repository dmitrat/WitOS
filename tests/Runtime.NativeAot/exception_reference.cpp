#include <windows.h>
#include <stdio.h>
static LONG order[16],count;
static bool payload;
static PVOID selfHandle;
static LONG record(LONG id,EXCEPTION_POINTERS* info)
{
    if(count>=16)return EXCEPTION_CONTINUE_SEARCH;
    order[count++]=id;
    if(info&&info->ExceptionRecord&&info->ContextRecord&&info->ExceptionRecord->ExceptionCode==0xE0421234&&
       info->ExceptionRecord->NumberParameters==2&&info->ExceptionRecord->ExceptionInformation[0]==0x11223344&&
       info->ExceptionRecord->ExceptionInformation[1]==0x55667788)payload=true;
    return EXCEPTION_CONTINUE_SEARCH;
}
static LONG CALLBACK first(EXCEPTION_POINTERS* info){return record(1,info);}
static LONG CALLBACK last(EXCEPTION_POINTERS* info){return record(2,info);}
static LONG CALLBACK front(EXCEPTION_POINTERS* info){return record(3,info);}
static LONG CALLBACK finish(EXCEPTION_POINTERS* info){record(4,info);return EXCEPTION_CONTINUE_EXECUTION;}
static LONG CALLBACK self_remove(EXCEPTION_POINTERS* info)
{
    record(5,info);
    if(!RemoveVectoredExceptionHandler(selfHandle))return EXCEPTION_CONTINUE_SEARCH;
    selfHandle=nullptr;return EXCEPTION_CONTINUE_SEARCH;
}
static bool sequence(const LONG* expected,unsigned n)
{
    if(count!=(LONG)n)return false;
    for(unsigned i=0;i<n;++i)if(order[i]!=expected[i])return false;
    return true;
}
static unsigned softwareMode,softwareCalls,innerCalls,noncontinuableCalls;
static bool softwarePayload;
static DWORD caught;
static LONG CALLBACK software(EXCEPTION_POINTERS* p)
{
    const auto& e=*p->ExceptionRecord;
    printf("software mode=%u code=%08lx flags=%08lx count=%lu\n",softwareMode,e.ExceptionCode,e.ExceptionFlags,e.NumberParameters);
    if(e.ExceptionCode==EXCEPTION_NONCONTINUABLE_EXCEPTION){++noncontinuableCalls;return EXCEPTION_CONTINUE_SEARCH;}
    if(e.ExceptionCode==0xE0421235){++innerCalls;return EXCEPTION_CONTINUE_EXECUTION;}
    if(e.ExceptionCode!=0xE0421234)return EXCEPTION_CONTINUE_SEARCH;
    ++softwareCalls;
    if(softwareMode==0){softwarePayload=e.NumberParameters==15;for(unsigned i=0;i<e.NumberParameters;++i)softwarePayload=softwarePayload&&e.ExceptionInformation[i]==0xA000+i;}
    if(softwareMode==1)softwarePayload=e.NumberParameters==0;
    if(softwareMode==2)RaiseException(0xE0421235,0,0,nullptr);
    return softwareMode==4?EXCEPTION_CONTINUE_SEARCH:EXCEPTION_CONTINUE_EXECUTION;
}
static LONG filter(EXCEPTION_POINTERS* p){caught=p->ExceptionRecord->ExceptionCode;return EXCEPTION_EXECUTE_HANDLER;}
static bool software_tests()
{
    auto handle=AddVectoredExceptionHandler(1,software);if(!handle)return false;
    ULONG_PTR args[15];for(unsigned i=0;i<15;++i)args[i]=0xA000+i;
    softwareMode=0;RaiseException(0xE0421234,0,15,args);if(!softwarePayload)return false;
    softwareMode=1;RaiseException(0xE0421234,0,0xFFFFFFFF,nullptr);if(!softwarePayload)return false;
    softwareMode=2;RaiseException(0xE0421234,0,0,nullptr);if(innerCalls!=1)return false;
    softwareMode=3;RaiseException(0xE0421234,EXCEPTION_NONCONTINUABLE,0,nullptr);
    if(noncontinuableCalls||softwareCalls!=4)return false;
    softwareMode=4;
    __try {
        __try {RaiseException(0xE0421234,EXCEPTION_NONCONTINUABLE,0,nullptr);}
        __except(GetExceptionCode()==0xE0421234?EXCEPTION_CONTINUE_EXECUTION:EXCEPTION_CONTINUE_SEARCH) {}
    }
    __except(filter(GetExceptionInformation())) {}
    printf("caught=%08lx noncontinuable=%u software=%u inner=%u\n",caught,noncontinuableCalls,softwareCalls,innerCalls);
    if(caught!=EXCEPTION_NONCONTINUABLE_EXCEPTION||noncontinuableCalls!=1||softwareCalls!=5)return false;
    if(!RemoveVectoredExceptionHandler(handle))return false;
    puts("PASS: Windows software parameters, nested continuation and noncontinuable search (HOSTED only)");return true;
}
int main()
{
    ULONG_PTR arguments[]={0x11223344,0x55667788};
    const PVOID a=AddVectoredExceptionHandler(1,first),b=AddVectoredExceptionHandler(0,last),c=AddVectoredExceptionHandler(7,front),d=AddVectoredExceptionHandler(0,finish);
    if(!a||!b||!c||!d||a==b||b==c||c==d)return 1;
    RaiseException(0xE0421234,0,2,arguments);const LONG all[]={3,1,2,4};
    if(!sequence(all,4)||!payload)return 2;
    if(!RemoveVectoredExceptionHandler(c)||RemoveVectoredExceptionHandler(c))return 3;
    count=0;RaiseException(0xE0421234,0,2,arguments);const LONG remaining[]={1,2,4};
    if(!sequence(remaining,3))return 4;
    selfHandle=AddVectoredExceptionHandler(1,self_remove);if(!selfHandle)return 5;
    count=0;RaiseException(0xE0421234,0,2,arguments);const LONG self[]={5,1,2,4};
    if(!sequence(self,4)||selfHandle)return 6;
    count=0;RaiseException(0xE0421234,0,2,arguments);if(!sequence(remaining,3))return 7;
    if(!RemoveVectoredExceptionHandler(d)||!RemoveVectoredExceptionHandler(b)||!RemoveVectoredExceptionHandler(a))return 8;
    if(!software_tests())return 9;
    puts("PASS: Windows VEH ordering, payload, continuation, stale removal and self-removal (HOSTED only)");return 0;
}
