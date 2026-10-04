#include "unwind_environment.h"
#include "unwinder.h"
#include "unwind_checked.witos.h"
#include <string.h>
extern "C" {
void unwind_v2();
void unwind_v2_body();
void unwind_v2_epilog();
void unwind_v2_pop();
void unwind_v2_ret();
void unwind_simple();
void unwind_simple_push();
void unwind_simple_body();
void unwind_simple_epilog();
void unwind_simple_pop();
void unwind_simple_ret();
void unwind_frame_body();
void unwind_frame_epilog();
void unwind_large_body();
void unwind_primary_body();
void unwind_child();
void unwind_handled_body();
void unwind_handler();
}

HRESULT OOPStackUnwinder::GetModuleBase(DWORD64 pc, PDWORD64 base)
{
    HMODULE module = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            (LPCWSTR)pc, &module)) {
        return E_FAIL;
    }
    *base = (DWORD64)module;
    return S_OK;
}

HRESULT OOPStackUnwinder::GetFunctionEntry(DWORD64 pc, PVOID output, DWORD bytes)
{
    DWORD64 base = 0;
    auto function = RtlLookupFunctionEntry(pc, &base, nullptr);
    if (!function || bytes != sizeof(*function)) {
        return E_FAIL;
    }
    memcpy(output, function, bytes);
    return S_OK;
}

[[noreturn]] void wit_unwind_access_failure(WitUnwindFailureReason reason)
{
    throw WitUnwindAccessFailure{reason};
}

extern "C" bool describe_host_image(const void *, WitUserImageInfo *);

struct DynamicView {
    const WitUserImageInfo *Image;
    RUNTIME_FUNCTION Entry;
};

static const void *dynamic_read(void *value, DWORD64 address, DWORD size, bool executable)
{
    auto view = (DynamicView *)value;
    const auto image = view->Image;
    const auto table = (DWORD64)&view->Entry;
    if (!executable &&
        address >= table &&
        address - table < sizeof(view->Entry) &&
        size <= sizeof(view->Entry) - (address - table)) {
        return (const void *)address;
    }
    if (!size ||
        address < image->Base ||
        address - image->Base >= image->ImageSize ||
        size > image->ImageSize - (address - image->Base)) {
        return nullptr;
    }
    const auto rva = address - image->Base;
    for (WitU32 i = 0; i < image->RangeCount; ++i) {
        const auto &r = image->Ranges[i];
        if (rva >= r.Rva &&
            rva - r.Rva < r.InitializedSize &&
            size <= r.InitializedSize - (rva - r.Rva) &&
            (r.Flags & WIT_IMAGE_INFO_READ) &&
            (!executable || (r.Flags & WIT_IMAGE_INFO_EXECUTE))) {
            return (const void *)address;
        }
    }
    return nullptr;
}

static PRUNTIME_FUNCTION dynamic_lookup(void *value, DWORD64 pc)
{
    auto view = (DynamicView *)value;
    if (pc >= view->Image->Base + view->Entry.BeginAddress && pc < view->Image->Base + view->Entry.EndAddress) {
        return &view->Entry;
    }
    DWORD64 base = 0;
    auto result = RtlLookupFunctionEntry(pc, &base, nullptr);
    return base == view->Image->Base ? result : nullptr;
}

static unsigned count;

