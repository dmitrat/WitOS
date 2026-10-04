#include "security_handler.witos.h"
#include "seh_validation.witos.h"
#include "native_security.h"

extern "C" EXCEPTION_DISPOSITION __cdecl __GSHandlerCheck_SEH(
    EXCEPTION_RECORD *exception, void *frame, CONTEXT *context, DISPATCHER_CONTEXT *dispatcher)
{
    const auto image = wit_native_process_image();
    if (!image ||
        !exception ||
        !context ||
        !dispatcher ||
        dispatcher->ImageBase != image->Base ||
        dispatcher->EstablisherFrame != (WitU64)frame ||
        (WitU64)dispatcher->LanguageHandler != (WitU64)&__GSHandlerCheck_SEH ||
        (WitU64)dispatcher->FunctionEntry < image->Base ||
        (WitU64)dispatcher->FunctionEntry - image->Base >= image->ImageSize) {
        wit_native_security_failure();
    }
    WitSehGsData data;
    if (wit_seh_gs_validate(image, (WitU32)((WitU64)dispatcher->FunctionEntry - image->Base),
            (WitU64)dispatcher->HandlerData, &data) != WitSehValid) {
        wit_native_security_failure();
    }
    const WitU32 flags = wit_native_gs_check(frame, dispatcher, (WitU64)&__GSHandlerCheck_SEH, data.Address);
    const WitU32 required = (exception->ExceptionFlags & (EXCEPTION_UNWINDING | EXCEPTION_EXIT_UNWIND))
        ? UNW_FLAG_UHANDLER
        : UNW_FLAG_EHANDLER;
    if (!(flags & required)) {
        return ExceptionContinueSearch;
    }
    return wit_native_c_specific_dispatch(exception, frame, context, dispatcher, (WitU64)&__GSHandlerCheck_SEH);
}
