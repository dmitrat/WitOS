#ifndef WITOS_NATIVE_DIRECTORY_H
#define WITOS_NATIVE_DIRECTORY_H
#include "path.h"
#define WIT_DIRECTORY_ALL 0U
#define WIT_DIRECTORY_ONLY 1U
typedef struct WitNativeDirectory { WitNativePath Path; WitU32 Cursor,Done; } WitNativeDirectory;
/* Immutable namespace iterator: cwd is captured at open. Counted UTF-8
 * patterns support literal characters, '*' and scalar '?', case-sensitively.
 * Match returns -1 for invalid input, 0 for no match, 1 for a match. */
int wit_directory_match(const char*,WitU32,const char*,WitU32);
WitU64 wit_native_directory_open(const char*,WitU32,WitNativeDirectory*);
/* EOF: OK/found=0 and unchanged node. Failed calls preserve cursor/node/found. */
WitU64 wit_native_directory_next(WitNativeDirectory*,const char*,WitU32,WitU32,WitStorageInfo*,WitU32*);
#endif
