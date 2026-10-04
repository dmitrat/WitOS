#include "cxx_runtime.h"
extern "C" {
#include "bootstrap.h"
}

/* The handler of functions with both C++ exception data and a GS cookie (P6.4.g), in the guest: the handler data
 * holds the FuncInfo4 RVA, then the GS data. The guest's GS check (Runtime.NativeAot/security_handler.witos.cpp)
 * validates the cookie before any exception processing, as vcruntime does. */
extern "C" WitU32 wit_native_gs_check(
    void *establisher, DISPATCHER_CONTEXT *dispatcher, WitU64 expectedHandler, WitU64 cookieData);
extern "C" EXCEPTION_DISPOSITION __cdecl __CxxFrameHandler4(
    EXCEPTION_RECORD *record, void *establisher, CONTEXT *context, DISPATCHER_CONTEXT *dispatcher);

extern "C" EXCEPTION_DISPOSITION __cdecl __GSHandlerCheck_EH4(
    EXCEPTION_RECORD *record, void *establisher, CONTEXT *context, DISPATCHER_CONTEXT *dispatcher)
{
    if (!dispatcher) {
        WitCxx::Fatal();
    }
    wit_native_gs_check(establisher, dispatcher, (WitU64)&__GSHandlerCheck_EH4, (WitU64)dispatcher->HandlerData + 4);
    return __CxxFrameHandler4(record, establisher, context, dispatcher);
}
