#include "witos/boot.h"
#include "witos/platform.h"

extern const WitU8 __ImageBase[];
static WitImageSection image_sections[WIT_MAX_IMAGE_SECTIONS];

static WitU16 read16(const WitU8 *p)
{
    return (WitU16)(p[0] | ((WitU16)p[1] << 8));
}

static WitU32 read32(const WitU8 *p)
{
    return p[0] | ((WitU32)p[1] << 8) | ((WitU32)p[2] << 16) | ((WitU32)p[3] << 24);
}

void wit_boot_describe_image(WitBootInfo *boot)
{
    const WitU8 *base = __ImageBase;
    const WitU32 pe = read32(base + 0x3C);
    WitU32 count;
    WitU32 optional_size;
    WitU32 image_size;
    WitU32 header_size;

    if (read16(base) != 0x5A4D ||
        pe < 0x40 ||
        pe > 0x1000 ||
        read32(base + pe) != 0x00004550 ||
        read16(base + pe + 4) != 0x8664) {
        wit_panic("Invalid native image headers");
    }
    count = read16(base + pe + 6);
    optional_size = read16(base + pe + 20);
    if (count == 0 ||
        count > WIT_MAX_IMAGE_SECTIONS ||
        optional_size < 112 ||
        optional_size > 512 ||
        read16(base + pe + 24) != 0x20B) {
        wit_panic("Unsupported native image layout");
    }
    image_size = read32(base + pe + 24 + 56);
    header_size = read32(base + pe + 24 + 60);
    if (read32(base + pe + 24 + 32) != 4096 ||
        image_size == 0 ||
        (image_size & 4095) != 0 ||
        ((WitU64)base & 4095) != 0 ||
        (WitU64)base + image_size > 0x100000000ULL ||
        header_size > image_size ||
        pe + 24 + optional_size + count * 40 > header_size) {
        wit_panic("Unsupported native image alignment");
    }
    for (WitU32 i = 0; i < count; ++i) {
        const WitU8 *section = base + pe + 24 + optional_size + i * 40;
        const WitU32 virtual_size = read32(section + 8);
        const WitU32 raw_size = read32(section + 16);
        const WitU32 offset = read32(section + 12);
        const WitU32 characteristics = read32(section + 36);
        const WitU64 size = (((WitU64)(virtual_size > raw_size ? virtual_size : raw_size)) + 4095) & ~4095ULL;
        WitU32 flags = WIT_IMAGE_READ;
        if (characteristics & 0x80000000U) {
            flags |= WIT_IMAGE_WRITE;
        }
        if (characteristics & 0x20000000U) {
            flags |= WIT_IMAGE_EXECUTE;
        }
        if (size == 0 ||
            (offset & 4095) != 0 ||
            offset < header_size ||
            offset >= image_size ||
            size > image_size - offset ||
            (flags & (WIT_IMAGE_WRITE | WIT_IMAGE_EXECUTE)) == (WIT_IMAGE_WRITE | WIT_IMAGE_EXECUTE)) {
            wit_panic("Invalid native image section");
        }
        image_sections[i].Base = (WitU64)base + offset;
        image_sections[i].Length = size;
        image_sections[i].Flags = flags;
        image_sections[i].Reserved = 0;
    }
    boot->ImageBase = (WitU64)base;
    boot->ImageSize = image_size;
    boot->ImageSectionCount = count;
    boot->Reserved = 0;
    boot->ImageSections = image_sections;
}
