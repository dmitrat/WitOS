#ifndef WITOS_PAL_ADAPTER_H
#define WITOS_PAL_ADAPTER_H
#include <stdint.h>
/* Unchanged pinned NativeAOT declarations, not locally redefined signatures. */
#include "Pal.h"
extern "C" {
#include "bootstrap.h"
#include "error.h"
}
void wit_pal_set_status(WitU64 status);
UInt32_BOOL wit_pal_result(WitU64 status);
#endif
