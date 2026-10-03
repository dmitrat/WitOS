#ifndef WITOS_NATIVE_FILE_VIEW_H
#define WITOS_NATIVE_FILE_VIEW_H
#include "file.h"
#include "native_limits.h"
#define WIT_FILE_VIEW_READONLY 0U
#define WIT_FILE_VIEW_PRIVATE 1U

/* Private native adapter, not a public memory-mapping ABI. Whole immutable
 * files are materialized in owned reservations. PRIVATE writes never reach
 * storage. Views outlive the open file and carry non-repeating identities. */
typedef struct WitNativeFileView {
    WitU64 Token;
    void *Address;
    WitU64 Length;
} WitNativeFileView;

WitU64 wit_native_file_view(WitU64 handle, WitU32 mode, WitNativeFileView *output);
WitU64 wit_native_map_file(const char *path, WitU32 bytes, WitU32 mode, WitNativeFileView *output);
WitU64 wit_native_unmap_file(void *address, WitU64 length);
WitU64 wit_native_file_unview(const WitNativeFileView *view);
#endif
