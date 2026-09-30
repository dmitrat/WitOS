#include "user.h"
#include "witos/platform.h"
static void copy_fx(WitU8* output,const WitU8* input)
{
    for(WitU32 i=0;i<512;++i)output[i]=0;
    // REX.W FXSAVE: FCW/FSW/FTW, FOP, 64-bit FIP/FDP, MXCSR/mask.
    for(WitU32 i=0;i<5;++i)output[i]=input[i];
    for(WitU32 i=6;i<32;++i)output[i]=input[i];
    *(WitU32*)&output[28]=wit_x64_mxcsr_mask();
    output[7]&=7; // FOP bits 15:11 are reserved.
    for(WitU32 reg=0;reg<8;++reg)
        for(WitU32 i=0;i<10;++i)output[32+reg*16+i]=input[32+reg*16+i];
    for(WitU32 i=160;i<416;++i)output[i]=input[i]; // XMM0-15.
}
void wit_x64_context_copy_self_test(void)
{
    WitU8 source[512],destination[512];
    for(WitU32 i=0;i<512;++i){source[i]=0xA5;destination[i]=0xCC;}
    copy_fx(destination,source);
    for(WitU32 i=0;i<512;++i){
        const int defined=i<5||(i>=6&&i<32)||(i>=32&&i<160&&(i-32)%16<10)||(i>=160&&i<416);
        const WitU8 expected=i>=28&&i<32?(WitU8)(wit_x64_mxcsr_mask()>>(8*(i-28))):i==7?5:defined?0xA5:0;
        if(destination[i]!=expected||source[i]!=0xA5)wit_panic("FXSAVE context sanitization failed");
    }
    wit_console_write("[TEST-PASS] Cpu.ContextSanitization\n");
}
static WitInterruptContext* owned_context(WitUserProcess* process,WitUserThread* target)
{
    const WitU32 index=(WitU32)(target-process->Threads);
    const WitU64 low=(WitU64)wit_x64_user_kernel_stacks[process->Slot][index]+4096;
    const WitU64 high=low+WIT_KERNEL_STACK_SIZE;
    WitInterruptContext* saved=target->Context;
    if(((WitU64)saved&15)||(WitU64)saved<low||(WitU64)saved>high-sizeof(*saved)||
        saved->Cs!=WIT_USER_CS||saved->Ss!=WIT_USER_SS||saved->Rsp<target->StackBottom||saved->Rsp>=target->StackTop||
        !wit_user_space_physical(&process->Space,saved->Rip,0,1))wit_panic("Context snapshot lost owning kernel/user frame");
    return saved;
}
static void describe(WitThreadContext* snapshot,const WitUserThread* target)
{
    snapshot->Version=WIT_THREAD_CONTEXT_VERSION;snapshot->Size=sizeof(*snapshot);
    snapshot->ThreadId=target->Handle;snapshot->StackLow=target->StackBottom;snapshot->StackHigh=target->StackTop;
    snapshot->State=target->State==WitThreadRunning?WIT_THREAD_CONTEXT_RUNNING:target->State==WitThreadWaiting?WIT_THREAD_CONTEXT_WAITING:WIT_THREAD_CONTEXT_READY;
    snapshot->SuspendCount=target->SuspendCount;
    snapshot->Flags=WIT_THREAD_CONTEXT_FXSAVE64|(target->SuspendCount?WIT_THREAD_CONTEXT_SUSPENDED:0)|
        (target->WaitKind!=WitWaitNone?WIT_THREAD_CONTEXT_SERVICE_ACTIVE:0)|(target->Exception.Token?WIT_THREAD_CONTEXT_EXCEPTION_ACTIVE:0);
    *(WitU32*)&snapshot->FxState[28]=wit_x64_mxcsr_mask();
}
void wit_user_context_snapshot(WitThreadContext* snapshot,const WitUserThread* target,const WitInterruptContext* saved)
{
    for(WitU32 i=0;i<sizeof(*snapshot);++i)((WitU8*)snapshot)[i]=0;
    describe(snapshot,target);
    snapshot->Rip=saved->Rip;snapshot->Rsp=saved->Rsp;snapshot->Rflags=saved->Rflags;snapshot->Cs=saved->Cs;snapshot->Ss=saved->Ss;
    snapshot->Rax=saved->Rax;snapshot->Rbx=saved->Rbx;snapshot->Rcx=saved->Rcx;snapshot->Rdx=saved->Rdx;snapshot->Rbp=saved->Rbp;
    snapshot->Rsi=saved->Rsi;snapshot->Rdi=saved->Rdi;snapshot->R8=saved->R8;snapshot->R9=saved->R9;snapshot->R10=saved->R10;
    snapshot->R11=saved->R11;snapshot->R12=saved->R12;snapshot->R13=saved->R13;snapshot->R14=saved->R14;snapshot->R15=saved->R15;
    copy_fx(snapshot->FxState,saved->FxState);
}
WitU64 wit_user_thread_context_get(WitUserProcess* process,WitU64 reference,WitU64 address,WitU64 size)
{
    WitUserThread* target=0;
    if(size!=sizeof(WitThreadContext))return WIT_STATUS_INVALID_ARGUMENT;
    const WitU64 status=wit_user_reference_target(process,reference,WIT_THREAD_REFERENCE_GET_CONTEXT,&target);
    if(status!=WIT_STATUS_OK)return status;
    if(!wit_x64_context_profile_supported())return WIT_STATUS_UNSUPPORTED;
    const WitInterruptContext* saved=owned_context(process,target);
    WitThreadContext snapshot;wit_user_context_snapshot(&snapshot,target,saved);
    // IF remains clear through snapshot, whole destination validation and copy.
    return wit_user_copy_to(&process->Space,address,(const WitU8*)&snapshot,sizeof(snapshot))?WIT_STATUS_OK:WIT_STATUS_BAD_ADDRESS;
}

