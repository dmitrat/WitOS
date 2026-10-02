#ifndef WITOS_FILE_IO_H
#define WITOS_FILE_IO_H
#include "types.h"
#define WIT_FILE_IO_VERSION 1U
#define WIT_FILE_OPEN 0U
#define WIT_FILE_LENGTH 1U
#define WIT_FILE_READ_AT 2U
#define WIT_FILE_READ 3U
#define WIT_FILE_SEEK 4U
#define WIT_FILE_SEEK_BEGIN 0U
#define WIT_FILE_SEEK_CURRENT 1U
#define WIT_FILE_SEEK_END 2U
#define WIT_FILE_MAX_READ 65536U
/* OPEN: counted UTF-8 relative path at Address/Bytes, returns handle.
 * READ[_AT]: validates the complete requested destination, returns bytes read.
 * SEEK: signed two's-complement Offset, origin in Flags; never negative position.
 * All unassigned fields must be zero. Unsupported mutations never succeed. */
typedef struct WitFileRequest {
    WitU32 Version,Size,Operation,Flags;
    WitU64 Handle,Address,Bytes,Offset,Reserved0,Reserved1;
} WitFileRequest;
WIT_STATIC_ASSERT(sizeof(WitFileRequest)==64,"File IO request ABI");
#endif
