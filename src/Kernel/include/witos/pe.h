#ifndef WITOS_PE_H
#define WITOS_PE_H
#include "types.h"
#include "limits.h"

#define WIT_PE_RUNTIME_FULL 4U
#define WIT_PE_UNWIND_RUNTIME 1U
#define WIT_PE_LIBRARY 8U
#define WIT_PE_LIBRARY_IMPORTS 16U
#define WIT_PE_LIBRARY_TLS 32U
#define WIT_PE_READ 1U
#define WIT_PE_WRITE 2U
#define WIT_PE_EXECUTE 4U

typedef enum WitPeStatus {
    WitPeOk,
    WitPeInvalidImage,
    WitPeUnsupportedImage,
    WitPeTooLarge,
    WitPeNoMemory,
    WitPeBusy,
    WitPeBadBase
} WitPeStatus;

typedef struct WitPeSection {
    WitU32 Rva;
    WitU32 VirtualSize;
    WitU32 RawOffset;
    WitU32 RawSize;
    WitU32 MapSize;
    WitU32 Flags;
} WitPeSection;

typedef struct WitPeUnwindRange {
    WitU32 Rva;
    WitU32 Size;
} WitPeUnwindRange;

typedef struct WitPeImage {
    WitU64 PreferredBase;
    WitU32 Profile;
    WitU32 ImageSize;
    WitU32 HeadersSize;
    WitU32 EntryRva;
    WitU32 SectionCount;
    WitU32 ExportRva, ExportSize, ExportBase, ExportCount, ExportNames;
    WitU32 ExportFunctionsRva, ExportNamesRva, ExportOrdinalsRva;
    WitU32 ImportRva, ImportSize, IatRva, IatSize;
    WitU32 RelocRva;
    WitU32 RelocSize;
    WitU32 UnwindRva;
    WitU32 UnwindSize;
    WitU32 UnwindCount;
    WitU32 TlsRva, TlsSize, TlsTemplateRva, TlsInitialized, TlsZeroFill, TlsIndexRva, TlsCallbacksRva;
    WitU32 TlsCallbackCount; /* entries before the null terminator; nonzero only in the library TLS profile */
    WitPeUnwindRange UnwindInfo[WIT_PE_FULL_UNWIND_ENTRIES];
    WitPeSection Sections[WIT_PE_MAX_SECTIONS];
} WitPeImage;

/* Input is a truthful, immutable kernel-owned byte span. Plan is usable only on Ok.
 * This controlled profile is not a Windows executable compatibility contract. */
WitPeStatus wit_pe_validate(const WitU8 *file, WitU32 size, WitPeImage *plan);
WitPeStatus wit_pe_validate_profile(const WitU8 *file, WitU32 size, WitPeImage *plan, WitU32 profile);
int wit_pe_file_range(const WitPeImage *plan, WitU32 rva, WitU32 size, WitU32 *offset);
WitPeStatus wit_pe_exports_validate(const WitU8 *, WitPeImage *);
/* Validated immutable input only; lookup returns RVA or zero and never runs a
 * forwarder/initializer. Name lookup is case-sensitive, ordinal lookup uses Base. */
WitPeStatus wit_pe_export_find(const WitU8 *, const WitPeImage *, const char *, WitU32, WitU32, WitU32 *);
#endif
