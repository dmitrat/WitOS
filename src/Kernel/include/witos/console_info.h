#ifndef WITOS_CONSOLE_INFO_H
#define WITOS_CONSOLE_INFO_H
#include "types.h"
#define WIT_CONSOLE_WRITE_VERSION 1U
#define WIT_CONSOLE_MAX_WRITE 65536U
typedef struct WitConsoleWriteRequest {
    WitU32 Version,Size;
    WitU64 Handle,Buffer,Length,Written;
} WitConsoleWriteRequest;
WIT_STATIC_ASSERT(sizeof(WitConsoleWriteRequest)==40,"Checked console write descriptor ABI");
#endif
