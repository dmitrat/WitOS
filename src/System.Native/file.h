#ifndef WITOS_NATIVE_FILE_H
#define WITOS_NATIVE_FILE_H
#include "bootstrap.h"
WitU64 wit_native_file_open(const char*,WitU32,WitU64*);
WitU64 wit_native_file_length(WitU64,WitU64*);
WitU64 wit_native_file_read(WitU64,void*,WitU32,WitU64*);
WitU64 wit_native_file_read_at(WitU64,void*,WitU32,WitU64,WitU64*);
WitU64 wit_native_file_seek(WitU64,WitU64,WitU32,WitU64*);
WitU64 wit_native_file_close(WitU64);
WitU64 wit_native_storage_stat(const char*,WitU32,WitStorageInfo*);
WitU64 wit_native_storage_list(const char*,WitU32,WitU32,WitStorageInfo*,WitU64*);
#endif
