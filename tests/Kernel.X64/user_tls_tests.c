#include "x64.h"
#include "user.h"
#include "witos/platform.h"
#include "protocol.h"
#include "tls_image.h"
#include "self_test.h"

static WitUserProcess process;
static WitPeImage plan;
static WitPageAllocator limited;
static WitU8 changed[16384];

static void require(int condition, const char *message);

/* THREAD_CREATE from the kernel side: the request sits at the bottom page of the main thread's stack. */
static WitU64 start_thread(WitU64 entry, WitU64 argument, WitU64 *result)
{
    const WitU64 address = process.Threads[0].StackBottom;
    WitThreadCreateRequest request = {WIT_THREAD_CREATE_VERSION, sizeof(request), entry, argument, 0, 0, 0, 0};
    require(wit_user_copy_to(&process.Space, address, (const WitU8 *)&request, sizeof(request)),
        "Thread request setup failed");
    return wit_user_thread_create(&process, address, sizeof(request), result);
}

static void require(int condition, const char *message)
{
    if (!condition) {
        wit_panic(message);
    }
}

static WitU32 u32(const WitU8 *p)
{
    return p[0] | ((WitU32)p[1] << 8) | ((WitU32)p[2] << 16) | ((WitU32)p[3] << 24);
}

static void put32(WitU32 at, WitU32 value)
{
    for (WitU32 i = 0; i < 4; ++i) {
        changed[at + i] = (WitU8)(value >> (8 * i));
    }
}

static void put64(WitU32 at, WitU64 value)
{
    for (WitU32 i = 0; i < 8; ++i) {
        changed[at + i] = (WitU8)(value >> (8 * i));
    }
}

static void reset(void)
{
    for (WitU32 i = 0; i < sizeof(wit_tls_image); ++i) {
        changed[i] = wit_tls_image[i];
    }
}

static void reject(WitPageAllocator *pages, WitPeStatus expected)
{
    const WitU64 before = wit_pages_free_count(pages);
    const WitPeStatus actual =
        wit_user_create_pe(&process, pages, 0, changed, sizeof(wit_tls_image), WIT_USER_IMAGE_BASE);
    if (actual != expected) {
        wit_console_write("TLS reject actual/expected: ");
        wit_console_write_u64(actual);
        wit_console_write("/");
        wit_console_write_u64(expected);
        wit_console_write("\n");
        wit_panic("TLS validation status mismatch");
    }
    require(!process.Space.Root && !process.Handles.Count && wit_pages_free_count(pages) == before,
        "Invalid TLS image allocated resources");
}

static void malformed(WitPageAllocator *pages)
{
    WitU32 raw = 0, reloc = 0;
    const WitU32 optional = u32(wit_tls_image + 60) + 24;
    require(wit_pe_file_range(&plan, plan.TlsRva, 40, &raw) &&
            wit_pe_file_range(&plan, plan.RelocRva, plan.RelocSize, &reloc),
        "TLS metadata file range missing");
    reset();
    put32(optional + 116 + 9 * 8, 39);
    reject(pages, WitPeInvalidImage);
    reset();
    put64(raw, plan.PreferredBase + plan.ImageSize + 1);
    reject(pages, WitPeInvalidImage);
    reset();
    put64(raw + 8, plan.PreferredBase + plan.TlsTemplateRva - 1);
    reject(pages, WitPeInvalidImage);
    reset();
    put64(raw + 16, plan.PreferredBase + plan.EntryRva);
    reject(pages, WitPeInvalidImage);
    reset();
    put64(raw + 16, plan.PreferredBase + plan.TlsIndexRva + 1);
    reject(pages, WitPeInvalidImage);
    reset();
    put64(raw + 16, plan.PreferredBase + plan.TlsTemplateRva);
    reject(pages, WitPeInvalidImage);
    reset();
    put32(raw + 32, WIT_PE_TLS_MAX_BYTES);
    reject(pages, WitPeTooLarge);
    reset();
    put32(raw + 36, 1);
    reject(pages, WitPeUnsupportedImage);
    reset();
    put32(raw + 36, 0x00A00000);
    reject(pages, WitPeUnsupportedImage);
    reset();
    put64(raw + 24, plan.PreferredBase + plan.TlsRva);
    reject(pages, WitPeUnsupportedImage);
    reset();
    put64(raw + 24, ~0ULL);
    reject(pages, WitPeInvalidImage);
    reset();
    put32(optional + 112 + 9 * 8, plan.TlsRva + 1);
    reject(pages, WitPeInvalidImage);
    for (WitU32 i = 0; i < plan.SectionCount; ++i) {
        const WitPeSection *section = &plan.Sections[i];
        if (plan.TlsRva < section->Rva || plan.TlsRva - section->Rva >= section->VirtualSize) {
            continue;
        }
        reset();
        {
            const WitU32 flags = optional + 240 + i * 40 + 36;
            put32(flags, u32(changed + flags) | 0x80000000U);
        }
        reject(pages, WitPeInvalidImage);
    }
    // Remove one mandatory VA relocation without corrupting the table shape.
    for (WitU32 offset = 0; offset < plan.RelocSize;) {
        const WitU32 page = u32(wit_tls_image + reloc + offset);
        const WitU32 size = u32(wit_tls_image + reloc + offset + 4);
        for (WitU32 i = 8; i < size; i += 2) {
            const WitU32 at = reloc + offset + i;
            const WitU32 entry = wit_tls_image[at] | ((WitU32)wit_tls_image[at + 1] << 8);
            if ((entry >> 12) != 10 || page + (entry & 4095) != plan.TlsRva) {
                continue;
            }
            reset();
            changed[at] = changed[at + 1] = 0;
            reject(pages, WitPeInvalidImage);
            // A fixup cannot rewrite size/flags fields of validated TLS metadata.
            reset();
            {
                const WitU32 bad = 0xA000 | ((plan.TlsRva + 32) & 4095);
                changed[at] = (WitU8)bad;
                changed[at + 1] = (WitU8)(bad >> 8);
            }
            reject(pages, WitPeInvalidImage);
            wit_console_write("[TEST-PASS] User.CompilerTlsValidation\n");
            return;
        }
        offset += size;
    }
    wit_panic("TLS VA relocation not found");
}

