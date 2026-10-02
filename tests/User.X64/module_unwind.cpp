#include <intrin.h>
#include "function_tables_guest.witos.h"
#include "unwind_checked.witos.h"
#include "unwind_scope.witos.h"
extern "C" {
#include "library.h"
#include "protocol.h"
}
extern "C" WitU64 wit_foreign_module_unwind_probe(WitU64, WitU64, WitU64);
static bool checked;
static DWORD64 imageBase;

static __declspec(noinline) int inspect(void *callerSlot, void *returned)
{
    auto ownSlot = (void **)_AddressOfReturnAddress();
    const DWORD64 pc = (DWORD64)*ownSlot, sp = (DWORD64)(ownSlot + 1);
    DWORD64 base = 0;
    auto entry = RtlLookupFunctionEntry(pc, &base, nullptr);
    if (!entry || base != imageBase) {
        return 1;
    }
    WitNativeUnwindScope scope(WIT_THREAD_REFERENCE_CURRENT);
    if (scope.Status() != WIT_STATUS_OK) {
        return 2;
    }
    // This fixed C frame is RSP-based; use its real call-site PC/SP, as in the
    // existing dynamic-frame probe. No complete register-capture API is claimed.
    CONTEXT context = {};
    context.ContextFlags = CONTEXT_FULL;
    context.Rip = pc;
    context.Rsp = sp;
    void *data = (void *)0x1234;
    DWORD64 frame = 0;
    auto handler = RtlVirtualUnwind(0, base, pc, entry, &context, &data, &frame, nullptr);
    checked = !handler &&
        data == (void *)0x1234 &&
        context.Rip == (DWORD64)returned &&
        context.Rsp == (DWORD64)callerSlot + 8;
    return checked ? 37 : 3;
}

extern "C" WitU64 wit_module_unwind_probe(unsigned mode)
{
    const char path[] = "/native/dependent.dll";
    WitU64 root = 0, pc = 0;
    if (wit_native_library_load(path, sizeof(path) - 1, &root) != WIT_STATUS_OK) {
        return 3101;
    }
    if (wit_native_library_symbol(root, "DependentInspect", 16, 0, &pc) != WIT_STATUS_OK) {
        return 3102;
    }
    WitLibraryInfo info;
    if (wit_native_library_info(root, &info) != WIT_STATUS_OK) {
        return 3103;
    }
    imageBase = info.Base;
    if (mode == 20) {
        WitU64 readers[WIT_LIBRARY_READER_CAPACITY];
        unsigned count = 0;
        WitU64 status = WIT_STATUS_OK;
        while (count < WIT_LIBRARY_READER_CAPACITY) {
            WitU64 reader = 0;
            status = wit_native_library_acquire_reader(pc, &info, &reader);
            if (status != WIT_STATUS_OK) {
                break;
            }
            readers[count++] = reader;
        }
        (void)readers;
        if (status != WIT_STATUS_NO_MEMORY || count < 2) {
            return 3104;
        }
        ((volatile WitU64 *)WIT_GC_INFO_REPORT)[6] = count;
        ((volatile WitU64 *)WIT_GC_INFO_REPORT)[7] = 20;
        WitFunctionLease impossible = {};
        (void)wit_coreclr_acquire_function(pc, &impossible);
        return 3105; // Must fail fast, never pretend this is a leaf.
    }
    checked = false;
    if (((int (*)(int (*)(void *, void *), int))pc)(inspect, 5) != 42 || !checked) {
        return 3106;
    }
    return wit_foreign_module_unwind_probe(root, pc, info.Base);
}
