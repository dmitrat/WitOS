#ifndef WITOS_IMAGE_INFO_H
#define WITOS_IMAGE_INFO_H
#include "types.h"

#define WIT_IMAGE_INFO_VERSION 1U
#define WIT_IMAGE_INFO_SIZE 304U
#define WIT_IMAGE_INFO_MAX_RANGES 16U
#define WIT_IMAGE_INFO_READ 1U
#define WIT_IMAGE_INFO_WRITE 2U
#define WIT_IMAGE_INFO_EXECUTE 4U

typedef struct WitUserImageRange {
    WitU32 Rva;
    WitU32 Size;
    WitU32 InitializedSize;
    WitU32 Flags;
} WitUserImageRange;

typedef struct WitUserImageInfo {
    WitU32 Version;
    WitU32 Size;
    WitU64 Base;
    WitU64 Entry;
    WitU32 ImageSize;
    WitU32 RangeCount;
    WitU32 HeadersSize;
    WitU32 UnwindRva;
    WitU32 UnwindSize;
    WitU32 Reserved;
    WitUserImageRange Ranges[WIT_IMAGE_INFO_MAX_RANGES];
} WitUserImageInfo;

_Static_assert(sizeof(WitUserImageRange) == 16, "Image range ABI");
_Static_assert(sizeof(WitUserImageInfo) == WIT_IMAGE_INFO_SIZE, "Image information ABI");
#endif
