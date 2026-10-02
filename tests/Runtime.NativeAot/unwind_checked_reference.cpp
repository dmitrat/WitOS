#include "unwind_checked.witos.h"
#include <stdio.h>
#include <string.h>

namespace {
struct Region {
    unsigned char *Bytes;

    explicit Region(unsigned size = 4096)
        : Bytes((unsigned char *)VirtualAlloc(nullptr, size + 4096, MEM_RESERVE, PAGE_NOACCESS))
    {
        if (Bytes && !VirtualAlloc(Bytes, size, MEM_COMMIT, PAGE_READWRITE)) {
            VirtualFree(Bytes, 0, MEM_RELEASE);
            Bytes = nullptr;
        }
    }

    ~Region()
    {
        if (Bytes) {
            VirtualFree(Bytes, 0, MEM_RELEASE);
        }
    }
};
}

extern "C" bool checked_failure_tests()
{
    Region imageMemory, stackMemory;
    if (!imageMemory.Bytes || !stackMemory.Bytes) {
        return false;
    }
    auto bytes = imageMemory.Bytes;
    WitUserImageInfo image = {};
    image.Version = WIT_IMAGE_INFO_VERSION;
    image.Size = sizeof(image);
    image.Base = (WitU64)bytes;
    image.ImageSize = 4096;
    image.RangeCount = 2;
    image.Ranges[0] = {64, 16, 16, WIT_IMAGE_INFO_READ | WIT_IMAGE_INFO_EXECUTE};
    image.Ranges[1] = {256, 3840, 3840, WIT_IMAGE_INFO_READ};
    image.UnwindRva = 3584;
    image.UnwindSize = 12;
    auto entry = (RUNTIME_FUNCTION *)(bytes + 3584);
    auto reset = [&]() {
        memset(bytes, 0, 4096);
        memset(bytes + 64, 0x90, 16);
        *entry = {64, 80, 256};
        const unsigned char info[] = {1, 5, 2, 0, 5, 0x32, 1, 0x30};
        memcpy(bytes + 256, info, sizeof(info));
        memset(stackMemory.Bytes, 0x5A, 4096);
    };
    reset();
    const WitU64 low = (WitU64)stackMemory.Bytes;
    CONTEXT seed = {};
    seed.ContextFlags = CONTEXT_FULL;
    seed.Rsp = low + 4056;
    seed.Rip = image.Base + 70;
    unsigned cases = 0;
    auto reject = [&](CONTEXT input, WitUnwindStackRange range, PRUNTIME_FUNCTION supplied, DWORD flags = 0,
                      bool budget = false) {
        CONTEXT result = input;
        void *data = (void *)0x1234;
        DWORD64 frame = 0xFEDCBA9876543210ULL;
        auto handler = (PEXCEPTION_ROUTINE)(uintptr_t)0x5678;
        KNONVOLATILE_CONTEXT_POINTERS pointers;
        memset(&pointers, 0xA5, sizeof(pointers));
        const auto before = pointers;
        bool failed = false;
        try {
            const auto status = wit_checked_virtual_unwind(
                &image, &range, flags, input.Rip, supplied, &result, &data, &frame, &pointers, &handler);
            failed = !budget && FAILED(status);
        } catch (const WitUnwindAccessFailure &failure) {
            failed = !budget || failure.Reason == WitUnwindFailureReason::Budget;
        }
        ++cases;
        if (!failed ||
            memcmp(&input, &result, sizeof(input)) ||
            data != (void *)0x1234 ||
            frame != 0xFEDCBA9876543210ULL ||
            handler != (PEXCEPTION_ROUTINE)(uintptr_t)0x5678 ||
            memcmp(&pointers, &before, sizeof(before))) {
            printf("checked failure not transactional: %u\n", cases);
            return false;
        }
        return true;
    };
    WitUnwindStackRange range = {low, low + 4096};
    // RBX is restored in the private working context, then return-address read hits the guard page.
    if (!reject(seed, range, entry)) {
        return false;
    }
    auto c = seed;
    c.Rsp = low + 4095;
    if (!reject(c, range, entry)) {
        return false;
    }
    c = seed;
    c.Rsp = low - 1;
    if (!reject(c, range, entry)) {
        return false;
    }
    if (!reject(seed, {low, low}, entry) ||
        !reject(seed, range, (PRUNTIME_FUNCTION)(uintptr_t)1) ||
        !reject(seed, range, entry, 4)) {
        return false;
    }
    c = seed;
    c.Rip = image.Base + 80;
    if (!reject(c, range, entry)) {
        return false;
    }
    // A REX prefix at the final declared code byte requires a forbidden second byte.
    reset();
    bytes[79] = 0x48;
    c = seed;
    c.Rip = image.Base + 79;
    if (!reject(c, range, entry)) {
        return false;
    }
    reset();
    bytes[258] = 1;
    bytes[261] = 1;
    if (!reject(seed, range, entry)) {
        return false; // Truncated ALLOC_LARGE.
    }
    // A frame register must not turn a wrapped address into a valid stack read.
    reset();
    bytes[258] = 1;
    bytes[259] = 0x15;
    bytes[260] = 1;
    bytes[261] = 3;
    c = seed;
    c.Rbp = 8;
    if (!reject(c, range, entry)) {
        return false;
    }
    // Machine-frame reads succeed, but the restored RSP is outside the owned stack.
    reset();
    bytes[258] = 1;
    bytes[260] = 1;
    bytes[261] = 10;
    c = seed;
    c.Rsp = low + 128;
    *(WitU64 *)(stackMemory.Bytes + 128) = image.Base + 64;
    *(WitU64 *)(stackMemory.Bytes + 152) = low - 8;
    if (!reject(c, range, entry)) {
        return false;
    }
    // A 16-byte XMM restore straddles the physical guard page.
    reset();
    bytes[258] = 2;
    bytes[260] = 1;
    bytes[261] = 0x68;
    bytes[262] = bytes[263] = 0;
    c = seed;
    c.Rsp = low + 4088;
    if (!reject(c, range, entry)) {
        return false;
    }
    // A long valid code range containing only POPs cannot drive an unbounded scan.
    Region large(81920);
    if (!large.Bytes) {
        return false;
    }
    const auto previousImage = image;
    const auto smallEntry = entry;
    image.Base = (WitU64)large.Bytes;
    image.ImageSize = 81920;
    image.Ranges[0] = {64, 70000, 70000, WIT_IMAGE_INFO_READ | WIT_IMAGE_INFO_EXECUTE};
    image.Ranges[1] = {73728, 8192, 8192, WIT_IMAGE_INFO_READ};
    image.UnwindRva = 73728;
    entry = (RUNTIME_FUNCTION *)(large.Bytes + 73728);
    *entry = {64, 70064, 73744};
    large.Bytes[73744] = 1;
    memset(large.Bytes + 64, 0x58, 70000);
    c = seed;
    c.Rip = image.Base + 64;
    if (!reject(c, range, entry, 0, true)) {
        return false;
    }
    image = previousImage;
    entry = smallEntry;
    // Access failure must unwind the hosted scope, allowing a subsequent valid request.
    reset();
    c = seed;
    c.Rsp = low + 128;
    *(WitU64 *)(stackMemory.Bytes + 168) = image.Base + 64;
    void *data = nullptr;
    DWORD64 frame = 0;
    PEXCEPTION_ROUTINE handler = nullptr;
    try {
        if (wit_checked_virtual_unwind(&image, &range, 0, c.Rip, entry, &c, &data, &frame, nullptr, &handler) != S_OK ||
            c.Rsp != low + 176 ||
            c.Rip != image.Base + 64 ||
            c.Rbx != 0x5A5A5A5A5A5A5A5AULL) {
            return false;
        }
    } catch (const WitUnwindAccessFailure &) {
        return false;
    }
    printf("PASS: %u transactional checked unwind failures\n", cases);
    return true;
}
