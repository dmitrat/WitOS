#ifndef WITOS_IMAGE_INFO_H
#define WITOS_IMAGE_INFO_H
#include "types.h"

#define WIT_IMAGE_INFO_VERSION 2U
#define WIT_IMAGE_INFO_SIZE 568U
#define WIT_IMAGE_RESOURCE_CAPACITY 128U
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
    WitU32 ResourceNameLength;
    WitU32 ResourceReserved;
    WitU16 ResourceName[WIT_IMAGE_RESOURCE_CAPACITY];
} WitUserImageInfo;

/* Private embedded boot-resource namespace, not a filesystem or public loader.
 * Names are ASCII labels transported as UTF-16 for the upstream PAL contract. */
static inline int wit_image_resource_valid(const WitU16 *name, WitU32 length)
{
    if (length >= WIT_IMAGE_RESOURCE_CAPACITY || name[length]) {
        return 0;
    }
    if (!length) {
        return 1; /* Explicitly anonymous image. */
    }
    if (length <= 6 ||
        name[0] != 'b' ||
        name[1] != 'o' ||
        name[2] != 'o' ||
        name[3] != 't' ||
        name[4] != ':' ||
        name[5] != '/') {
        return 0;
    }
    for (WitU32 i = 6; i < length; ++i) {
        const WitU16 c = name[i];
        if (!((c >= 'a' && c <= 'z') ||
                (c >= 'A' && c <= 'Z') ||
                (c >= '0' && c <= '9') ||
                c == '.' ||
                c == '_' ||
                c == '-')) {
            return 0;
        }
    }
    return 1;
}

WIT_STATIC_ASSERT(sizeof(WitUserImageRange) == 16, "Image range ABI");
WIT_STATIC_ASSERT(sizeof(WitUserImageInfo) == WIT_IMAGE_INFO_SIZE, "Image information ABI");
#endif
