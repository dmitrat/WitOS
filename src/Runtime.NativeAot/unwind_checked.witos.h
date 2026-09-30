#ifndef WITOS_UNWIND_CHECKED_H
#define WITOS_UNWIND_CHECKED_H
#include <windows.h>
#include "unwind_validation.witos.h"
enum class WitUnwindFailureReason { Access, Budget, Contract };
struct WitUnwindAccessFailure { WitUnwindFailureReason Reason; };
// Host reference throws this failure; the guest adapter must terminate/report
// failure without recursively unwinding the unwinder. No success substitute.
[[noreturn]] void wit_unwind_access_failure(WitUnwindFailureReason reason=WitUnwindFailureReason::Access);
HRESULT wit_checked_virtual_unwind(const WitUserImageInfo*,const WitUnwindStackRange*,DWORD,DWORD64,PRUNTIME_FUNCTION,CONTEXT*,void**,DWORD64*,KNONVOLATILE_CONTEXT_POINTERS*,PEXCEPTION_ROUTINE*);
// The descriptor and all code/unwind bytes must remain immutable for this
// token's lifetime. Guest use is restricted to its kernel-published image.
// This is a validation cache, not authority over memory or a stack lifetime.
class WitValidatedUnwindImage {
    const WitUserImageInfo* image=nullptr;
public:
    HRESULT Initialize(const WitUserImageInfo* value);
    const WitUserImageInfo* Image() const { return image; }
};
HRESULT wit_checked_virtual_unwind_prevalidated(const WitValidatedUnwindImage&,const WitUnwindStackRange*,DWORD,DWORD64,PRUNTIME_FUNCTION,CONTEXT*,void**,DWORD64*,KNONVOLATILE_CONTEXT_POINTERS*,PEXCEPTION_ROUTINE*);
ULONG64 wit_checked_read64(ULONG64);
M128A wit_checked_read128(ULONG64);
void* wit_checked_unwind_info(ULONG64);
class WitUnwindInstructionBuffer {
    ULONG64 address;
public:
    explicit WitUnwindInstructionBuffer(ULONG64 value):address(value){}
    explicit operator ULONG64() const {return address;}
    unsigned char operator[](int offset) const;
    WitUnwindInstructionBuffer& operator++(){return operator+=(1);}
    WitUnwindInstructionBuffer& operator+=(INT offset);
};
#endif
