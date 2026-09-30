#ifndef WITOS_UNWIND_REFERENCE_ENVIRONMENT_H
#define WITOS_UNWIND_REFERENCE_ENVIRONMENT_H
#include <windows.h>
#include <stdint.h>
#include <stddef.h>
#include <stdlib.h>
#include <stdio.h>
#pragma warning(push)
#pragma warning(disable:4201) // Unmodified upstream metadata ABI uses anonymous unions.
#include "win64unwind.h"
#pragma warning(pop)
#pragma warning(disable:4100) // Same optional-parameter convention as upstream native builds.
#define ARGUMENT_PRESENT(p) ((p)!=nullptr)
using TADDR=uintptr_t;
using PTR_ULONG64=ULONG64*;
using T_RUNTIME_FUNCTION=RUNTIME_FUNCTION;
using PT_RUNTIME_FUNCTION=RUNTIME_FUNCTION*;
#define DPTR(T) T*
template<class T> T dac_cast(TADDR address) {return reinterpret_cast<T>(address);}
#define _ASSERTE(condition) do {if(!(condition)){fprintf(stderr,"UNWINDER assertion: %s:%d\n",__FILE__,__LINE__);abort();}} while(0)
#endif
