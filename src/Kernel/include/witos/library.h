#ifndef WITOS_LIBRARY_H
#define WITOS_LIBRARY_H
#include "types.h"
#define WIT_LIBRARY_VERSION 1U
#define WIT_LIBRARY_CAPACITY 4U
#define WIT_LIBRARY_LOAD 0U
#define WIT_LIBRARY_SYMBOL 1U
#define WIT_LIBRARY_UNLOAD 2U
#define WIT_LIBRARY_QUERY 3U
#define WIT_LIBRARY_FIND 4U
#define WIT_LIBRARY_PATH 5U
#define WIT_LIBRARY_ACQUIRE_READER 6U
#define WIT_LIBRARY_RELEASE_READER 7U
#define WIT_LIBRARY_QUERY_READER 8U
#define WIT_LIBRARY_FINISH_LIFECYCLE 9U
#define WIT_LIBRARY_SHUTDOWN 10U
#define WIT_LIBRARY_THREAD_ENTER 11U
#define WIT_LIBRARY_THREAD_LEAVE 12U
#define WIT_LIBRARY_THREAD_ATTACH 3U
#define WIT_LIBRARY_THREAD_DETACH 4U
#define WIT_LIBRARY_PROCESS_SHUTDOWN 2U
#define WIT_LIBRARY_USER_LIFECYCLE 1U
#define WIT_LIBRARY_READER_CAPACITY 16U
#define WIT_LIBRARY_BY_BASENAME 1U
#define WIT_LIBRARY_PATH_BYTES 1024U
#define WIT_LIBRARY_BY_ORDINAL 1U
typedef struct WitLibraryLifecycleEntry { WitU64 Handle,Base,Entry; } WitLibraryLifecycleEntry;
typedef struct WitLibraryLifecycle {
    WitU32 Version,Size,Attach,Count;
    WitU64 Token,Root;
    WitLibraryLifecycleEntry Entries[WIT_LIBRARY_CAPACITY];
} WitLibraryLifecycle;
WIT_STATIC_ASSERT(sizeof(WitLibraryLifecycle)==128,"Readonly library lifecycle plan");
typedef struct WitLibraryRequest {
    WitU32 Version,Size,Operation,Flags;
    WitU64 Handle,Name,NameBytes,Ordinal,Buffer,BufferBytes;
} WitLibraryRequest;
typedef struct WitLibraryInfo {
    WitU32 Version,Size;
    WitU64 Base,ImageBytes;
    WitU32 EntryRva,UnwindRva,UnwindBytes,References;
} WitLibraryInfo;
typedef struct WitLibraryPath {
    WitU32 Version,Size,NameBytes,Reserved;
    WitU8 Name[WIT_LIBRARY_PATH_BYTES]; /* Counted canonical package key. */
} WitLibraryPath;
WIT_STATIC_ASSERT(sizeof(WitLibraryPath)==1040,"Library path ABI");
/* FIND acquires one reference without loading. Basename ambiguity returns BUSY
 * before changing references; PATH validates the whole output before copying. */
WIT_STATIC_ASSERT(sizeof(WitLibraryRequest)==64,"Library request ABI");
WIT_STATIC_ASSERT(sizeof(WitLibraryInfo)==40,"Library info ABI");
/* Private native-library contract: immutable package bytes only. Caller must
 * quiesce code/readers and unregister unwind tables before final UNLOAD.
 * Dependencies resolve in the importing package directory. References count
 * external owners; kernel graph edges preserve implicit libraries and cycles.
 * Reader handles independently retain the module graph while unwind metadata is
 * inspected. Acquire uses Ordinal as a PC and copies WitLibraryInfo atomically.
 * Query/release use the distinct reader handle; ordinary CLOSE/UNLOAD reject it.
 * Entry points run in user space through readonly lifecycle plans and static
 * TLS uses per-module slots (ABI v45-v48). Forwarders remain unsupported. */
#endif
