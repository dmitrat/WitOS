#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <string.h>
#include "witos/pe_imports.h"
static unsigned char original[65536], changed[65536];
static unsigned char *guarded;
static WitPeImage image;
static WitPeImports imports;
static unsigned cases;
static WitU32 fullProfile = WIT_PE_LIBRARY | WIT_PE_UNWIND_RUNTIME | WIT_PE_LIBRARY_IMPORTS;

static void put32(unsigned char *p, WitU32 value)
{
    for (unsigned i = 0; i < 4; ++i) {
        p[i] = (unsigned char)(value >> (i * 8));
    }
}

static void put64(unsigned char *p, WitU64 value)
{
    put32(p, (WitU32)value);
    put32(p + 4, (WitU32)(value >> 32));
}

static unsigned raw(unsigned rva)
{
    return rva - 0xF00;
}

static void seed(void)
{
    memset(original, 0, sizeof(original));
    memset(&image, 0, sizeof(image));
    // Import-parser unit contract: a previously validated immutable section map.
    // This synthetic byte array is not claimed to be an executable PE image.
    image.SectionCount = 1;
    image.Sections[0] = (WitPeSection){0x1000, 0xE000, 0x100, 0xE000, 0xE000, WIT_PE_READ};
    put32(original + 0x100, 0x1120);
    put32(original + 0x10C, 0x1100);
    put32(original + 0x110, 0x1140);
    memcpy(original + 0x200, "lib.dll", 8);
    put64(original + 0x220, 0x1160);
    put64(original + 0x228, 0x800000000000000CULL);
    put64(original + 0x240, 0x1160);
    put64(original + 0x248, 0x800000000000000CULL);
    original[0x260] = 17;
    memcpy(original + 0x262, "LibraryAdd", 11);
}

static int check(const unsigned char *data, unsigned bytes, unsigned directoryBytes, WitPeStatus expected)
{
    DWORD old;
    if (!VirtualProtect(guarded, 65536, PAGE_READWRITE, &old)) {
        return 0;
    }
    unsigned char *input = guarded + 65536 - bytes;
    if (bytes) {
        memcpy(input, data, bytes);
    }
    if (!VirtualProtect(guarded, 65536, PAGE_READONLY, &old)) {
        return 0;
    }
    const WitPeStatus status = wit_pe_imports_validate(input, bytes, &image, 0x1000, directoryBytes, &imports);
    ++cases;
    if (status != expected) {
        printf("FAIL import case=%u bytes=%u expected=%u actual=%u\n", cases, bytes, expected, status);
        return 0;
    }
    return 1;
}

static int full_case(const unsigned char *data, unsigned bytes, WitPeStatus expected)
{
    DWORD old;
    if (!VirtualProtect(guarded, 65536, PAGE_READWRITE, &old)) {
        return 0;
    }
    unsigned char *input = guarded + 65536 - bytes;
    if (bytes) {
        memcpy(input, data, bytes);
    }
    if (!VirtualProtect(guarded, 65536, PAGE_READONLY, &old)) {
        return 0;
    }
    const WitPeStatus status = wit_pe_validate_profile(input, bytes, &image, fullProfile);
    ++cases;
    if (status != expected) {
        printf("FAIL full import case=%u expected=%u actual=%u\n", cases, expected, status);
        return 0;
    }
    return 1;
}

