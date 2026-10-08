#include "witos/arch.h"
#include "witos/boot.h"
#include "witos/platform.h"
#include "witos/processor.h"

/* The processors of the q35 board (plan step K7.1) from ACPI: the RSDP the loader published leads to the XSDT (or
 * the RSDT of a revision 1 firmware) and to the MADT, whose Processor Local APIC and Processor Local x2APIC entries
 * name the processors; a processor is usable when enabled or online capable. The tables are read through device-page
 * mappings of their pages, each page mapped once; every table's length and checksum are verified before its body is
 * read, and nothing else of ACPI is interpreted here. */

#define RSDP_BYTES 36U
#define HEADER_BYTES 36U
#define TABLE_LIMIT 65536U
#define MADT_ENTRIES 44U
#define ENTRY_LOCAL_APIC 0U
#define ENTRY_LOCAL_X2APIC 9U
#define LAPIC_ENABLED 1U
#define LAPIC_ONLINE_CAPABLE 2U
#define MAPPED_PAGES 64U

static WitU64 mapped[MAPPED_PAGES];
static WitU32 mapped_count;

/* A page of a table: usable RAM is in the kernel's identity map already; anything else is mapped once as a device
 * page (the tables live in the firmware's reserved memory). */
static void map_once(const WitBootInfo *boot, WitU64 page)
{
    for (WitU32 i = 0; i < boot->MemoryRegionCount; ++i) {
        const WitMemoryRegion *r = &boot->MemoryRegions[i];
        if (r->Kind == WIT_MEMORY_USABLE && page >= r->Base && page - r->Base < r->Length) {
            return;
        }
    }
    for (WitU32 i = 0; i < mapped_count; ++i) {
        if (mapped[i] == page) {
            return;
        }
    }
    if (mapped_count == MAPPED_PAGES) {
        wit_panic("ACPI tables span more pages than the platform maps");
    }
    mapped[mapped_count++] = page;
    wit_arch_map_device_page(boot, page);
}

static const WitU8 *map(const WitBootInfo *boot, WitU64 physical, WitU32 bytes)
{
    for (WitU64 page = physical & ~4095ULL; page < physical + bytes; page += 4096) {
        map_once(boot, page);
    }
    return (const WitU8 *)physical;
}

static WitU32 le32(const WitU8 *p)
{
    return (WitU32)p[0] | ((WitU32)p[1] << 8) | ((WitU32)p[2] << 16) | ((WitU32)p[3] << 24);
}

static WitU64 le64(const WitU8 *p)
{
    return (WitU64)le32(p) | ((WitU64)le32(p + 4) << 32);
}

static int checksum_ok(const WitU8 *bytes, WitU32 length)
{
    WitU8 sum = 0;
    for (WitU32 i = 0; i < length; ++i) {
        sum = (WitU8)(sum + bytes[i]);
    }
    return sum == 0;
}

static int signature(const WitU8 *bytes, const char *expected, WitU32 length)
{
    for (WitU32 i = 0; i < length; ++i) {
        if (bytes[i] != (WitU8)expected[i]) {
            return 0;
        }
    }
    return 1;
}

/* A verified table at a physical address: its header mapped, its length within bounds, its body mapped and summed. */
static const WitU8 *table(const WitBootInfo *boot, WitU64 physical, WitU32 *length)
{
    const WitU8 *header = map(boot, physical, HEADER_BYTES);
    *length = le32(header + 4);
    if (*length < HEADER_BYTES || *length > TABLE_LIMIT) {
        return 0;
    }
    const WitU8 *body = map(boot, physical, *length);
    return checksum_ok(body, *length) ? body : 0;
}

static WitU32 read_madt(const WitU8 *madt, WitU32 length, WitProcessorDescriptor *out, WitU32 capacity)
{
    WitU32 count = 0;
    for (WitU32 offset = MADT_ENTRIES; offset + 2 <= length && count < capacity;) {
        const WitU8 *entry = madt + offset;
        const WitU32 type = entry[0], size = entry[1];
        if (size < 2 || offset + size > length) {
            break;
        }
        if (type == ENTRY_LOCAL_APIC && size >= 8) {
            const WitU32 flags = le32(entry + 4);
            out[count].HardwareId = entry[3];
            out[count].Enabled = (flags & (LAPIC_ENABLED | LAPIC_ONLINE_CAPABLE)) != 0;
            out[count].Reserved = 0;
            ++count;
        } else if (type == ENTRY_LOCAL_X2APIC && size >= 16) {
            const WitU32 flags = le32(entry + 8);
            out[count].HardwareId = le32(entry + 4);
            out[count].Enabled = (flags & (LAPIC_ENABLED | LAPIC_ONLINE_CAPABLE)) != 0;
            out[count].Reserved = 0;
            ++count;
        }
        offset += size;
    }
    return count;
}

WitU32 wit_platform_processors(const WitBootInfo *boot, WitProcessorDescriptor *out, WitU32 capacity)
{
    WitU32 root_length = 0;
    if (!boot->AcpiRsdp || !capacity) {
        return 0;
    }
    const WitU8 *rsdp = map(boot, boot->AcpiRsdp, RSDP_BYTES);
    if (!signature(rsdp, "RSD PTR ", 8) || !checksum_ok(rsdp, 20)) {
        return 0;
    }
    WitU64 root = le32(rsdp + 16);
    WitU32 entry_bytes = 4;
    if (rsdp[15] >= 2 && le32(rsdp + 20) >= RSDP_BYTES && checksum_ok(rsdp, RSDP_BYTES) && le64(rsdp + 24)) {
        root = le64(rsdp + 24);
        entry_bytes = 8;
    }
    const WitU8 *sdt = root ? table(boot, root, &root_length) : 0;
    if (!sdt || !signature(sdt, entry_bytes == 8 ? "XSDT" : "RSDT", 4)) {
        return 0;
    }
    for (WitU32 offset = HEADER_BYTES; offset + entry_bytes <= root_length; offset += entry_bytes) {
        const WitU64 address = entry_bytes == 8 ? le64(sdt + offset) : le32(sdt + offset);
        WitU32 length = 0;
        if (!address) {
            continue;
        }
        const WitU8 *header = map(boot, address, HEADER_BYTES);
        if (!signature(header, "APIC", 4)) {
            continue;
        }
        const WitU8 *madt = table(boot, address, &length);
        if (madt && length >= MADT_ENTRIES) {
            return read_madt(madt, length, out, capacity);
        }
    }
    return 0;
}
