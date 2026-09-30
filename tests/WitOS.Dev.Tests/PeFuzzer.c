#include <stddef.h>
#include <stdint.h>
#include <stdlib.h>
#include "witos/pe.h"
static WitPeImage plan;
int LLVMFuzzerTestOneInput(const uint8_t* data,size_t size)
{
    if(size>WIT_PE_MAX_FILE_SIZE)return 0;
    const WitPeStatus status=wit_pe_validate_profile(data,(WitU32)size,&plan,WIT_PE_UNWIND_RUNTIME|WIT_PE_RUNTIME_FULL);
    if(status<WitPeOk||status>WitPeBadBase)abort();
    return 0;
}