static int full_image(const char *path, int cycle)
{
    FILE *stream = 0;
    if (fopen_s(&stream, path, "rb") || !stream) {
        return 20;
    }
    fseek(stream, 0, SEEK_END);
    long length = ftell(stream);
    rewind(stream);
    if (length <= 0 || length > 65536 || fread(original, 1, (size_t)length, stream) != (size_t)length) {
        fclose(stream);
        return 21;
    }
    fclose(stream);
    if (wit_pe_validate_profile(original, (WitU32)length, &image, WIT_PE_LIBRARY | WIT_PE_UNWIND_RUNTIME) !=
        WitPeUnsupportedImage) {
        return 22;
    }
    if (!full_case(original, (unsigned)length, WitPeOk)) {
        return 23;
    }
    if (wit_pe_imports_validate(original, (WitU32)length, &image, image.ImportRva, image.ImportSize, &imports) !=
            WitPeOk ||
        imports.ModuleCount != 1 ||
        imports.SymbolCount != (cycle ? 1U : 3U)) {
        return 24;
    }
    if (!cycle) {
        unsigned ordinals = 0;
        for (unsigned i = 0; i < imports.SymbolCount; ++i) {
            if (!imports.Symbols[i].NameRva && imports.Symbols[i].Ordinal == 12) {
                ++ordinals;
            }
        }
        if (ordinals != 1) {
            return 25;
        }
    }
    unsigned directory = 0, iat = 0;
    wit_pe_file_range(&image, image.ImportRva, image.ImportSize, &directory);
    wit_pe_file_range(&image, imports.Modules[0].IatRva, 8, &iat);
    const unsigned optional = (unsigned)(original[60] |
                                  ((unsigned)original[61] << 8) |
                                  ((unsigned)original[62] << 16) |
                                  ((unsigned)original[63] << 24)) +
        24;
    const unsigned importRva = image.ImportRva;
    for (unsigned mode = 0; mode < 6; ++mode) {
        memcpy(changed, original, (size_t)length);
        WitPeStatus expected = WitPeInvalidImage;
        if (mode == 0) {
            changed[iat] ^= 1;
        }
        if (mode == 1) {
            put32(changed + directory + 4, 1);
            expected = WitPeUnsupportedImage;
        }
        if (mode == 2) {
            changed[directory] ^= 4;
        }
        if (mode == 3) {
            put32(changed + optional + 212, 8);
        }
        if (mode >= 4) {
            unsigned index = mode == 4 ? 11 : 13;
            put32(changed + optional + 112 + index * 8, importRva);
            put32(changed + optional + 116 + index * 8, 20);
            expected = WitPeUnsupportedImage;
        }
        if (!full_case(changed, (unsigned)length, expected)) {
            return 26;
        }
    }
    for (unsigned bytes = 0; bytes < (unsigned)length; ++bytes) {
        if (!full_case(original, bytes, WitPeInvalidImage)) {
            return 27;
        }
    }
    HMODULE library = LoadLibraryExA(path, 0, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!library) {
        return 28;
    }
    FARPROC symbol = GetProcAddress(library, cycle ? "CycleA" : "DependentAdd");
    int result = 0;
    if (symbol) {
        if (cycle) {
            int (*call)(int);
            memcpy(&call, &symbol, sizeof(call));
            result = call(4);
        } else {
            int (*call)(int, int);
            memcpy(&call, &symbol, sizeof(call));
            result = call(731, 11);
        }
    }
    FreeLibrary(library);
    if (result != (cycle ? 5 : 742)) {
        return 29;
    }
    return 0;
}

static int tls_image(const char *path)
{
    FILE *file = 0;
    if (fopen_s(&file, path, "rb") || !file) {
        return 60;
    }
    fseek(file, 0, SEEK_END);
    long bytes = ftell(file);
    rewind(file);
    if (bytes <= 0 || bytes > 65536 || fread(original, 1, (size_t)bytes, file) != (size_t)bytes) {
        fclose(file);
        return 61;
    }
    fclose(file);
    if (wit_pe_validate_profile(original, (WitU32)bytes, &image, fullProfile) != WitPeUnsupportedImage) {
        return 62;
    }
    fullProfile |= WIT_PE_LIBRARY_TLS;
    if (!full_case(original, (unsigned)bytes, WitPeOk)) {
        return 63;
    }
    if (image.TlsSize != 40 || !image.TlsInitialized || image.TlsCallbacksRva) {
        return 64;
    }
    unsigned directory = 0;
    wit_pe_file_range(&image, image.TlsRva, 40, &directory);
    const WitU64 preferred = image.PreferredBase + image.TlsRva;
    for (unsigned mode = 0; mode < 4; ++mode) {
        memcpy(changed, original, (size_t)bytes);
        WitPeStatus expected = WitPeInvalidImage;
        if (mode == 0) {
            put32(changed + directory + 32, WIT_PE_TLS_MAX_BYTES);
            expected = WitPeTooLarge;
        }
        if (mode == 1) {
            changed[directory + 16] ^= 1;
        }
        if (mode == 2) {
            put64(changed + directory + 24, preferred);
            expected = WitPeUnsupportedImage;
        }
        if (mode == 3) {
            put32(changed + directory + 32, 32);
            expected = WitPeOk;
        }
        if (!full_case(changed, (unsigned)bytes, expected)) {
            return 65;
        }
    }
    for (unsigned count = 0; count < (unsigned)bytes; ++count) {
        if (!full_case(original, count, WitPeInvalidImage)) {
            return 66;
        }
    }
    return 0;
}

