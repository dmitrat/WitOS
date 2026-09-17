#ifndef WITOS_GCENV_H
#define WITOS_GCENV_H
/* The unmodified, hash-pinned upstream interface. Windows SDK types are used
 * only to compile declarations; no Windows implementation is linked. */
#include <stdint.h>
#include <stddef.h>
#include <assert.h>
#include <windows.h>
#include "gcenv.structs.h"
#include "gcenv.base.h"
#include "gcenv.os.h"
#include "gcenv.windows.inl"
extern "C" {
#include "bootstrap.h"
}
WitU64 wit_gc_deadline_at(uint32_t milliseconds, WitU64 now, WitU64 frequency);
WitU64 wit_gc_deadline(uint32_t milliseconds);
#endif
