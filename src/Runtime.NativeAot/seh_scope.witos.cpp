#include "seh_scope.witos.h"

namespace {
bool frame_valid(WitU64 frame, const WitUnwindStackRange *stack)
{
    return stack && stack->Low < stack->High && frame >= stack->Low && frame < stack->High;
}

bool contains(const WitSehScope &scope, WitU64 rva)
{
    return rva >= scope.Begin && rva < scope.End;
}
}

WitSehAction wit_seh_search(const WitUserImageInfo *image, WitU32 function, WitU64 data, WitU64 pc, WitU64 frame,
    const WitUnwindStackRange *stack, EXCEPTION_RECORD *exception, CONTEXT *context, WitSehDecision *output,
    const WitSehCallbacks *callbacks)
{
    if (callbacks && (!callbacks->Before || !callbacks->After || !callbacks->Invoke)) {
        return WitSehInvalid;
    }
    WitSehTable table;
    if (!exception ||
        !context ||
        !output ||
        !frame_valid(frame, stack) ||
        wit_seh_validate(image, function, data, &table) != WitSehValid ||
        pc < image->Base ||
        pc - image->Base >= image->ImageSize) {
        return WitSehInvalid;
    }
    const WitU64 rva = pc - image->Base;
    EXCEPTION_POINTERS pointers = {exception, context};
    for (WitU32 i = 0; i < table.Count; ++i) {
        WitSehScope scope;
        if (!wit_seh_scope(&table, i, &scope)) {
            return WitSehInvalid;
        }
        if (!scope.Target || !contains(scope, rva)) {
            continue;
        }
        LONG result = EXCEPTION_EXECUTE_HANDLER;
        if (scope.Handler != 1) {
            using Filter = LONG(__cdecl *)(EXCEPTION_POINTERS *, ULONG64);
            if (callbacks) {
                callbacks->Before(callbacks->Context);
            }
            result = callbacks
                ? (LONG)callbacks->Invoke(callbacks->Context, image->Base + scope.Handler, (WitU64)&pointers, frame)
                : ((Filter)(image->Base + scope.Handler))(&pointers, frame);
            if (callbacks) {
                callbacks->After(callbacks->Context);
            }
            if (pointers.ExceptionRecord != exception || pointers.ContextRecord != context) {
                return WitSehInvalid;
            }
        }
        if (result < 0) {
            return WitSehContinue;
        }
        if (result > 0) {
            *output = {i, scope.Target};
            return WitSehTarget;
        }
    }
    return WitSehSearch;
}

bool wit_seh_terminate(const WitUserImageInfo *image, WitU32 function, WitU64 data, WitU64 pc, WitU64 frame,
    const WitUnwindStackRange *stack, bool targetFrame, WitU64 target, DWORD *cursor, const WitSehCallbacks *callbacks)
{
    if (callbacks && (!callbacks->Before || !callbacks->After || !callbacks->Invoke)) {
        return false;
    }
    WitSehTable table;
    if (!cursor ||
        !frame_valid(frame, stack) ||
        wit_seh_validate(image, function, data, &table) != WitSehValid ||
        *cursor > table.Count ||
        pc < image->Base ||
        pc - image->Base >= image->ImageSize ||
        (targetFrame && (target < image->Base || target - image->Base >= image->ImageSize))) {
        return false;
    }
    const WitU64 rva = pc - image->Base, targetRva = target - image->Base;
    for (WitU32 i = *cursor; i < table.Count; ++i) {
        WitSehScope scope;
        if (!wit_seh_scope(&table, i, &scope)) {
            return false;
        }
        if (!contains(scope, rva)) {
            continue;
        }
        if (targetFrame && (contains(scope, targetRva) || (scope.Target && scope.Target == targetRva))) {
            break;
        }
        if (scope.Target) {
            continue;
        }
        *cursor = i + 1;
        using Finally = void(__cdecl *)(BOOLEAN, ULONG64);
        if (callbacks) {
            callbacks->Before(callbacks->Context);
        }
        if (callbacks) {
            callbacks->Invoke(callbacks->Context, image->Base + scope.Handler, TRUE, frame);
        } else {
            ((Finally)(image->Base + scope.Handler))(TRUE, frame);
        }
        if (callbacks) {
            callbacks->After(callbacks->Context);
        }
    }
    return true;
}
