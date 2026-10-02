#ifndef WITOS_PACKAGE_H
#define WITOS_PACKAGE_H
#include "types.h"
#define WIT_PACKAGE_MAX_FILES 1024U
#define WIT_PACKAGE_MAX_NAME 1024U
#define WIT_PACKAGE_MAX_BYTES (128ULL * 1024 * 1024)
/* Private immutable boot storage, not a public filesystem ABI. The owner must
 * retain the entire readable byte span unchanged throughout all operations. */
typedef struct WitPackage { const WitU8* Data; WitU64 Size; WitU32 Count; } WitPackage;
typedef struct WitPackageFile { WitU64 Offset; WitU64 Length; const WitU8* Name; WitU32 NameLength; } WitPackageFile;
typedef enum WitPackageStatus { WitPackageOk, WitPackageInvalid, WitPackageMissing, WitPackageNotDirectory, WitPackageEnd } WitPackageStatus;
/* Failure leaves every output field unchanged. Find revalidates the complete
 * immutable descriptor; no cache is shared with mutable or user-owned bytes. */
WitPackageStatus wit_package_open(const WitU8* data,WitU64 size,WitPackage* output);
WitPackageStatus wit_package_get(const WitPackage* package,WitU32 index,WitPackageFile* output);
WitPackageStatus wit_package_find(const WitPackage* package,const WitU8* name,WitU32 length,WitPackageFile* output);
#define WIT_PACKAGE_FILE 1U
#define WIT_PACKAGE_DIRECTORY 2U
typedef struct WitPackageNode {
    const WitU8* Name; /* Borrowed immutable name: stat=full path, list=one child. */
    WitU32 NameLength,Kind;
    WitU64 Length;
} WitPackageNode;
WitPackageStatus wit_package_stat(const WitPackage*,const WitU8*,WitU32,WitPackageNode*);
/* Cursor is an opaque next-entry position, not authority. Root uses length 0.
 * Success updates node and next; missing/end/invalid/not-directory preserve both. */
WitPackageStatus wit_package_list(const WitPackage*,const WitU8*,WitU32,WitU32,WitPackageNode*,WitU32*);
#endif
