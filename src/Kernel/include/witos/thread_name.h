#ifndef WITOS_THREAD_NAME_H
#define WITOS_THREAD_NAME_H
#include "types.h"
#define WIT_THREAD_NAME_CAPACITY 128U
#define WIT_THREAD_NAME_VERSION 1U

/* Current-thread diagnostic label, not authority or a public naming service. */
typedef struct WitThreadNameInfo {
    WitU32 Version, Size;
    WitU64 ThreadId;
    WitU32 Length, Reserved;
    WitU16 Name[WIT_THREAD_NAME_CAPACITY];
} WitThreadNameInfo;

WIT_STATIC_ASSERT(sizeof(WitThreadNameInfo) == 280, "Thread name snapshot ABI");
#endif
