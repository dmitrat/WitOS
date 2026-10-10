#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "PackageReader.h"
static unsigned cases;
static unsigned char *allocation;
static const size_t capacity = 65536;

static unsigned get32(const unsigned char *p)
{
    return p[0] | ((unsigned)p[1] << 8) | ((unsigned)p[2] << 16) | ((unsigned)p[3] << 24);
}

static void put32(unsigned char *p, unsigned value)
{
    for (unsigned i = 0; i < 4; ++i) {
        p[i] = (unsigned char)(value >> (i * 8));
    }
}

static int check(const unsigned char *source, size_t size, int expected, const char *label)
{
    DWORD old;
    if (size > capacity || !VirtualProtect(allocation + 4096, capacity, PAGE_READWRITE, &old)) {
        return 0;
    }
    unsigned char *data = allocation + 4096 + capacity - size;
    if (size) {
        memcpy(data, source, size);
    }
    if (!VirtualProtect(allocation + 4096, capacity, PAGE_READONLY, &old)) {
        return 0;
    }
    WitPackage output;
    memset(&output, 0xA5, sizeof(output));
    const WitPackage before = output;
    const WitPackageStatus status = wit_package_open(data, size, &output);
    ++cases;
    if ((expected >= 0 && (int)status != expected) ||
        (status != WitPackageOk && status != WitPackageInvalid) ||
        (status != WitPackageOk && memcmp(&output, &before, sizeof(output)))) {
        printf("FAIL %s size=%zu expected=%d actual=%d case=%u\n", label, size, expected, status, cases);
        return 0;
    }
    if (status == WitPackageOk) {
        for (unsigned i = 0; i < output.Count; ++i) {
            WitPackageFile file, found;
            if (wit_package_get(&output, i, &file) != WitPackageOk ||
                wit_package_find(&output, file.Name, file.NameLength, &found) != WitPackageOk ||
                found.Offset != file.Offset ||
                found.Length != file.Length) {
                return 0;
            }
        }
        WitPackageFile missing;
        memset(&missing, 0xA5, sizeof(missing));
        const WitPackageFile untouched = missing;
        if (wit_package_get(&output, output.Count, &missing) != WitPackageMissing ||
            memcmp(&missing, &untouched, sizeof(missing)) ||
            wit_package_find(&output, (const WitU8 *)"absent/file", 11, &missing) != WitPackageMissing ||
            memcmp(&missing, &untouched, sizeof(missing))) {
            return 0;
        }
        ++output.Count;
        if (wit_package_get(&output, 0, &missing) != WitPackageInvalid ||
            memcmp(&missing, &untouched, sizeof(missing))) {
            return 0;
        }
    }
    return 1;
}

static unsigned char *load(const char *path, size_t *size)
{
    FILE *file = 0;
    if (fopen_s(&file, path, "rb") || !file) {
        return 0;
    }
    if (fseek(file, 0, SEEK_END)) {
        fclose(file);
        return 0;
    }
    const long length = ftell(file);
    if (length < 0 || length > 65536 || fseek(file, 0, SEEK_SET)) {
        fclose(file);
        return 0;
    }
    unsigned char *data = (unsigned char *)malloc(length ? length : 1);
    if (!data) {
        fclose(file);
        return 0;
    }
    if (fread(data, 1, (size_t)length, file) != (size_t)length) {
        free(data);
        fclose(file);
        return 0;
    }
    fclose(file);
    *size = (size_t)length;
    return data;
}

int boot_package_firmware_test(const char *);

