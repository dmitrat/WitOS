#include "security_handler.witos.h"
#include "native_security.h"
#include <stddef.h>
extern "C" void __cdecl __security_check_cookie(uintptr_t);
extern "C" EXCEPTION_DISPOSITION __cdecl __GSHandlerCheck(EXCEPTION_RECORD *, void *, CONTEXT *, DISPATCHER_CONTEXT *);
static_assert(offsetof(DISPATCHER_CONTEXT, FunctionEntry) == 16 && offsetof(DISPATCHER_CONTEXT, HandlerData) == 56);

namespace {
WitU32 read32(const void *address)
{
    auto p = (const volatile unsigned char *)address;
    return WitU32(p[0]) | (WitU32(p[1]) << 8) | (WitU32(p[2]) << 16) | (WitU32(p[3]) << 24);
}

WitU64 add(WitU64 address, int32_t offset)
{
    if (offset >= 0) {
        if (address > UINT64_MAX - WitU64(offset)) {
            wit_native_security_failure();
        }
        return address + WitU64(offset);
    }
    const auto magnitude = WitU64(-int64_t(offset));
    if (address < magnitude) {
        wit_native_security_failure();
    }
    return address - magnitude;
}

bool readonly(const WitUserImageInfo *image, WitU64 address, WitU64 size)
{
    return wit_native_image_range(
               image, address, size, WIT_IMAGE_INFO_READ, WIT_IMAGE_INFO_WRITE | WIT_IMAGE_INFO_EXECUTE, 1) != 0;
}

bool stack(const WitUserThreadInfo &info, WitU64 address, WitU64 bytes)
{
    return address >= info.StackLow && address <= info.StackHigh && bytes <= info.StackHigh - address;
}
}

extern "C" WitU32 wit_native_gs_check(
    void *establisher, DISPATCHER_CONTEXT *dispatcher, WitU64 expectedHandler, WitU64 cookieData)
{
    // Shared v1 GS cookie contract. The combined handler validates its complete
    // scope table and derives the trailing GS data before entering this helper.
    const auto image = wit_native_process_image();
    if (!image || !dispatcher || dispatcher->ImageBase != image->Base) {
        wit_native_security_failure();
    }
    const auto function = (WitU64)dispatcher->FunctionEntry;
    if (!readonly(image, function, 12)) {
        wit_native_security_failure();
    }
    const auto begin = read32((void *)function), end = read32((void *)(function + 4)),
               unwind = read32((void *)(function + 8));
    if (begin >= end ||
        unwind >= image->ImageSize ||
        (unwind & 3) ||
        !wit_native_image_range(image, image->Base + begin, end - begin, WIT_IMAGE_INFO_READ | WIT_IMAGE_INFO_EXECUTE,
            WIT_IMAGE_INFO_WRITE, 1)) {
        wit_native_security_failure();
    }
    const auto address = image->Base + unwind;
    if (!readonly(image, address, 4)) {
        wit_native_security_failure();
    }
    const auto header = (const volatile unsigned char *)address;
    const unsigned flags = header[0] >> 3, frame = header[3] & 15;
    if ((header[0] & 7) != 1 ||
        !flags ||
        (flags & ~3U) ||
        (!frame && (header[3] & 0xF0U)) ||
        (frame && frame != 3 && frame != 5 && frame != 6 && frame != 7 && frame < 12)) {
        wit_native_security_failure();
    }
    const WitU64 headerBytes = 4 + ((WitU64(header[2]) + 1) & ~1ULL) * 2;
    if (!readonly(image, address, headerBytes + 8) ||
        image->Base + read32((void *)(address + headerBytes)) != expectedHandler ||
        (WitU64)dispatcher->HandlerData != address + headerBytes + 4) {
        wit_native_security_failure();
    }
    if (!readonly(image, cookieData, 4)) {
        wit_native_security_failure();
    }
    const auto data = (const unsigned char *)cookieData;
    const auto encoded = int32_t(read32(data));
    if ((encoded & 4) && !readonly(image, (WitU64)data, 12)) {
        wit_native_security_failure();
    }
    WitUserThreadInfo info;
    WitU64 copied = 0;
    if (wit_native_call(WIT_CALL_THREAD_QUERY, (WitU64)&info, sizeof(info), WIT_THREAD_INFO_VERSION, &copied) !=
            WIT_STATUS_OK ||
        copied != sizeof(info) ||
        info.Version != WIT_THREAD_INFO_VERSION ||
        info.Size != sizeof(info) ||
        !info.ThreadId ||
        info.StackLow >= info.StackHigh) {
        wit_native_security_failure();
    }
    const auto frameAddress = (WitU64)establisher;
    if (!stack(info, frameAddress, 0)) {
        wit_native_security_failure();
    }
    WitU64 aligned = frameAddress;
    if (encoded & 4) {
        const auto alignment = read32(data + 8);
        if (!alignment || (alignment & (alignment - 1))) {
            wit_native_security_failure();
        }
        aligned = add(frameAddress, int32_t(read32(data + 4))) & ~(WitU64(alignment) - 1);
    }
    const auto cookieAddress = add(aligned, encoded & ~7);
    const auto xorBase = frameAddress + (frame ? (header[3] & 0xF0U) : 0);
    if (!stack(info, cookieAddress, 8) || (cookieAddress & 7) || xorBase < frameAddress || !stack(info, xorBase, 0)) {
        wit_native_security_failure();
    }
    const auto encodedCookie = *(const volatile WitU64 *)cookieAddress;
    __security_check_cookie((uintptr_t)(encodedCookie ^ xorBase));
    return WitU32(encoded) & 3U;
}

extern "C" EXCEPTION_DISPOSITION __cdecl __GSHandlerCheck(
    EXCEPTION_RECORD *, void *frame, CONTEXT *, DISPATCHER_CONTEXT *dispatcher)
{
    wit_native_gs_check(frame, dispatcher, (WitU64)&__GSHandlerCheck, dispatcher ? (WitU64)dispatcher->HandlerData : 0);
    return ExceptionContinueSearch;
}
