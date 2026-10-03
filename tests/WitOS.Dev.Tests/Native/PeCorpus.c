#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "witos/pe.h"
static WitPeImage plan;
static unsigned cases, accepted, rejected;
/* FNV-1a over every verdict in order: equal digests show that a parser change kept each verdict. */
static unsigned verdicts = 2166136261u;

static WitPeStatus verify(const unsigned char *bytes, unsigned size)
{
    unsigned span = (size + 4095u) & ~4095u;
    DWORD prior;
    unsigned char *region = VirtualAlloc(NULL, (SIZE_T)span + 8192, MEM_RESERVE, PAGE_NOACCESS);
    if (!region) {
        exit(2);
    }
    unsigned char *end = region + 4096 + span;
    if (span && !VirtualAlloc(region + 4096, span, MEM_COMMIT, PAGE_READWRITE)) {
        exit(3);
    }
    if (size) {
        memcpy(end - size, bytes, size);
    }
    if (span && !VirtualProtect(region + 4096, span, PAGE_READONLY, &prior)) {
        exit(4);
    }
    WitPeStatus result = wit_pe_validate_profile(end - size, size, &plan, WIT_PE_UNWIND_RUNTIME | WIT_PE_RUNTIME_FULL);
    if (result < 0 || result > WitPeBadBase) {
        exit(5);
    }
    ++cases;
    verdicts = (verdicts ^ (unsigned)result) * 16777619u;
    if (result == WitPeOk) {
        ++accepted;
    } else {
        ++rejected;
    }
    if (!VirtualFree(region, 0, MEM_RELEASE)) {
        exit(6);
    }
    return result;
}

static WitPeImage baseline;
static unsigned structural;

static unsigned read32(const unsigned char *p)
{
    unsigned value;
    memcpy(&value, p, 4);
    return value;
}

static void mutation(unsigned char *data, unsigned size, unsigned offset, unsigned width, unsigned long long value,
    WitPeStatus expected, const char *name)
{
    unsigned char saved[8];
    if (width > sizeof(saved) || offset > size || width > size - offset) {
        exit(17);
    }
    memcpy(saved, data + offset, width);
    memcpy(data + offset, &value, width);
    const WitPeStatus actual = verify(data, size);
    memcpy(data + offset, saved, width);
    if (actual != expected) {
        printf("FAIL: structural %s expected=%u actual=%u offset=%u\n", name, (unsigned)expected, (unsigned)actual,
            offset);
        exit(18);
    }
    ++structural;
}

static void deep_cases(unsigned char *data, unsigned size, unsigned optional)
{
    unsigned pdata, tls, reloc, xdata;
    baseline = plan;
    if (!wit_pe_file_range(&baseline, baseline.UnwindRva, 12, &pdata) ||
        !wit_pe_file_range(&baseline, baseline.TlsRva, 40, &tls) ||
        !wit_pe_file_range(&baseline, baseline.RelocRva, 10, &reloc) ||
        !wit_pe_file_range(&baseline, read32(data + pdata + 8), 4, &xdata)) {
        exit(19);
    }
#define MUT(name, offset, width, value, expected) mutation(data, size, offset, width, value, expected, name)
    MUT("pdata-size", optional + 116 + 3 * 8, 4, baseline.UnwindSize - 1, WitPeInvalidImage);
    MUT("pdata-alignment", optional + 112 + 3 * 8, 4, baseline.UnwindRva + 1, WitPeInvalidImage);
    MUT("pdata-empty-function", pdata, 4, read32(data + pdata + 4), WitPeInvalidImage);
    MUT("pdata-code-range", pdata + 4, 4, baseline.ImageSize, WitPeInvalidImage);
    MUT("pdata-xdata-alignment", pdata + 8, 4, read32(data + pdata + 8) | 1, WitPeInvalidImage);
    MUT("xdata-version", xdata, 1, (data[xdata] & ~7U) | 7U, WitPeUnsupportedImage);
    MUT("xdata-flags", xdata, 1, (data[xdata] & 7U) | 0xF8U, WitPeInvalidImage);
    MUT("xdata-frame-register", xdata + 3, 1, 2, WitPeInvalidImage);
    MUT("tls-directory-size", optional + 116 + 9 * 8, 4, 39, WitPeInvalidImage);
    MUT("tls-directory-alignment", optional + 112 + 9 * 8, 4, baseline.TlsRva + 1, WitPeInvalidImage);
    MUT("tls-template-before-image", tls, 8, baseline.PreferredBase - 1, WitPeInvalidImage);
    MUT("tls-template-reversed", tls + 8, 8, baseline.PreferredBase + baseline.TlsTemplateRva - 1, WitPeInvalidImage);
    MUT("tls-index-alignment", tls + 16, 8, baseline.PreferredBase + baseline.TlsIndexRva + 1, WitPeInvalidImage);
    MUT("tls-zero-fill-quota", tls + 32, 4, WIT_PE_TLS_MAX_BYTES + 1, WitPeTooLarge);
    MUT("tls-reserved-flags", tls + 36, 4, 1, WitPeUnsupportedImage);
    MUT("tls-alignment-quota", tls + 36, 4, 0xF00000, WitPeUnsupportedImage);
    MUT("tls-callback-outside-image", tls + 24, 8, baseline.PreferredBase + baseline.ImageSize + 8, WitPeInvalidImage);
    MUT("tls-nonempty-callbacks", tls + 24, 8, baseline.PreferredBase + baseline.UnwindRva, WitPeUnsupportedImage);
    MUT("relocation-small-block", reloc + 4, 4, 4, WitPeInvalidImage);
    MUT("relocation-unaligned-block", reloc + 4, 4, 10, WitPeInvalidImage);
    MUT("relocation-truncated-block", reloc + 4, 4, 0xFFFFFFFCU, WitPeInvalidImage);
    MUT("relocation-page-alignment", reloc, 4, read32(data + reloc) + 1, WitPeInvalidImage);
    MUT("relocation-page-outside-image", reloc, 4, baseline.ImageSize, WitPeInvalidImage);
    MUT("relocation-unsupported-kind", reloc + 8, 2, 0x3000, WitPeUnsupportedImage);
    // Controls: changes to unused DOS stub/checksum bytes must remain acceptable.
    MUT("ignored-dos-stub", 64, 1, data[64] ^ 0x40, WitPeOk);
    MUT("ignored-checksum", optional + 64, 4, 0x73A51234, WitPeOk);
#undef MUT
    if (structural != 26) {
        exit(20);
    }
    printf("PASS: %u structural PE verdict cases (pdata/xdata/TLS/relocations and valid controls)\n", structural);
}

