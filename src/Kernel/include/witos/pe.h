#ifndef WITOS_PE_H
#define WITOS_PE_H
#include "types.h"

#define WIT_PE_MAX_SECTIONS 16U
#define WIT_PE_MAX_IMAGE_SIZE 262144U
#define WIT_PE_MAX_FILE_SIZE 1048576U
#define WIT_PE_MAX_RELOCATIONS 2048U
#define WIT_PE_MAX_UNWIND_ENTRIES 128U
#define WIT_PE_READ 1U
#define WIT_PE_WRITE 2U
#define WIT_PE_EXECUTE 4U

typedef enum WitPeStatus {
    WitPeOk, WitPeInvalidImage, WitPeUnsupportedImage, WitPeTooLarge,
    WitPeNoMemory, WitPeBusy, WitPeBadBase
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
    WitU32 ImageSize;
    WitU32 HeadersSize;
    WitU32 EntryRva;
    WitU32 SectionCount;
    WitU32 RelocRva;
    WitU32 RelocSize;
    WitU32 UnwindRva;
    WitU32 UnwindSize;
    WitU32 UnwindCount;
    WitPeUnwindRange UnwindInfo[WIT_PE_MAX_UNWIND_ENTRIES];
    WitPeSection Sections[WIT_PE_MAX_SECTIONS];
} WitPeImage;

/* Input is a truthful, immutable kernel-owned byte span. Plan is usable only on Ok.
 * This controlled profile is not a Windows executable compatibility contract. */
WitPeStatus wit_pe_validate(const WitU8 *file, WitU32 size, WitPeImage *plan);
int wit_pe_file_range(const WitPeImage *plan, WitU32 rva, WitU32 size, WitU32 *offset);
#endif