static void run(WitPageAllocator *pages, WitU64 base, WitU64 mode)
{
    const WitU64 before = wit_pages_free_count(pages);
    WitUserTestConfig *config;
    WitU32 owned;
    WitU64 gs;
    require(wit_user_create_pe(&process, pages, 0, wit_tls_image, sizeof(wit_tls_image), base) == WitPeOk,
        "Compiler TLS image creation failed");
    config = (WitUserTestConfig *)wit_user_space_physical(&process.Space, WIT_USER_INFO, 0, 0);
    config->Mode = mode;
    config->KernelProbe = base + plan.TlsTemplateRva;
    config->InstanceId = plan.TlsInitialized;
    config->ReadOnlyHandle = gs = process.Threads[0].CompilerTls;
    config->ForeignHandle = (WitU64)wit_tls_image & ~4095ULL;
    owned = process.Space.OwnedCount;
    // MSVC merges the TLS template into read-only .rdata. Corrupt its mapped
    // supervisor alias after capture; child initialization must use the seed.
    if (!mode) {
        for (WitU32 i = 0; i < plan.TlsInitialized; ++i) {
            *(WitU8 *)wit_user_space_physical(&process.Space, base + plan.TlsTemplateRva + i, 0, 0) = 0xEE;
        }
    }
    wit_user_run(&process);
    if (!mode) {
        if (process.State != WitUserExited || process.ExitCode != WIT_TEST_EXIT_CODE) {
            wit_console_write("TLS state/code: ");
            wit_console_write_u64(process.State);
            wit_console_write("/");
            wit_console_write_u64(process.ExitCode);
            wit_console_write("\n");
            wit_panic("Compiled TLS execution failed");
        }
        require(process.ThreadCreates == 4 &&
                process.ThreadReaps == 3 &&
                process.ThreadTimerSwitches > 0 &&
                process.IdleHalts > 0 &&
                process.Space.OwnedCount == owned,
            "Compiler TLS preemption/idle/thread reclamation failed");
        require(!wit_user_space_physical(&process.Space, gs + WIT_USER_THREAD_STRIDE, 0, 0),
            "Joined compiler TLS page remains mapped");
    } else {
        require(wit_test_faulted(&process) &&
                process.FaultVector == 14 &&
                process.FaultError == (mode == 1 ? 21U : 5U) &&
                process.FaultAddress == (mode == 1 ? gs + 0x100 : config->ForeignHandle) &&
                process.FaultState.Cs == WIT_USER_CS &&
                process.FaultState.Ss == WIT_USER_SS,
            "Compiler TLS isolation fault mismatch");
    }
    require(!process.Handles.Count && !process.Events.Count, "Compiler TLS handles leaked");
    wit_user_destroy(&process);
    require(wit_pages_free_count(pages) == before, "Compiler TLS teardown leaked pages");
}

