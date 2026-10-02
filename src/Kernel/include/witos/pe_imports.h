#ifndef WITOS_PE_IMPORTS_H
#define WITOS_PE_IMPORTS_H
#include "pe.h"
#define WIT_PE_IMPORT_MODULES 16U
#define WIT_PE_IMPORT_SYMBOLS 512U
#define WIT_PE_IMPORT_NAME 255U
typedef struct WitPeImportModule {
    WitU32 NameRva,NameBytes,LookupRva,IatRva,FirstSymbol,SymbolCount;
} WitPeImportModule;
typedef struct WitPeImportSymbol {
    WitU32 IatRva,NameRva,NameBytes,Ordinal;
} WitPeImportSymbol; /* NameRva==0 selects the ordinal (including zero). */
typedef struct WitPeImports {
    WitU32 DirectoryRva,DirectoryBytes,ModuleCount,SymbolCount;
    WitPeImportModule Modules[WIT_PE_IMPORT_MODULES];
    WitPeImportSymbol Symbols[WIT_PE_IMPORT_SYMBOLS];
} WitPeImports;
/* Caller supplies a validated immutable section plan. Result is usable only on
 * Ok. This parser never resolves, writes an IAT, loads a DLL or executes code.
 * Only unbound AMD64 imports with initialized readonly metadata/IAT are accepted. */
WitPeStatus wit_pe_imports_validate(const WitU8*,WitU32,const WitPeImage*,WitU32,WitU32,WitPeImports*);
/* A validated plan identifies every loader metadata range, including IAT slots,
 * for relocation/write exclusion. This does not validate arbitrary plans. */
int wit_pe_imports_overlap(const WitPeImports*,WitU32,WitU32);
#endif
