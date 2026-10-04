#include <stddef.h>
#include <stdint.h>
#include <stdlib.h>
#include "witos/pe.h"
static WitPeImage plan;

int LLVMFuzzerTestOneInput(const uint8_t *data, size_t size)
{
    if (size > WIT_PE_MAX_FILE_SIZE) {
        return 0;
    }
    const WitU32 profiles[] = {WIT_PE_UNWIND_RUNTIME | WIT_PE_RUNTIME_FULL, WIT_PE_UNWIND_RUNTIME | WIT_PE_LIBRARY,
        WIT_PE_UNWIND_RUNTIME | WIT_PE_LIBRARY | WIT_PE_LIBRARY_IMPORTS,
        WIT_PE_UNWIND_RUNTIME | WIT_PE_LIBRARY | WIT_PE_LIBRARY_IMPORTS | WIT_PE_LIBRARY_TLS};
    for (unsigned i = 0; i < sizeof(profiles) / sizeof(profiles[0]); ++i) {
        const WitPeStatus status = wit_pe_validate_profile(data, (WitU32)size, &plan, profiles[i]);
        if (status < WitPeOk || status > WitPeBadBase) {
            abort();
        }
        if (status == WitPeOk && (profiles[i] & WIT_PE_LIBRARY)) {
            WitU32 rva = 0;
            if (wit_pe_export_find(data, &plan, "LibraryAdd", 10, 0, &rva) != WitPeOk ||
                wit_pe_export_find(data, &plan, 0, 0, plan.ExportBase, &rva) != WitPeOk) {
                abort();
            }
        }
    }
    return 0;
}