WitU64 wit_user_context_validate(WitUserProcess* process,WitUserThread* target,const WitThreadContext* input,int restoring)
{
    if(input->Version!=WIT_THREAD_CONTEXT_VERSION)return WIT_STATUS_UNSUPPORTED;
    if(input->Size!=sizeof(*input)||input->ThreadId!=target->Handle||input->StackLow!=target->StackBottom||input->StackHigh!=target->StackTop||
        input->Reserved||input->State!=(restoring?WIT_THREAD_CONTEXT_RUNNING:WIT_THREAD_CONTEXT_READY)||
        input->Flags!=(WIT_THREAD_CONTEXT_FXSAVE64|(restoring?0:WIT_THREAD_CONTEXT_SUSPENDED)|(target->Exception.Token?WIT_THREAD_CONTEXT_EXCEPTION_ACTIVE:0))||input->SuspendCount!=target->SuspendCount||
        input->Cs!=WIT_USER_CS||input->Ss!=WIT_USER_SS||(input->Rflags&~WIT_CONTEXT_USER_FLAGS)||(input->Rflags&0x202)!=0x202)
        return WIT_STATUS_INVALID_ARGUMENT;
    if(input->Rsp<target->StackBottom||input->Rsp>=target->StackTop||!wit_user_space_physical(&process->Space,input->Rsp,1,0)||
        !wit_user_space_physical(&process->Space,input->Rip,0,1))return WIT_STATUS_BAD_ADDRESS;
    if(!wit_x64_mxcsr_mask()||(*(const WitU32*)&input->FxState[24]&~wit_x64_mxcsr_mask()))return WIT_STATUS_INVALID_ARGUMENT;
    WitU8 canonical[512];copy_fx(canonical,input->FxState);
    for(WitU32 i=0;i<512;++i)if(canonical[i]!=input->FxState[i])return WIT_STATUS_INVALID_ARGUMENT;
    return WIT_STATUS_OK;
}
void wit_user_context_commit(WitInterruptContext* output,const WitThreadContext* input)
{
    output->Rip=input->Rip;output->Rsp=input->Rsp;output->Rflags=input->Rflags;output->Cs=input->Cs;output->Ss=input->Ss;
    output->Rax=input->Rax;output->Rbx=input->Rbx;output->Rcx=input->Rcx;output->Rdx=input->Rdx;output->Rbp=input->Rbp;
    output->Rsi=input->Rsi;output->Rdi=input->Rdi;output->R8=input->R8;output->R9=input->R9;output->R10=input->R10;
    output->R11=input->R11;output->R12=input->R12;output->R13=input->R13;output->R14=input->R14;output->R15=input->R15;
    copy_fx(output->FxState,input->FxState);
}
WitU64 wit_user_thread_context_set(WitUserProcess* process,WitU64 reference,WitU64 address,WitU64 size)
{
    WitUserThread* target=0;
    if(size!=sizeof(WitThreadContext))return WIT_STATUS_INVALID_ARGUMENT;
    WitU64 status=wit_user_reference_target(process,reference,WIT_THREAD_REFERENCE_SET_CONTEXT,&target);
    if(status!=WIT_STATUS_OK)return status;
    if(target==&process->Threads[process->CurrentThread]||!target->SuspendCount||target->State!=WitThreadReady||target->WaitKind!=WitWaitNone)return WIT_STATUS_BUSY;
    if(target->Exception.Token||wit_user_stack_leased(process,target->Handle,0))return WIT_STATUS_BUSY;
    if(!wit_x64_context_profile_supported())return WIT_STATUS_UNSUPPORTED;
    WitThreadContext input;
    if(!wit_user_copy_from(&process->Space,address,(WitU8*)&input,sizeof(input)))return WIT_STATUS_BAD_ADDRESS;
    status=wit_user_context_validate(process,target,&input,0);
    if(status!=WIT_STATUS_OK)return status;
    wit_user_context_commit(owned_context(process,target),&input);
    return WIT_STATUS_OK;
}
WitU64 wit_user_thread_context_restore(WitUserProcess* process,WitU64 address,WitU64 size,WitU64 version)
{
    WitUserThread* target=&process->Threads[process->CurrentThread];
    if(version!=WIT_THREAD_CONTEXT_VERSION)return WIT_STATUS_UNSUPPORTED;
    if(size!=sizeof(WitThreadContext))return WIT_STATUS_INVALID_ARGUMENT;
    if(target->SuspendCount||target->State!=WitThreadRunning||target->WaitKind!=WitWaitNone)return WIT_STATUS_BUSY;
    if(target->Exception.Token||wit_user_stack_leased(process,target->Handle,0)||wit_user_stack_leases_owned(process,target->Handle))return WIT_STATUS_BUSY;
    if(!wit_x64_context_profile_supported())return WIT_STATUS_UNSUPPORTED;
    WitThreadContext input;
    if(!wit_user_copy_from(&process->Space,address,(WitU8*)&input,sizeof(input)))return WIT_STATUS_BAD_ADDRESS;
    const WitU64 status=wit_user_context_validate(process,target,&input,1);
    if(status!=WIT_STATUS_OK)return status;
    wit_user_context_commit(owned_context(process,target),&input);
    return WIT_STATUS_OK;
}

WitU64 wit_user_thread_context_metadata(WitUserProcess* process,WitU64 reference,WitU64 address,WitU64 size)
{
    WitUserThread* target=0;
    if(size!=sizeof(WitThreadContext))return WIT_STATUS_INVALID_ARGUMENT;
    const WitU64 status=wit_user_reference_target(process,reference,WIT_THREAD_REFERENCE_SET_CONTEXT,&target);
    if(status!=WIT_STATUS_OK)return status;
    if(!wit_x64_context_profile_supported())return WIT_STATUS_UNSUPPORTED;
    WitThreadContext metadata={0};describe(&metadata,target);
    // SET-only capability exposes validation metadata, never another thread's registers.
    return wit_user_copy_to(&process->Space,address,(const WitU8*)&metadata,sizeof(metadata))?WIT_STATUS_OK:WIT_STATUS_BAD_ADDRESS;
}
