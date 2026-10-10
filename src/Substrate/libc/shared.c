#define _GNU_SOURCE
#include "witos_libc.h"
#include "witos/memory_object.h"
#include <errno.h>
#include <fcntl.h>
#include <string.h>
#include <sys/mman.h>

/* Shared memory within the process (plan step R3.2b): POSIX's shm_open, which musl opens as /dev/shm/<name>, and
 * Linux's memfd_create give a descriptor of a shared memory file, ftruncate sizes it, and mmap with MAP_SHARED shows
 * its pages; every mapping of the same offsets shows the same pages, which is what CoreCLR's double mapper needs for
 * the JIT: a writable view and an executable view of one memory (RFC 0011 section 7.2), since WitOS never grants
 * WRITE|EXECUTE. A file is a sequence of chunks, each one anonymous memory object of WIT_MEMORY_OBJECT_PAGES pages
 * created zeroed the first time a mapping shows any of it, and a mapping is made of object mappings of at most one
 * chunk at consecutive addresses (memory.c). A file lives while a descriptor, a mapping or its name refers to it;
 * shm_unlink takes the name. Reads and writes of the descriptor are not there, nor files shared with another process,
 * nor copy-on-write mappings of one. Every function runs under the table lock of __wit_syscall. */

#define FILES 8U
#define NAME_BYTES 64U
#define CHUNK ((WitU64)WIT_MEMORY_OBJECT_PAGES * 4096ULL)
#define SIZE_LIMIT (16ULL << 30) /* a file's chunk table stays within 512 KiB */

typedef struct SharedFile {
    WitU32 References; /* descriptors and mappings; the name keeps a file alive too */
    WitU32 Live;
    WitU32 NameLength; /* zero for an anonymous file (memfd_create) or one shm_unlink took the name of */
    unsigned char Name[NAME_BYTES];
    WitU64 Size;
    WitU64 *Chunks; /* the chunk objects' handles, zero for a chunk nobody mapped yet */
    WitU64 ChunkCapacity; /* entries of Chunks, a mapping of whole pages */
} SharedFile;

static SharedFile files[FILES];

static void close_handle(WitU64 handle)
{
    WitU64 result = 0;
    wit_syscall(WIT_CALL_HANDLE_CLOSE, handle, 0, 0, &result);
}

/* A file ends with its last reference once nothing names it: its chunk objects end with their last mapping. */
static void settle(SharedFile *f)
{
    if (f->References || f->NameLength || !f->Live) {
        return;
    }
    for (WitU64 i = 0; i < f->ChunkCapacity; ++i) {
        if (f->Chunks[i]) {
            close_handle(f->Chunks[i]);
        }
    }
    if (f->Chunks) {
        __wit_munmap((long)f->Chunks, (long)(f->ChunkCapacity * sizeof(WitU64)));
    }
    memset(f, 0, sizeof(*f));
}

static long create(WitU32 *index)
{
    for (WitU32 i = 0; i < FILES; ++i) {
        if (!files[i].Live) {
            memset(&files[i], 0, sizeof(files[i]));
            files[i].Live = 1;
            files[i].References = 1;
            *index = i;
            return 0;
        }
    }
    return -ENFILE;
}

/* shm_open's file: the name after "dev/shm/", created by O_CREAT, refused by O_EXCL when it exists, emptied by
 * O_TRUNC; the descriptor's reference. */
long __wit_shared_open(const unsigned char *name, WitU32 length, long flags, WitU32 *index)
{
    if (!length || length > NAME_BYTES || memchr(name, '/', length)) {
        return length > NAME_BYTES ? -ENAMETOOLONG : -EINVAL;
    }
    for (WitU32 i = 0; i < FILES; ++i) {
        SharedFile *f = &files[i];
        if (f->Live && f->NameLength == length && !memcmp(f->Name, name, length)) {
            if ((flags & (O_CREAT | O_EXCL)) == (O_CREAT | O_EXCL)) {
                return -EEXIST;
            }
            if ((flags & O_TRUNC) && __wit_shared_truncate(i, 0) < 0) {
                return -EINVAL;
            }
            ++f->References;
            *index = i;
            return 0;
        }
    }
    if (!(flags & O_CREAT)) {
        return -ENOENT;
    }
    const long status = create(index);
    if (status == 0) {
        memcpy(files[*index].Name, name, length);
        files[*index].NameLength = length;
    }
    return status;
}

/* memfd_create's file: no name, the descriptor's reference. */
long __wit_shared_anonymous(WitU32 *index)
{
    return create(index);
}

/* shm_unlink: the name goes; the file lives on while a descriptor or a mapping refers to it. */
long __wit_shared_unlink(const unsigned char *name, WitU32 length)
{
    for (WitU32 i = 0; i < FILES; ++i) {
        SharedFile *f = &files[i];
        if (f->Live && f->NameLength && f->NameLength == length && !memcmp(f->Name, name, length)) {
            f->NameLength = 0;
            settle(f);
            return 0;
        }
    }
    return -ENOENT;
}

void __wit_shared_reference(WitU32 index)
{
    ++files[index].References;
}

void __wit_shared_release(WitU32 index)
{
    --files[index].References;
    settle(&files[index]);
}

WitU64 __wit_shared_size(WitU32 index)
{
    return files[index].Size;
}

/* ftruncate: a larger file gets chunks nobody mapped yet; a smaller one gives up the chunks wholly past its end, whose
 * objects live on while a mapping shows them. */
long __wit_shared_truncate(WitU32 index, WitU64 size)
{
    SharedFile *f = &files[index];
    if (size > SIZE_LIMIT) {
        return -EFBIG;
    }
    const WitU64 needed = (size + CHUNK - 1) / CHUNK;
    if (needed > f->ChunkCapacity) {
        const WitU64 bytes = (needed * sizeof(WitU64) + 4095) & ~4095ULL;
        const long table = __wit_mmap(0, (long)bytes, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
        if (table < 0) {
            return table;
        }
        if (f->Chunks) {
            memcpy((void *)table, f->Chunks, f->ChunkCapacity * sizeof(WitU64));
            __wit_munmap((long)f->Chunks, (long)(f->ChunkCapacity * sizeof(WitU64)));
        }
        f->Chunks = (WitU64 *)table;
        f->ChunkCapacity = bytes / sizeof(WitU64);
    }
    for (WitU64 i = needed; i < f->ChunkCapacity; ++i) {
        if (f->Chunks[i]) {
            close_handle(f->Chunks[i]);
            f->Chunks[i] = 0;
        }
    }
    f->Size = size;
    return 0;
}

/* The object of the chunk that holds the offset, created zeroed when no mapping showed it yet: -ENXIO past the end. */
long __wit_shared_chunk(WitU32 index, WitU64 offset, WitU64 *handle)
{
    SharedFile *f = &files[index];
    if (offset >= f->Size) {
        return -ENXIO;
    }
    WitU64 *chunk = &f->Chunks[offset / CHUNK];
    if (!*chunk) {
        WitU64 object = 0;
        const WitU64 status = wit_syscall(WIT_CALL_MEMORY_OBJECT_CREATE, CHUNK, 0, 0, &object);
        if (status != WIT_STATUS_OK) {
            return status == WIT_STATUS_NO_MEMORY ? -ENOMEM : __wit_errno(status);
        }
        *chunk = object;
    }
    *handle = *chunk;
    return 0;
}
