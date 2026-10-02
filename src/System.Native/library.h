#ifndef WITOS_NATIVE_LIBRARY_H
#define WITOS_NATIVE_LIBRARY_H
#include "path.h"
#include "library_lifecycle.h"
WitU64 wit_native_library_load(const char *, WitU32, WitU64 *);
WitU64 wit_native_library_symbol(WitU64, const char *, WitU32, WitU32, WitU64 *);
WitU64 wit_native_library_info(WitU64, WitLibraryInfo *);
WitU64 wit_native_library_unload(WitU64);
WitU64 wit_native_library_find(const char *, WitU32, int, WitU64 *);
WitU64 wit_native_library_path(WitU64, WitLibraryPath *);
WitU64 wit_native_library_acquire_reader(WitU64, WitLibraryInfo *, WitU64 *);
WitU64 wit_native_library_query_reader(WitU64, WitLibraryInfo *);
WitU64 wit_native_library_release_reader(WitU64);
#endif
