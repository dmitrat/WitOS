#ifndef WITOS_STORAGE_QUERY_H
#define WITOS_STORAGE_QUERY_H
#include "types.h"
#define WIT_STORAGE_QUERY_VERSION 1U
#define WIT_STORAGE_STAT 0U
#define WIT_STORAGE_LIST 1U
#define WIT_STORAGE_FILE 1U
#define WIT_STORAGE_DIRECTORY 2U
#define WIT_STORAGE_NAME_BYTES 1024U

typedef struct WitStorageQuery {
    WitU32 Version, Size, Operation, Reserved;
    WitU64 Path, PathBytes, Cursor, Buffer, BufferBytes, Reserved2;
} WitStorageQuery;

typedef struct WitStorageInfo {
    WitU32 Version, Size, Kind, NameBytes;
    WitU64 Length, NextCursor;
    WitU8 Name[WIT_STORAGE_NAME_BYTES]; /* Counted UTF-8; no required terminator. */
} WitStorageInfo;

WIT_STATIC_ASSERT(sizeof(WitStorageQuery) == 64, "Storage query ABI");
WIT_STATIC_ASSERT(sizeof(WitStorageInfo) == 1056, "Storage info ABI");
/* Root is an empty path. LIST's cursor has no authority; the immutable package
 * defines ordering. EOF returns OK/result=0 without modifying the destination.
 * Successful STAT/LIST return sizeof(WitStorageInfo); failures preserve output. */
#endif
