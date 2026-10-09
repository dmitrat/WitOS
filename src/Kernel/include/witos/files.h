#ifndef WITOS_FILES_H
#define WITOS_FILES_H
#include "handles.h"
#include "package.h"

typedef struct WitFile {
    WitU64 Token, Offset, Length, Position;
} WitFile;

typedef struct WitFileTable {
    WitFile Entries[WIT_PROCESS_HANDLE_CAPACITY];
} WitFileTable;

void wit_files_initialize(WitFileTable *files);
WitU64 wit_file_open(WitFileTable *, WitHandleTable *, const WitPackage *, const WitU8 *, WitU32, WitU64 *);
WitU64 wit_file_get(WitFileTable *, WitHandleTable *, WitU64, WitFile **);
WitU64 wit_file_close(WitFileTable *, WitHandleTable *, WitU64);
WitU64 wit_file_seek(WitFile *, WitU64, WitU32, WitU64 *);
#endif
