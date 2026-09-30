#ifndef WITOS_UNWIND_ENVIRONMENT_H
#define WITOS_UNWIND_ENVIRONMENT_H
#include <windows.h>
#include <stdint.h>
#include <stddef.h>
#include "unwind_checked.witos.h"
#pragma warning(push)
#pragma warning(disable:4201) // Pinned upstream metadata ABI uses anonymous unions.
#include "win64unwind.h"
#pragma warning(pop)
#pragma warning(disable:4100) // Optional parameters in the upstream algorithm.
#define ARGUMENT_PRESENT(p) ((p)!=nullptr)
using TADDR=uintptr_t;
using PTR_ULONG64=ULONG64*;
using T_RUNTIME_FUNCTION=RUNTIME_FUNCTION;
using PT_RUNTIME_FUNCTION=RUNTIME_FUNCTION*;
#define DPTR(T) T*
template<class T> T dac_cast(TADDR address) {return reinterpret_cast<T>(address);}
#define _ASSERTE(condition) do {if(!(condition))wit_unwind_access_failure(WitUnwindFailureReason::Contract);} while(0)
#define OOPStackUnwinder WitCheckedStackUnwinder
#define OOPStackUnwinderAMD64 WitCheckedStackUnwinderAMD64
#define RtlVirtualUnwind_Unsafe WitCheckedVirtualUnwind_Unsafe
#endif