static bool check(void (*pc)(), CONTEXT seed, ULONG handlerType = 0, bool epilog = false, bool requireHandler = false)
{
    DWORD64 base = 0;
    auto function = RtlLookupFunctionEntry((DWORD64)pc, &base, nullptr);
    if (!function) {
        puts("Missing real PE function entry");
        return false;
    }
    seed.Rip = (DWORD64)pc;
    CONTEXT actual = seed, expected = seed;
    void *actualData = (void *)0x1234, *expectedData = (void *)0x1234;
    DWORD64 actualFrame = 0, expectedFrame = 0;
    KNONVOLATILE_CONTEXT_POINTERS actualPointers = {}, expectedPointers = {};
    PEXCEPTION_ROUTINE actualHandler = nullptr;
    HRESULT hr = OOPStackUnwinderAMD64::VirtualUnwind(
        handlerType, base, (DWORD64)pc, function, &actual, &actualData, &actualFrame, &actualPointers, &actualHandler);
    auto expectedHandler = RtlVirtualUnwind(
        handlerType, base, (DWORD64)pc, function, &expected, &expectedData, &expectedFrame, &expectedPointers);
    WitUserImageInfo image = {};
    if (!describe_host_image((void *)base, &image)) {
        return false;
    }
    ULONG_PTR low = 0, high = 0;
    GetCurrentThreadStackLimits(&low, &high);
    WitUnwindStackRange stack = {low, high};
    CONTEXT checked = seed;
    void *checkedData = (void *)0x1234;
    DWORD64 checkedFrame = 0;
    KNONVOLATILE_CONTEXT_POINTERS checkedPointers = {};
    PEXCEPTION_ROUTINE checkedHandler = nullptr;
    HRESULT checkedHr = E_FAIL;
    try {
        checkedHr = wit_checked_virtual_unwind(&image, &stack, handlerType, (DWORD64)pc, function, &checked,
            &checkedData, &checkedFrame, &checkedPointers, &checkedHandler);
    } catch (const WitUnwindAccessFailure &) {
        puts("Checked unwind access rejected valid frame");
        return false;
    }
    if (checkedHr != S_OK ||
        checkedHandler != actualHandler ||
        checkedData != actualData ||
        (!epilog && checkedFrame != actualFrame) ||
        memcmp(&checked, &actual, sizeof(actual)) ||
        memcmp(&checkedPointers, &actualPointers, sizeof(actualPointers))) {
        printf("Checked unwind mismatch hr=%08lx pc=%p\n", (unsigned long)checkedHr, pc);
        return false;
    }
    DynamicView dynamic{&image, *function};
    WitDynamicUnwindSource dynamicSource = {image.Base, image.ImageSize, &dynamic, dynamic_read, dynamic_lookup};
    CONTEXT dynamicContext = seed;
    void *dynamicData = (void *)0x1234;
    DWORD64 dynamicFrame = 0;
    KNONVOLATILE_CONTEXT_POINTERS dynamicPointers = {};
    PEXCEPTION_ROUTINE dynamicHandler = nullptr;
    const auto dynamicHr = wit_checked_virtual_unwind_dynamic(&dynamicSource, &stack, handlerType, (DWORD64)pc,
        &dynamic.Entry, &dynamicContext, &dynamicData, &dynamicFrame, &dynamicPointers, &dynamicHandler);
    if (dynamicHr != S_OK ||
        dynamicHandler != actualHandler ||
        dynamicData != actualData ||
        (!epilog && dynamicFrame != actualFrame) ||
        memcmp(&dynamicContext, &actual, sizeof(actual)) ||
        memcmp(&dynamicPointers, &actualPointers, sizeof(actualPointers))) {
        return false;
    }
    const auto savedDynamic = dynamicContext;
    const auto savedPointers = dynamicPointers;
    const auto savedData = dynamicData;
    const auto savedFrame = dynamicFrame;
    const auto savedHandler = dynamicHandler;
    auto fakeEntry = dynamic.Entry;
    if (SUCCEEDED(wit_checked_virtual_unwind_dynamic(&dynamicSource, &stack, handlerType, (DWORD64)pc, &fakeEntry,
            &dynamicContext, &dynamicData, &dynamicFrame, &dynamicPointers, &dynamicHandler))) {
        return false;
    }
    dynamic.Entry.UnwindData |= 1;
    if (SUCCEEDED(wit_checked_virtual_unwind_dynamic(&dynamicSource, &stack, handlerType, (DWORD64)pc, &dynamic.Entry,
            &dynamicContext, &dynamicData, &dynamicFrame, &dynamicPointers, &dynamicHandler)) ||
        memcmp(&dynamicContext, &savedDynamic, sizeof(dynamicContext)) ||
        memcmp(&dynamicPointers, &savedPointers, sizeof(dynamicPointers)) ||
        dynamicData != savedData ||
        dynamicFrame != savedFrame ||
        dynamicHandler != savedHandler) {
        return false;
    }
    WitValidatedUnwindImage validated;
    WitUserImageInfo invalid = image;
    invalid.UnwindSize = 1;
    if (SUCCEEDED(validated.Initialize(&invalid)) ||
        validated.Image() != nullptr ||
        FAILED(validated.Initialize(&image)) ||
        FAILED(validated.Initialize(&image)) ||
        SUCCEEDED(validated.Initialize(&invalid))) {
        return false;
    }
    // Reuse the same immutable-image token; all stack/context/output semantics
    // must remain identical to both full validation and Windows.
    for (unsigned repeat = 0; repeat < 2; ++repeat) {
        CONTEXT fast = seed;
        void *fastData = (void *)0x1234;
        DWORD64 fastFrame = 0;
        KNONVOLATILE_CONTEXT_POINTERS fastPointers = {};
        PEXCEPTION_ROUTINE fastHandler = nullptr;
        const HRESULT fastHr = wit_checked_virtual_unwind_prevalidated(validated, &stack, handlerType, (DWORD64)pc,
            function, &fast, &fastData, &fastFrame, &fastPointers, &fastHandler);
        if (fastHr != S_OK ||
            fastHandler != actualHandler ||
            fastData != actualData ||
            (!epilog && fastFrame != actualFrame) ||
            memcmp(&fast, &actual, sizeof(actual)) ||
            memcmp(&fastPointers, &actualPointers, sizeof(actualPointers))) {
            return false;
        }
        const auto before = fast;
        const auto beforeData = fastData;
        const auto beforeFrame = fastFrame;
        const auto beforePointers = fastPointers;
        const auto beforeHandler = fastHandler;
        RUNTIME_FUNCTION forged = *function;
        if (SUCCEEDED(wit_checked_virtual_unwind_prevalidated(validated, &stack, handlerType, (DWORD64)pc, &forged,
                &fast, &fastData, &fastFrame, &fastPointers, &fastHandler)) ||
            memcmp(&fast, &before, sizeof(fast)) ||
            fastData != beforeData ||
            fastFrame != beforeFrame ||
            fastHandler != beforeHandler ||
            memcmp(&fastPointers, &beforePointers, sizeof(fastPointers))) {
            return false;
        }
    }
    ++count;
    if (requireHandler) {
        printf("compiler frame unwind header=%02x prolog=%u offset=%llu flags=%lu\n",
            *(unsigned char *)(base + function->UnwindData), *((unsigned char *)(base + function->UnwindData) + 1),
            (unsigned long long)((DWORD64)pc - base - function->BeginAddress), handlerType);
    }
    // Microsoft x64 contract leaves EstablisherFrame undefined in an epilog.
    // All defined context/handler outputs remain exact comparisons.
    if ((requireHandler && !expectedHandler) ||
        hr != S_OK ||
        actualHandler != expectedHandler ||
        actualData != expectedData ||
        (!epilog && actualFrame != expectedFrame) ||
        memcmp(&actual, &expected, sizeof(actual)) ||
        memcmp(&actualPointers, &expectedPointers, sizeof(actualPointers))) {
        for (size_t offset = 0; offset < sizeof(actual); ++offset) {
            if (((unsigned char *)&actual)[offset] != ((unsigned char *)&expected)[offset]) {
                printf("context byte %zu: upstream=%02x Windows=%02x\n", offset, ((unsigned char *)&actual)[offset],
                    ((unsigned char *)&expected)[offset]);
            }
        }
        printf("FAIL case %u pc=%p hr=%08lx rip=%llx/%llx rsp=%llx/%llx frame=%llx/%llx handler=%p/%p data=%p/%p\n",
            count, pc, (unsigned long)hr, actual.Rip, expected.Rip, actual.Rsp, expected.Rsp, actualFrame,
            expectedFrame, actualHandler, expectedHandler, actualData, expectedData);
        return false;
    }
    return true;
}

