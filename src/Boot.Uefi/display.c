#include "uefi.h"
#include "witos/boot.h"
#include "witos/platform.h"

/* The firmware's framebuffer (plan step C2.1): the display the firmware's graphics output protocol set up, recorded in
 * the boot contract as physical memory with its geometry and pixel format, so that the platform can publish it as a
 * device and a display service can map it. The protocol is optional — a board without a display, or one whose
 * firmware offers no linear framebuffer, boots as before — and the loader changes no mode: what the firmware shows
 * is what the kernel inherits. */

static int whole_pages(WitU64 base, WitU64 bytes)
{
    return (base & 4095) == 0 && (bytes & 4095) == 0 && bytes != 0 && base <= ~0ULL - bytes;
}

void wit_boot_display(EfiBootServicesPrefix *services, WitBootInfo *boot)
{
    static const EfiGuid guid = {0x9042a9de, 0x23dc, 0x4a38, {0x96, 0xfb, 0x7a, 0xde, 0xd0, 0x80, 0x51, 0x6a}};
    EfiGraphicsOutputProtocol *output = 0;
    boot->FramebufferBase = 0;
    boot->FramebufferBytes = 0;
    boot->FramebufferWidth = 0;
    boot->FramebufferHeight = 0;
    boot->FramebufferStride = 0;
    boot->FramebufferFormat = 0;
    if (!services->LocateProtocol ||
        services->LocateProtocol(&guid, 0, (void **)&output) != EFI_SUCCESS ||
        !output ||
        !output->Mode ||
        !output->Mode->Info) {
        wit_console_write("[BOOT] No framebuffer\n");
        return;
    }
    const EfiGraphicsOutputProtocolMode *mode = output->Mode;
    const EfiGraphicsOutputModeInformation *info = mode->Info;
    const WitU32 format = info->PixelFormat == EFI_PIXEL_BLUE_GREEN_RED_RESERVED ? WIT_PIXEL_BGRX
        : info->PixelFormat == EFI_PIXEL_RED_GREEN_BLUE_RESERVED                 ? WIT_PIXEL_RGBX
                                                                                 : 0;
    /* The framebuffer the kernel takes is a linear one of whole pages whose geometry the bytes hold; anything else
     * (a bit-mask format, a Blt-only output, an odd range) is reported and left. */
    const WitU64 needed = (WitU64)info->PixelsPerScanLine * info->VerticalResolution * 4;
    if (format == 0 ||
        mode->SizeOfInfo < sizeof(*info) ||
        info->HorizontalResolution == 0 ||
        info->VerticalResolution == 0 ||
        info->PixelsPerScanLine < info->HorizontalResolution ||
        mode->FrameBufferBase == 0 ||
        !whole_pages(mode->FrameBufferBase, mode->FrameBufferSize) ||
        mode->FrameBufferSize < needed) {
        wit_console_write("[BOOT] Framebuffer not linear: format ");
        wit_console_write_u64(info->PixelFormat);
        wit_console_write(", ");
        wit_console_write_u64(info->HorizontalResolution);
        wit_console_write("x");
        wit_console_write_u64(info->VerticalResolution);
        wit_console_write(" at ");
        wit_console_write_hex(mode->FrameBufferBase);
        wit_console_write(" of ");
        wit_console_write_u64(mode->FrameBufferSize);
        wit_console_write(" bytes; left\n");
        return;
    }
    boot->FramebufferBase = mode->FrameBufferBase;
    boot->FramebufferBytes = mode->FrameBufferSize;
    boot->FramebufferWidth = info->HorizontalResolution;
    boot->FramebufferHeight = info->VerticalResolution;
    boot->FramebufferStride = info->PixelsPerScanLine;
    boot->FramebufferFormat = format;
    wit_console_write("[BOOT] Framebuffer: ");
    wit_console_write_u64(info->HorizontalResolution);
    wit_console_write("x");
    wit_console_write_u64(info->VerticalResolution);
    wit_console_write(", stride ");
    wit_console_write_u64(info->PixelsPerScanLine);
    wit_console_write(format == WIT_PIXEL_BGRX ? ", BGRX at " : ", RGBX at ");
    wit_console_write_hex(mode->FrameBufferBase);
    wit_console_write(" of ");
    wit_console_write_u64(mode->FrameBufferSize);
    wit_console_write(" bytes, mode ");
    wit_console_write_u64(mode->Mode);
    wit_console_write(" of ");
    wit_console_write_u64(mode->MaxMode);
    wit_console_write("\n");
}