static void rollback(WitPageAllocator *pages)
{
    WitU64 borrowed[64];
    WitMemoryRegion regions[64];
    WitU32 required;
    const WitU64 before = wit_pages_free_count(pages);
    require(
        wit_user_create_pe(&process, pages, 0, wit_tls_image, sizeof(wit_tls_image), WIT_USER_IMAGE_BASE) == WitPeOk,
        "TLS rollback fixture creation failed");
    required = process.Space.OwnedCount;
    wit_user_destroy(&process);
    require(required < 64, "TLS OOM fixture too large");
    for (WitU32 i = 0; i < required; ++i) {
        require(wit_page_allocate(pages, &borrowed[i]), "Cannot borrow TLS OOM page");
        regions[i].Base = borrowed[i];
        regions[i].Length = 4096;
        regions[i].Kind = WIT_MEMORY_USABLE;
        regions[i].Reserved = 0;
    }
    for (WitU32 count = 1; count <= required; ++count) {
        require(wit_pages_initialize(&limited, regions, count), "TLS OOM allocator init failed");
        require(wit_user_create_pe(&process, &limited, 0, wit_tls_image, sizeof(wit_tls_image), WIT_USER_IMAGE_BASE) ==
                (count == required ? WitPeOk : WitPeNoMemory),
            "TLS load OOM status mismatch");
        if (count == required) {
            wit_user_destroy(&process);
        }
        require(!process.Space.Root && !process.Handles.Count && wit_pages_free_count(&limited) == count,
            "TLS image allocation rollback leaked");
    }
    for (WitU32 i = 0; i < required; ++i) {
        require(wit_page_free(pages, borrowed[i]), "Cannot return TLS OOM page");
    }
    // Fail each stack/raw-TLS/compiler-TLS allocation of a child, including the last.
    require(
        wit_user_create_pe(&process, pages, 0, wit_tls_image, sizeof(wit_tls_image), WIT_USER_IMAGE_BASE) == WitPeOk,
        "TLS child OOM fixture creation failed");
    for (WitU32 remaining = 0; remaining < (WIT_USER_STACK_TOP - WIT_USER_STACK_BOTTOM) / 4096 + 2; ++remaining) {
        WitU64 arena, result = 99;
        WitU32 count = 0;
        WitU64 free_before;
        require(wit_user_memory_reserve(&process.Space, 128 * 4096, 4096, 0, &arena) == WIT_STATUS_OK,
            "TLS OOM reserve failed");
        while (wit_user_memory_commit(&process.Space, arena + count * 4096ULL, 4096, 3) == WIT_STATUS_OK) {
            ++count;
        }
        require(count > remaining, "TLS OOM setup failed");
        if (remaining) {
            require(wit_user_memory_decommit(
                        &process.Space, arena + (count - remaining) * 4096ULL, remaining * 4096ULL) == WIT_STATUS_OK,
                "TLS OOM decommit failed");
        }
        free_before = wit_pages_free_count(pages);
        require(start_thread(process.ImageEntry, WIT_USER_INFO, &result) == WIT_STATUS_NO_MEMORY &&
                !result &&
                process.Handles.Count == 2 &&
                process.Threads[1].State == WitThreadEmpty &&
                wit_pages_free_count(pages) == free_before,
            "Compiler TLS child rollback leaked");
        require(!wit_user_space_physical(&process.Space, WIT_USER_TLS + WIT_USER_THREAD_STRIDE, 0, 0) &&
                !wit_user_space_physical(&process.Space, WIT_USER_TLS + WIT_USER_THREAD_STRIDE + 4096, 0, 0),
            "TLS rollback retained mapping");
        require(
            wit_user_memory_release(&process.Space, arena, 0) == WIT_STATUS_OK && process.Space.OwnedCount == required,
            "TLS OOM cleanup failed");
    }
    wit_user_destroy(&process);
    require(wit_pages_free_count(pages) == before, "TLS OOM test leaked pages");
    wit_console_write("[TEST-PASS] User.CompilerTlsRollback\n");
}

void wit_user_tls_self_test(WitPageAllocator *pages)
{
    require(sizeof(wit_tls_image) <= sizeof(changed) &&
            wit_pe_validate(wit_tls_image, sizeof(wit_tls_image), &plan) == WitPeOk &&
            plan.TlsSize,
        "Compiler TLS fixture invalid");
    malformed(pages);
    rollback(pages);
    run(pages, WIT_USER_IMAGE_BASE, 0);
    run(pages, WIT_USER_IMAGE_ALTERNATE, 0);
    wit_console_write("[TEST-PASS] User.CompilerTlsThreads\n");
    run(pages, WIT_USER_IMAGE_BASE, 1);
    run(pages, WIT_USER_IMAGE_BASE, 2);
    run(pages, WIT_USER_IMAGE_BASE, 0);
    wit_console_write("[TEST-PASS] User.CompilerTlsIsolation\n");
}
