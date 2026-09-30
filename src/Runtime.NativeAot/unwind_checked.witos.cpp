#include "unwind_environment.witos.h"
#include "unwinder.h"
#include <string.h>
namespace {
struct Scope {
    const WitUserImageInfo* Image;
    WitUnwindStackRange Stack;
    Scope* Previous;
    unsigned Budget;
};
static __declspec(thread) Scope* active;
struct Guard {
    Scope Value;
    Guard(const WitUserImageInfo* image,const WitUnwindStackRange* stack):Value{image,*stack,active,65536}{active=&Value;}
    ~Guard(){active=Value.Previous;}
};
Scope& scope(){
    if(!active)wit_unwind_access_failure(WitUnwindFailureReason::Contract);
    if(!active->Budget)wit_unwind_access_failure(WitUnwindFailureReason::Budget);
    --active->Budget;return *active;
}
PRUNTIME_FUNCTION lookup(const WitUserImageInfo* image,ULONG64 pc)
{
    if(pc<image->Base||pc-image->Base>=image->ImageSize)return nullptr;
    const auto rva=(WitU32)(pc-image->Base);
    auto entries=(PRUNTIME_FUNCTION)(image->Base+image->UnwindRva);
    WitU32 low=0,high=image->UnwindSize/12;
    while(low<high){const WitU32 middle=low+(high-low)/2;const auto& entry=entries[middle];
        if(rva<entry.BeginAddress)high=middle;else if(rva>=entry.EndAddress)low=middle+1;else return &entries[middle];}
    return nullptr;
}
}
ULONG64 wit_checked_read64(ULONG64 address)
{
    auto& s=scope();ULONG64 result;
    if(!wit_unwind_read_stack(&s.Stack,address,&result,sizeof(result)))wit_unwind_access_failure();
    return result;
}
M128A wit_checked_read128(ULONG64 address)
{
    auto& s=scope();M128A result;
    if(!wit_unwind_read_stack(&s.Stack,address,&result,sizeof(result)))wit_unwind_access_failure();
    return result;
}
void* wit_checked_unwind_info(ULONG64 address)
{
    auto& s=scope();auto result=wit_unwind_info_address(s.Image,address);
    if(!result)wit_unwind_access_failure();return (void*)result;
}
unsigned char WitUnwindInstructionBuffer::operator[](int offset) const
{
    auto& s=scope();
    if(offset<0||address>~0ULL-(unsigned)offset)wit_unwind_access_failure();
    unsigned char result;
    if(!wit_unwind_read_code(s.Image,address+(unsigned)offset,&result,1))wit_unwind_access_failure();
    return result;
}
WitUnwindInstructionBuffer& WitUnwindInstructionBuffer::operator+=(INT offset)
{
    if(offset<0||address>~0ULL-(unsigned)offset)wit_unwind_access_failure();
    address+=(unsigned)offset;return *this;
}
HRESULT OOPStackUnwinder::GetModuleBase(DWORD64 pc,PDWORD64 base)
{
    auto& s=scope();unsigned char ignored;
    if(!wit_unwind_read_code(s.Image,pc,&ignored,1))return E_FAIL;
    *base=s.Image->Base;return S_OK;
}
HRESULT OOPStackUnwinder::GetFunctionEntry(DWORD64 pc,PVOID buffer,DWORD bytes)
{
    auto& s=scope();auto entry=lookup(s.Image,pc);
    if(!entry||bytes!=sizeof(*entry))return E_FAIL;memcpy(buffer,entry,bytes);return S_OK;
}
static HRESULT unwind(const WitUserImageInfo* image,bool prevalidated,const WitUnwindStackRange* stack,DWORD handlerType,DWORD64 pc,PRUNTIME_FUNCTION entry,
    CONTEXT* context,void** handlerData,DWORD64* frame,KNONVOLATILE_CONTEXT_POINTERS* pointers,PEXCEPTION_ROUTINE* handler)
{
    if(!stack||!context||!handlerData||!frame||!handler||stack->Low>=stack->High||context->Rsp<stack->Low||context->Rsp>=stack->High||
        (handlerType&~3U)||!image||(!prevalidated&&wit_unwind_validate_image(image)!=WitUnwindValid))return E_INVALIDARG;
    const auto canonical=lookup(image,pc);
    if(!canonical||entry!=canonical)return E_INVALIDARG; // Never dereference a caller-supplied fake entry.
    WitUnwindRecord record;
    if(prevalidated&&wit_unwind_validate_function(image,(WitU32)((uintptr_t)canonical-image->Base),&record)!=WitUnwindValid)return E_INVALIDARG;
    Guard guard(image,stack);
    // Real CoffNativeCodeManager callers leave HandlerData uninitialized and
    // initialize only the context fields required by their particular walk.
    // Copy object bytes; do not evaluate output-only pointer values.
    CONTEXT result;memcpy(&result,context,sizeof(result));
    void* data=nullptr;DWORD64 establisher=0;PEXCEPTION_ROUTINE routine=nullptr;
    KNONVOLATILE_CONTEXT_POINTERS savedPointers={};if(pointers)memcpy(&savedPointers,pointers,sizeof(savedPointers));
    const HRESULT hr=OOPStackUnwinderAMD64::VirtualUnwind(handlerType,image->Base,pc,canonical,&result,&data,&establisher,pointers?&savedPointers:nullptr,&routine);
    if(FAILED(hr))return hr;
    if(result.Rsp<stack->Low||result.Rsp>stack->High||(routine&&(establisher<stack->Low||establisher>=stack->High)))return E_FAIL;
    memcpy(context,&result,sizeof(result));
    // Upstream writes HandlerData only when a handler is found. Preserve the
    // caller's bytes otherwise, without reading an uninitialized output slot.
    if(routine)*handlerData=data;
    *frame=establisher;*handler=routine;
    if(pointers)memcpy(pointers,&savedPointers,sizeof(savedPointers));
    return S_OK;
}

HRESULT WitValidatedUnwindImage::Initialize(const WitUserImageInfo* value)
{
    if(image)return image==value?S_OK:E_INVALIDARG;
    if(wit_unwind_validate_image(value)!=WitUnwindValid)return E_INVALIDARG;
    image=value;return S_OK;
}
HRESULT wit_checked_virtual_unwind(const WitUserImageInfo* image,const WitUnwindStackRange* stack,DWORD type,DWORD64 pc,PRUNTIME_FUNCTION entry,
    CONTEXT* context,void** data,DWORD64* frame,KNONVOLATILE_CONTEXT_POINTERS* pointers,PEXCEPTION_ROUTINE* handler)
{ return unwind(image,false,stack,type,pc,entry,context,data,frame,pointers,handler); }
HRESULT wit_checked_virtual_unwind_prevalidated(const WitValidatedUnwindImage& image,const WitUnwindStackRange* stack,DWORD type,DWORD64 pc,PRUNTIME_FUNCTION entry,
    CONTEXT* context,void** data,DWORD64* frame,KNONVOLATILE_CONTEXT_POINTERS* pointers,PEXCEPTION_ROUTINE* handler)
{ return unwind(image.Image(),true,stack,type,pc,entry,context,data,frame,pointers,handler); }