static bool compilerFramePassed;

extern "C" __declspec(noinline) void capture_parent(volatile char *buffer)
{
    buffer[1] = 17;
    CONTEXT live = {};
    RtlCaptureContext(&live);
    DWORD64 base = 0, frame = 0;
    PVOID data = nullptr;
    auto entry = RtlLookupFunctionEntry(live.Rip, &base, nullptr);
    if (!entry) {
        return;
    }
    RtlVirtualUnwind(0, base, live.Rip, entry, &live, &data, &frame, nullptr);
    compilerFramePassed = check((void (*)())live.Rip, live, UNW_FLAG_UHANDLER, false, true);
}

extern "C" int protected_frame();
extern "C" bool metadata_tests();
extern "C" bool checked_failure_tests();
extern "C" bool validate_host_image(const char *);

int main(int argc, char **argv)
{
    if (!checked_failure_tests() ||
        !metadata_tests() ||
        !validate_host_image(nullptr) ||
        (argc > 1 && !validate_host_image(argv[1]))) {
        return 20;
    }
    alignas(16) unsigned char stack[8192];
    memset(stack, 0x5A, sizeof(stack));
    auto body = (DWORD64)(stack + 512);
    *(DWORD64 *)(body + 32) = 0x1122334455667788ULL;
    *(DWORD64 *)(body + 40) = (DWORD64)&unwind_simple_body;
    CONTEXT c = {};
    c.ContextFlags = CONTEXT_FULL;
    c.Rsp = body;
    c.Rbx = 0x8877665544332211ULL;
    if (!check(unwind_simple_body, c) || !check(unwind_simple_epilog, c, 0, true)) {
        return 1;
    }
    c.Rsp = body + 32;
    if (!check(unwind_simple_push, c) || !check(unwind_simple_pop, c, 0, true)) {
        return 2;
    }
    c.Rsp = body + 40;
    c.Rbx = *(DWORD64 *)(body + 32);
    if (!check(unwind_simple, c) || !check(unwind_simple_ret, c, 0, true)) {
        return 3;
    }
    c = {};
    c.ContextFlags = CONTEXT_FULL;
    c.Rsp = body;
    c.Rbp = body + 64;
    *(DWORD64 *)(body + 128) = 0xABCDEF1234567890ULL;
    *(DWORD64 *)(body + 136) = (DWORD64)&unwind_simple_body;
    *(M128A *)(body + 32) = {0x1111222233334444ULL, 0x5555666677778888LL};
    if (!check(unwind_frame_body, c)) {
        return 4;
    }
    c.Xmm6 = *(M128A *)(body + 32);
    if (!check(unwind_frame_epilog, c, 0, true)) {
        return 5;
    }
    c = {};
    c.ContextFlags = CONTEXT_FULL;
    c.Rsp = body;
    *(DWORD64 *)(body + 4096) = (DWORD64)&unwind_simple_body;
    if (!check(unwind_large_body, c)) {
        return 6;
    }
    *(DWORD64 *)(body + 32) = 0x1122334455667788ULL;
    *(DWORD64 *)(body + 40) = (DWORD64)&unwind_simple_body;
    c.Rsp = body;
    c.Rbx = 0;
    if (!check(unwind_primary_body, c) ||
        !check(unwind_child, c) ||
        !check(unwind_handled_body, c) ||
        !check(unwind_handled_body, c, UNW_FLAG_EHANDLER)) {
        return 7;
    }
    c = {};
    c.ContextFlags = CONTEXT_FULL;
    c.Rsp = body;
    *(DWORD64 *)body = 0x1122334455667788ULL;
    *(DWORD64 *)(body + 8) = 0x8877665544332211ULL;
    *(DWORD64 *)(body + 16) = (DWORD64)&unwind_simple_body;
    if (!check(unwind_v2_body, c) || !check(unwind_v2_epilog, c, 0, true)) {
        return 9;
    }
    c.Rsi = *(DWORD64 *)body;
    c.Rsp = body + 8;
    if (!check(unwind_v2_pop, c, 0, true)) {
        return 10;
    }
    c.Rdi = *(DWORD64 *)(body + 8);
    c.Rsp = body + 16;
    if (!check(unwind_v2_ret, c, 0, true) || !check(unwind_v2, c)) {
        return 11;
    }
    // CoffNativeCodeManager intentionally supplies a partial context in release builds.
    // Fields unrelated to the selected frame must remain byte-for-byte unchanged.
    memset(&c, 0xCD, sizeof(c));
    // Flags are a defined Windows input: poisoning them requests unsupported
    // architecture/reporting bits that modern Windows normalizes, unlike the
    // pinned upstream algorithm. Poison unused register fields, not flags.
    c.ContextFlags = CONTEXT_FULL;
    c.Rsp = body;
    c.Rbp = body;
    c.Rip = (DWORD64)&unwind_simple_body;
    *(DWORD64 *)(body + 32) = 0x1122334455667788ULL;
    *(DWORD64 *)(body + 40) = (DWORD64)&unwind_simple_body;
    if (!check(unwind_simple_body, c)) {
        return 12;
    }
    if (protected_frame() != 59 || !compilerFramePassed) {
        return 8;
    }
    puts("PASS: 20 dynamic-source Windows comparisons and 40 transactional entry/metadata rejections");
    printf("PASS: %u immutable-image cached unwind comparisons and %u forged-entry rejections\n", count * 2, count * 2);
    printf("PASS: %u checked unwind differential cases\n", count);
    printf("PASS: %u upstream AMD64 unwind differential cases\n", count);
    return 0;
}
