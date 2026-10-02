#ifndef WITOS_NATIVE_PATH_H
#define WITOS_NATIVE_PATH_H
#include "file.h"
#define WIT_PATH_INPUT_MAX 4096U
#define WIT_PATH_BUFFER (WIT_STORAGE_NAME_BYTES+2U)
typedef struct WitNativePath { WitU32 Bytes,RequireDirectory; char Text[WIT_PATH_BUFFER]; } WitNativePath;
/* UTF-8, rooted virtual namespace. No drives, symlinks or Windows OS identity.
 * Both separators are accepted at the private compiler/PAL boundary. Outputs
 * are transactional; '..' clamps at the root of this immutable namespace. */
int wit_path_utf8_valid(const char*,WitU32);
WitU64 wit_path_resolve(const char* cwd,WitU32 cwdBytes,const char* input,WitU32 inputBytes,WitNativePath* output);
WitU64 wit_native_path_resolve(const char*,WitU32,WitNativePath*);
WitU64 wit_native_path_full(const char*,WitU32,WitNativePath*);
WitU64 wit_native_cwd_get(WitNativePath*);
WitU64 wit_native_cwd_set(const char*,WitU32);
#endif