int main(int argc, char **argv)
{
    if (argc != 2) {
        return 7;
    }
    FILE *f = fopen(argv[1], "rb");
    if (!f) {
        return 8;
    }
    fseek(f, 0, SEEK_END);
    long length = ftell(f);
    rewind(f);
    if (length < 4096 || (unsigned long)length > WIT_PE_MAX_FILE_SIZE) {
        return 9;
    }
    unsigned n = (unsigned)length;
    unsigned char *data = malloc(n);
    if (!data) {
        return 10;
    }
    if (fread(data, 1, n, f) != n) {
        return 11;
    }
    fclose(f);
    if (wit_pe_validate_profile(data, n, &plan, 5) != WitPeOk) {
        return 12;
    }
    verify(data, n);
    {
        unsigned ntHeader = read32(data + 0x3c);
        deep_cases(data, n, ntHeader + 24);
    }
    if (wit_pe_validate_profile(data, n, &plan, WIT_PE_UNWIND_RUNTIME) != WitPeTooLarge) {
        return 13;
    }
    unsigned nt = 0, originalImageSize = 0;
    memcpy(&nt, data + 0x3c, 4);
    const unsigned imageSizeOffset = nt + 24 + 56;
    memcpy(&originalImageSize, data + imageSizeOffset, 4);
    // The parser correctly requires SizeOfImage to match the final section's
    // mapped end. Extend that section's zero-fill tail for a valid boundary PE.
    unsigned short sectionCount = 0;
    memcpy(&sectionCount, data + nt + 6, 2);
    unsigned lastHeader = 0, lastRva = 0, originalVirtualSize = 0;
    for (unsigned i = 0; i < sectionCount; ++i) {
        unsigned header = nt + 24 + 240 + i * 40, rva = 0;
        memcpy(&rva, data + header + 12, 4);
        if (rva > lastRva) {
            lastRva = rva;
            lastHeader = header;
        }
    }
    if (!lastHeader) {
        return 16;
    }
    memcpy(&originalVirtualSize, data + lastHeader + 8, 4);
    unsigned cap = WIT_PE_FULL_IMAGE_SIZE, extent = cap - lastRva;
    memcpy(data + imageSizeOffset, &cap, 4);
    memcpy(data + lastHeader + 8, &extent, 4);
    WitPeStatus boundary = verify(data, n);
    if (boundary != WitPeOk) {
        printf("FAIL: exact full-image cap status %u\n", (unsigned)boundary);
        return 14;
    }
    cap += 4096;
    extent = cap - lastRva;
    memcpy(data + imageSizeOffset, &cap, 4);
    memcpy(data + lastHeader + 8, &extent, 4);
    boundary = verify(data, n);
    if (boundary != WitPeTooLarge) {
        printf("FAIL: excessive full-image cap status %u\n", (unsigned)boundary);
        return 15;
    }
    memcpy(data + imageSizeOffset, &originalImageSize, 4);
    memcpy(data + lastHeader + 8, &originalVirtualSize, 4);
    for (unsigned size = 0; size <= 1024; size += 4) {
        verify(data, size);
    }
    for (unsigned gap = 1; gap <= 4096; gap *= 2) {
        verify(data, n - gap);
    }
    unsigned seed = 0x57314A29;
    for (unsigned i = 0; i < 256; ++i) {
        seed = seed * 1664525u + 1013904223u;
        unsigned offset = seed % 1024;
        unsigned char original = data[offset];
        data[offset] ^= (unsigned char)(1u << ((seed >> 16) & 7));
        verify(data, n);
        data[offset] = original;
    }
    printf("PASS: %u immutable trailing-guard PE inputs, %u accepted, %u rejected; seed=0x57314A29; verdicts=0x%08X\n",
        cases, accepted, rejected, verdicts);
    free(data);
    return 0;
}
