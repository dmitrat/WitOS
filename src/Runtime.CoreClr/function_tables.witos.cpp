#include "function_tables.witos.h"
namespace {
struct Guard {WitFunctionTableHooks& Hooks;Guard(WitFunctionTableHooks& h):Hooks(h){Hooks.Enter();}~Guard(){Hooks.Leave();}};
bool overlap(uint64_t a,uint64_t n,uint64_t b,uint64_t m){return a<b+m&&b<a+n;}
bool valid(const WitRuntimeFunction& function,uint64_t length)
{return function.BeginAddress<function.EndAddress&&function.EndAddress<=length&&(function.UnwindData&3)==0;}
}
bool WitFunctionTables::Add(uint64_t key,uint64_t base,uint64_t length,WitRuntimeFunction* table,uint32_t count,WitFunctionCallback callback,void* context)
{
    if(!key||!base||!length||length>0x7fffffffULL||base>UINT64_MAX-length)return false;
    Guard guard(hooks);
    Record* free=nullptr;
    for(auto& record:records){
        if(!record.Generation){if(!free)free=&record;continue;}
        if(record.Key==key||overlap(base,length,record.Base,record.Length))return false;
    }
    if(!free||!nextGeneration)return false;
    *free={key,base,length,nextGeneration++,table,count,0,callback,context,false};return true;
}
bool WitFunctionTables::AddTable(WitRuntimeFunction* table,uint32_t count,uint64_t base,uint64_t length)
{
    if(!table||!count||count>4096||!hooks.Readable((uint64_t)table,(size_t)count*sizeof(*table)))return false;
    uint32_t previous=0;
    for(uint32_t i=0;i<count;++i){
        if(!valid(table[i],length)||table[i].BeginAddress<previous)return false;
        previous=table[i].EndAddress;
    }
    return Add((uint64_t)table,base,length,table,count,nullptr,nullptr);
}
bool WitFunctionTables::AddCallback(uint64_t key,uint64_t base,uint64_t length,WitFunctionCallback callback,void* context)
{
    if((key&3)!=3||!callback||!hooks.Executable((uint64_t)callback,1))return false;
    return Add(key,base,length,nullptr,0,callback,context);
}
bool WitFunctionTables::Acquire(uint64_t pc,WitFunctionLease* result)
{
    if(!result)return false;
    Record snapshot{};uint64_t token=0;
    {
        Guard guard(hooks);
        for(auto& record:records)if(record.Generation&&!record.Retiring&&pc>=record.Base&&pc-record.Base<record.Length){
            if(record.Readers==UINT32_MAX||!nextReader)return false;
            Reader* available=nullptr;for(auto& reader:readers)if(!reader.Token){available=&reader;break;}
            if(!available)return false;
            token=nextReader++;*available={token,record.Generation};
            ++record.Readers;snapshot=record;break;
        }
    }
    if(!snapshot.Generation)return false;
    WitFunctionLease lease={token,snapshot.Generation,snapshot.Base,snapshot.Length,nullptr};
    WitRuntimeFunction* selected=nullptr;
    if(!Lookup(lease,pc,&selected)){(void)Release(&lease);return false;}
    lease.Entry=selected;*result=lease;return true;
}
bool WitFunctionTables::Lookup(const WitFunctionLease& lease,uint64_t pc,WitRuntimeFunction** result)
{
    if(!result||!lease.Token||!lease.Generation)return false;
    Record snapshot{};
    {
        Guard guard(hooks);bool live=false;
        for(const auto& reader:readers)if(reader.Token==lease.Token&&reader.Generation==lease.Generation){live=true;break;}
        if(!live)return false;
        for(const auto& record:records)if(record.Generation==lease.Generation){snapshot=record;break;}
    }
    if(!snapshot.Generation||pc<snapshot.Base||pc-snapshot.Base>=snapshot.Length)return false;
    WitRuntimeFunction* selected=nullptr;
    if(snapshot.Callback)selected=snapshot.Callback(pc,snapshot.Context);
    else if(hooks.Readable((uint64_t)snapshot.Table,(size_t)snapshot.Count*sizeof(*snapshot.Table))){
        uint32_t low=0,high=snapshot.Count;
        while(low<high){const uint32_t middle=low+(high-low)/2;auto& entry=snapshot.Table[middle];
            if(pc-snapshot.Base<entry.BeginAddress)high=middle;else if(pc-snapshot.Base>=entry.EndAddress)low=middle+1;else{selected=&entry;break;}}
    }
    if(!selected||!hooks.Readable((uint64_t)selected,sizeof(*selected))||!valid(*selected,snapshot.Length)||
       pc-snapshot.Base<selected->BeginAddress||pc-snapshot.Base>=selected->EndAddress||
       !hooks.Executable(snapshot.Base+selected->BeginAddress,selected->EndAddress-selected->BeginAddress)){
        return false;
    }
    *result=selected;return true;
}

bool WitFunctionTables::Release(WitFunctionLease* lease)
{
    if(!lease||!lease->Token||!lease->Generation)return false;
    Guard guard(hooks);
    Reader* reader=nullptr;
    for(auto& candidate:readers)if(candidate.Token==lease->Token&&candidate.Generation==lease->Generation){reader=&candidate;break;}
    if(!reader)return false;
    for(auto& record:records)if(record.Generation==lease->Generation){
        if(!record.Readers)return false;
        --record.Readers;*reader={};*lease={};return true;
    }
    return false;
}
bool WitFunctionTables::HasRange(uint64_t base,uint64_t length)
{
    if(!length||base>UINT64_MAX-length)return true;
    Guard guard(hooks);
    for(const auto& record:records)if(record.Generation&&overlap(base,length,record.Base,record.Length))return true;
    return false;
}
bool WitFunctionTables::BeginRemove(uint64_t key)
{
    Guard guard(hooks);
    for(auto& record:records)if(record.Generation&&record.Key==key&&!record.Retiring){record.Retiring=true;return true;}
    return false;
}
bool WitFunctionTables::FinishRemove(uint64_t key)
{
    Guard guard(hooks);
    for(auto& record:records)if(record.Generation&&record.Key==key&&record.Retiring){
        if(record.Readers)return false;
        record={};return true;
    }
    return false;
}