int main(int argc, char **argv)
{
    if (argc != 5) {
        return 1;
    }
    if (!boot_package_firmware_test(argv[3])) {
        return 13;
    }
    size_t size = 0, smallSize = 0;
    unsigned char *data = load(argv[1], &size);
    unsigned char *small = load(argv[2], &smallSize);
    if (!data || !small || smallSize != 128) {
        return 2;
    }
    allocation = (unsigned char *)VirtualAlloc(0, capacity + 8192, MEM_RESERVE, PAGE_NOACCESS);
    if (!allocation || !VirtualAlloc(allocation + 4096, capacity, MEM_COMMIT, PAGE_READWRITE)) {
        return 3;
    }
    if (!check(data, size, WitPackageOk, "writer package") || !check(small, smallSize, WitPackageOk, "two entries")) {
        return 4;
    }
    for (size_t length = 0; length < size; ++length) {
        if (!check(data, length, WitPackageInvalid, "every truncation")) {
            return 5;
        }
    }
    // Repair the declared total as well: these cases reach the index/name/data
    // bounds rather than stopping at the header's original total-length check.
    for (size_t length = 32; length < size; ++length) {
        put32(data + 24, (unsigned)length);
        if (!check(data, length, WitPackageInvalid, "declared-size truncation")) {
            return 14;
        }
    }
    put32(data + 24, (unsigned)size);
    size_t hierarchySize = 0;
    unsigned char *hierarchy = load(argv[4], &hierarchySize);
    if (!hierarchy || !check(hierarchy, hierarchySize, WitPackageOk, "hierarchy control")) {
        return 15;
    }
    hierarchy[get32(hierarchy + 32)] = 'b';
    if (!check(hierarchy, hierarchySize, WitPackageInvalid, "non-adjacent file/directory collision")) {
        return 16;
    }
    free(hierarchy);
    unsigned char changed[128];
    const unsigned positions[] = {0, 8, 12, 16, 20, 24, 32, 36, 40, 44, 48, 52, 56, 60, 64, 68, 72, 76, 80, 84, 88, 92};
    for (unsigned i = 0; i < sizeof(positions) / sizeof(positions[0]); ++i) {
        memcpy(changed, small, 128);
        put32(changed + positions[i], 0xFFFFFFFFU);
        if (!check(changed, 128, WitPackageInvalid, "overflow/header/reserved")) {
            return 6;
        }
    }
    const unsigned firstName = get32(small + 32), secondName = get32(small + 64);
    for (unsigned mode = 0; mode < 12; ++mode) {
        memcpy(changed, small, 128);
        if (mode == 0) {
            memcpy(changed + secondName, changed + firstName, 5); // duplicate
        }
        if (mode == 1) {
            changed[firstName] = 'z'; // unsorted
        }
        if (mode == 2) {
            changed[firstName] = 0;
        }
        if (mode == 3) {
            changed[firstName] = '/';
        }
        if (mode == 4) {
            changed[firstName] = '.';
            changed[firstName + 1] = '.';
            changed[firstName + 2] = '/';
        }
        if (mode == 5) {
            changed[firstName] = '\\';
        }
        if (mode == 6) {
            changed[firstName] = 0xC0;
            changed[firstName + 1] = 0xAF;
        } // overlong slash
        if (mode == 7) {
            changed[firstName] = 0xED;
            changed[firstName + 1] = 0xA0;
            changed[firstName + 2] = 0x80;
        } // surrogate
        if (mode == 8) {
            changed[firstName] = 0xF4;
            changed[firstName + 1] = 0x90;
            changed[firstName + 2] = 0x80;
            changed[firstName + 3] = 0x80;
        }
        if (mode == 9) {
            changed[111] = 1; // index/name padding
        }
        if (mode == 10) {
            changed[127] = 1; // payload padding
        }
        if (mode == 11) {
            put32(changed + 72, get32(changed + 40)); // overlapping payload
        }
        if (!check(changed, 128, WitPackageInvalid, "names/padding/overlap")) {
            return 7;
        }
    }
    for (unsigned offset = 0; offset < 128; ++offset) {
        for (unsigned bit = 0; bit < 8; ++bit) {
            memcpy(changed, small, 128);
            changed[offset] ^= (unsigned char)(1U << bit);
            if (!check(changed, 128, -1, "bounded bit mutations")) {
                return 8;
            }
        }
    }
    WitPackage package;
    if (wit_package_open(data, size, &package) != WitPackageOk) {
        return 9;
    }
    WitPackageNode node;
    WitU32 next = 0, position = 0;
    const char *names[] = {"app", "empty", "resources"};
    const unsigned kinds[] = {WIT_PACKAGE_DIRECTORY, WIT_PACKAGE_FILE, WIT_PACKAGE_DIRECTORY};
    for (unsigned i = 0; i < 3; ++i) {
        if (wit_package_list(&package, 0, 0, position, &node, &next) != WitPackageOk ||
            next <= position ||
            next > package.Count ||
            node.Kind != kinds[i] ||
            node.NameLength != strlen(names[i]) ||
            memcmp(node.Name, names[i], node.NameLength)) {
            return 17;
        }
        position = next;
    }
    memset(&node, 0xA5, sizeof(node));
    unsigned char unchanged[sizeof(node)];
    memcpy(unchanged, &node, sizeof(node));
    next = 123;
    if (wit_package_list(&package, 0, 0, position, &node, &next) != WitPackageEnd ||
        next != 123 ||
        memcmp(&node, unchanged, sizeof(node))) {
        return 18;
    }
    if (wit_package_list(&package, (const WitU8 *)"empty", 5, 0, &node, &next) != WitPackageNotDirectory ||
        next != 123 ||
        memcmp(&node, unchanged, sizeof(node))) {
        return 19;
    }
    if (wit_package_list(&package, 0, 0, package.Count + 1, &node, &next) != WitPackageInvalid ||
        next != 123 ||
        memcmp(&node, unchanged, sizeof(node))) {
        return 20;
    }
    if (wit_package_stat(&package, (const WitU8 *)"absent", 6, &node) != WitPackageMissing ||
        memcmp(&node, unchanged, sizeof(node))) {
        return 21;
    }
    if (wit_package_stat(&package, (const WitU8 *)"../app", 6, &node) != WitPackageInvalid ||
        memcmp(&node, unchanged, sizeof(node))) {
        return 22;
    }
    if (wit_package_stat(&package, 0, 0, &node) != WitPackageOk ||
        node.Kind != WIT_PACKAGE_DIRECTORY ||
        node.NameLength) {
        return 23;
    }
    if (wit_package_stat(&package, (const WitU8 *)"app", 3, &node) != WitPackageOk ||
        node.Kind != WIT_PACKAGE_DIRECTORY) {
        return 24;
    }
    if (wit_package_stat(&package, (const WitU8 *)"app/Probe.dll", 13, &node) != WitPackageOk ||
        node.Kind != WIT_PACKAGE_FILE ||
        node.Length != 4099) {
        return 25;
    }
    if (wit_package_list(&package, (const WitU8 *)"resources", 9, 0, &node, &next) != WitPackageOk ||
        node.Kind != WIT_PACKAGE_FILE ||
        node.NameLength != 6 ||
        node.Name[0] != 0xCE ||
        node.Name[1] != 0xBB ||
        memcmp(node.Name + 2, ".txt", 4)) {
        return 26;
    }
    puts("PASS: native package stat/list root, implicit directories, Unicode, cursor and transactional failures");
    for (unsigned i = 0; i < package.Count; ++i) {
        WitPackageFile file;
        char path[40];
        FILE *output = 0;
        if (wit_package_get(&package, i, &file) != WitPackageOk) {
            return 10;
        }
        sprintf_s(path, sizeof(path), "package-roundtrip-%u.bin", i);
        if (fopen_s(&output, path, "wb") || !output) {
            return 11;
        }
        if (fwrite(data + file.Offset, 1, (size_t)file.Length, output) != (size_t)file.Length || fclose(output)) {
            return 12;
        }
    }
    VirtualFree(allocation, 0, MEM_RELEASE);
    free(data);
    free(small);
    printf("PASS: %u readonly guard-boundary package cases, native lookup and exact payload extraction\n", cases);
    return 0;
}
