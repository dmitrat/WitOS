#ifndef WITOS_SECURITY_HANDLER_H
#define WITOS_SECURITY_HANDLER_H
#include "pal.witos.h"
extern "C" WitU32 wit_native_gs_check(void*,DISPATCHER_CONTEXT*,WitU64,WitU64);
extern "C" EXCEPTION_DISPOSITION wit_native_c_specific_dispatch(EXCEPTION_RECORD*,void*,CONTEXT*,DISPATCHER_CONTEXT*,WitU64);
#endif