static int lifecycle_reference(char **paths)
{
    HMODULE provider = LoadLibraryExA(paths[0], 0, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!provider) {
        return 40;
    }
    FARPROC symbol = GetProcAddress(provider, "LibraryData");
    int *trace = 0;
    memcpy(&trace, &symbol, sizeof(trace));
    if (!trace) {
        return 41;
    }
    *trace = 731;
    HMODULE first = LoadLibraryExA(paths[1], 0, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!first || *trace != 7311) {
        return 42;
    }
    HMODULE second = LoadLibraryExA(paths[1], 0, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (second != first || *trace != 7311) {
        return 43;
    }
    if (!FreeLibrary(second) || *trace != 7311 || !FreeLibrary(first) || *trace != 73112) {
        return 44;
    }
    *trace = 731;
    first = LoadLibraryExA(paths[2], 0, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!first || *trace != 73115 || !FreeLibrary(first) || *trace != 7311562) {
        return 45;
    }
    *trace = 731;
    SetLastError(0);
    first = LoadLibraryExA(paths[3], 0, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (first || GetLastError() != ERROR_DLL_INIT_FAILED || *trace != 73134) {
        return 46;
    }
    *trace = 731;
    SetLastError(0);
    first = LoadLibraryExA(paths[4], 0, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (first || GetLastError() != ERROR_DLL_INIT_FAILED || *trace != 7311562) {
        return 47;
    }
    if (!FreeLibrary(provider)) {
        return 48;
    }
    puts("PASS: Windows DLL attach-once, reverse dependency detach and failed child/parent initialization cleanup");
    return 0;
}

static int shutdown_child(char **paths)
{
    HANDLE mapping = OpenFileMappingA(FILE_MAP_ALL_ACCESS, FALSE, paths[2]);
    if (!mapping) {
        return 50;
    }
    void *view = MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, 4096);
    if (!view) {
        return 51;
    }
    HMODULE provider = LoadLibraryExA(paths[0], 0, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!provider) {
        return 52;
    }
    FARPROC dataSymbol = GetProcAddress(provider, "LibraryData"),
            traceSymbol = GetProcAddress(provider, "LibraryTraceTarget");
    int *data = 0;
    unsigned long long **trace = 0;
    memcpy(&data, &dataSymbol, sizeof(data));
    memcpy(&trace, &traceSymbol, sizeof(trace));
    if (!data || !trace) {
        return 53;
    }
    *data = 731;
    *trace = (unsigned long long *)view;
    if (!LoadLibraryExA(paths[1], 0, LOAD_WITH_ALTERED_SEARCH_PATH) || *(unsigned long long *)view != 73115) {
        return 54;
    }
    // The real OS supplies the process-termination notification/non-null reserved.
    ExitProcess(0);
}

int main(int argc, char **argv)
{
    if (argc == 5 && !strcmp(argv[1], "--shutdown-child")) {
        return shutdown_child(argv + 2);
    }
    guarded = VirtualAlloc(0, 65536 + 4096, MEM_RESERVE, PAGE_NOACCESS);
    if (!guarded || !VirtualAlloc(guarded, 65536, MEM_COMMIT, PAGE_READWRITE)) {
        return 1;
    }
    seed();
    if (!check(original, 0xA00, 40, WitPeOk)) {
        return 2;
    }
    if (imports.ModuleCount != 1 ||
        imports.SymbolCount != 2 ||
        imports.Modules[0].NameBytes != 7 ||
        imports.Symbols[0].NameRva != 0x1160 ||
        imports.Symbols[0].NameBytes != 10 ||
        imports.Symbols[1].NameRva ||
        imports.Symbols[1].Ordinal != 12) {
        return 3;
    }
    if (!wit_pe_imports_overlap(&imports, 0x1000, 1) ||
        !wit_pe_imports_overlap(&imports, 0x1157, 1) ||
        !wit_pe_imports_overlap(&imports, 0x116C, 1) ||
        wit_pe_imports_overlap(&imports, 0x1158, 8) ||
        wit_pe_imports_overlap(&imports, 0x116D, 1) ||
        wit_pe_imports_overlap(&imports, 0xFFFFFFFF, 0xFFFFFFFF)) {
        return 4;
    }
    for (unsigned bytes = 0; bytes < 0xA00; ++bytes) {
        if (!check(original, bytes, 40, bytes >= 0x26D ? WitPeOk : WitPeInvalidImage)) {
            return 5;
        }
    }
    for (unsigned mode = 0; mode < 12; ++mode) {
        memcpy(changed, original, sizeof(changed));
        WitPeStatus expected = WitPeInvalidImage;
        if (mode == 0) {
            put32(changed + 0x104, 1);
            expected = WitPeUnsupportedImage;
        }
        if (mode == 1) {
            put32(changed + 0x108, 1);
            expected = WitPeUnsupportedImage;
        }
        if (mode == 2) {
            put32(changed + 0x10C, 0xFFFFFFFF);
        }
        if (mode == 3) {
            changed[0x200] = 0;
        }
        if (mode == 4) {
            changed[0x201] = '/';
        }
        if (mode == 5) {
            put32(changed + 0x114, 1);
        }
        if (mode == 6) {
            put64(changed + 0x240, 0x1162);
        }
        if (mode == 7) {
            put64(changed + 0x228, 0x800000000001000CULL);
            put64(changed + 0x248, 0x800000000001000CULL);
        }
        if (mode == 8) {
            put64(changed + 0x220, 0x100001160ULL);
            put64(changed + 0x240, 0x100001160ULL);
        }
        if (mode == 9) {
            memset(changed + 0x262, 'x', 256);
        }
        if (mode == 10) {
            put32(changed + 0x110, 0x1141);
        }
        if (mode == 11) {
            put32(changed + 0x100, 0x1121);
        }
        if (!check(changed, 0xA00, 40, expected)) {
            return 6;
        }
    }
    image.Sections[0].Flags = WIT_PE_READ | WIT_PE_WRITE;
    if (!check(original, 0xA00, 40, WitPeInvalidImage)) {
        return 7;
    }
    image.Sections[0].Flags = WIT_PE_READ;
    memcpy(changed, original, sizeof(changed));
    put32(changed + 0x100, 0);
    if (!check(changed, 0xA00, 40, WitPeOk) || imports.Modules[0].LookupRva != 0x1140) {
        return 8;
    }
    put64(changed + 0x248, 0x8000000000000000ULL);
    if (!check(changed, 0xA00, 40, WitPeOk) || imports.Symbols[1].Ordinal) {
        return 9;
    }
    memcpy(changed, original, sizeof(changed));
    put32(changed + 0x100, 0x2000);
    put32(changed + 0x110, 0x4000);
    for (unsigned i = 0; i < 512; ++i) {
        put64(changed + raw(0x2000) + i * 8, 0x800000000000000CULL);
        put64(changed + raw(0x4000) + i * 8, 0x800000000000000CULL);
    }
    if (!check(changed, 0x6000, 40, WitPeOk) || imports.SymbolCount != 512) {
        return 10;
    }
    put64(changed + raw(0x2000) + 512 * 8, 0x800000000000000CULL);
    put64(changed + raw(0x4000) + 512 * 8, 0x800000000000000CULL);
    if (!check(changed, 0x6000, 40, WitPeTooLarge)) {
        return 11;
    }
    // Capacity is inclusive: sixteen DLL descriptors with disjoint IATs work.
    memset(changed, 0, sizeof(changed));
    memcpy(changed + raw(0x1300), "lib.dll", 8);
    memcpy(changed + raw(0x1380) + 2, "LibraryAdd", 11);
    put64(changed + raw(0x1320), 0x1380);
    put64(changed + raw(0x1320) + 8, 0x800000000000000CULL);
    for (unsigned i = 0; i < 16; ++i) {
        unsigned char *descriptor = changed + 0x100 + i * 20;
        put32(descriptor, 0x1320);
        put32(descriptor + 12, 0x1300);
        put32(descriptor + 16, 0x1500 + i * 32);
        memcpy(changed + raw(0x1500 + i * 32), changed + raw(0x1320), 24);
    }
    if (!check(changed, 0xA00, 340, WitPeOk) || imports.ModuleCount != 16 || imports.SymbolCount != 32) {
        return 16;
    }
    if (!check(original, 0xA00, 360, WitPeTooLarge)) {
        return 12;
    }
    // Two modules cannot write the same IAT, even if their import strings agree.
    memcpy(changed, original, sizeof(changed));
    memcpy(changed + 0x114, original + 0x100, 20);
    if (!check(changed, 0xA00, 60, WitPeInvalidImage)) {
        return 13;
    }
    if (!check(original, 0xA00, 20, WitPeInvalidImage)) {
        return 14;
    }
    WitPeImports empty;
    memset(&empty, 0xA5, sizeof(empty));
    if (wit_pe_imports_validate(original, sizeof(original), &image, 0, 0, &empty) != WitPeOk ||
        empty.ModuleCount ||
        empty.SymbolCount) {
        return 15;
    }
    const unsigned units = cases;
    if (argc >= 3) {
        int status = full_image(argv[1], 0);
        if (status) {
            return status;
        }
        status = full_image(argv[2], 1);
        if (status) {
            return status;
        }
        printf("PASS: %u full DLL import profile cases plus Windows named/ordinal/data and cyclic dependency calls\n",
            cases - units);
    }
    if (argc >= 8) {
        const int status = lifecycle_reference(argv + 3);
        if (status) {
            return status;
        }
    }
    if (argc == 9) {
        const unsigned before = cases;
        const int status = tls_image(argv[8]);
        if (status) {
            return status;
        }
        printf("PASS: %u full DLL TLS admission/truncation/metadata cases\n", cases - before);
    }
    VirtualFree(guarded, 0, MEM_RELEASE);
    printf("PASS: %u guarded native import descriptor/name/ordinal/IAT/quota cases (parser unit only)\n", units);
    return 0;
}
