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
 * shm_unlink takes the name. A descriptor reads and writes a file as Linux's tmpfs does (R7.2a): through the library's
 * own read and write view of one chunk at a time, a chunk nobody mapped or wrote reading as zeros, and a write past the
 * end growing the file; /dev/shm is the one directory in which a process may create files. Files shared with another
 * process and copy-on-write mappings of one are not there. Every function runs under the table lock of
 * __wit_syscall. */

/* The files at once: shm_open's and memfd_create's, and since R4.2 every volume file mapped shared, which CoreCLR
 * keeps for every assembly it loads from a volume and MemoryMappedFile for every read-only view (N4.1: Roslyn's
 * analyzer loading tests kept more than 64 and got ENFILE); as many as the descriptor table holds. */
#define FILES 1024U
#define NAME_BYTES 255U /* NAME_MAX */
#define CHUNK ((WitU64)WIT_MEMORY_OBJECT_PAGES * 4096ULL)
#define SIZE_LIMIT (16ULL << 30) /* a file's chunk table stays within 512 KiB */

/* The record is packed to 320 bytes: the tables are the root task's writable segment, which the flat format bounds at
 * a MiB (WIT_FLAT_MAX_SEGMENT_BYTES), and R9's two fields put it 584 bytes over. */
typedef struct SharedFile {
    WitU32 References; /* descriptors and mappings; the name keeps a file alive too */
    WitU32
        Remote; /* one more than the remote record of a volume's file whose chunks the namespace service shares (R4.2) */
    WitU16 Mode; /* the permission bits, 0600 until chmod changes them (N4.1), as a file of tmpfs keeps its own */
    WitU8 Live;
    WitU8 NameLength; /* zero for an anonymous file (memfd_create) or one shm_unlink took the name of */
    WitU8 Writable; /* a remote file's chunks are asked for writable */
    WitU8
        Fixed; /* a file of the library's own, sized once at its creation, whose chunk objects are as large as the file
                    needs (R9: a page or two of an image's code); it has no descriptor and never grows */
    unsigned char Name[NAME_BYTES];
    WitU64 Size;
    WitU64 *Chunks; /* the chunk objects' handles, zero for a chunk nobody mapped yet */
    WitU64 ChunkCapacity; /* entries of Chunks, a mapping of whole pages */
    WitU64 View; /* the library's read and write view of chunk ViewChunk (R7.2a), zero when none */
    WitU64 ViewChunk;
    WitU64
        OneChunk; /* a fixed file's chunk table when it has one chunk, so that a copy of a page costs no table page */
} SharedFile;

/* The bytes of a chunk's object: a chunk of a fixed file holds what the file needs of it, rounded to pages. */
static WitU64 chunk_bytes(const SharedFile *f, WitU64 chunk)
{
    const WitU64 start = chunk * CHUNK;
    if (f->Fixed && start < f->Size && f->Size - start < CHUNK) {
        return (f->Size - start + 4095) & ~4095ULL;
    }
    return CHUNK;
}

static void drop_table(SharedFile *f)
{
    if (f->Chunks && f->Chunks != &f->OneChunk) {
        __wit_munmap((long)f->Chunks, (long)(f->ChunkCapacity * sizeof(WitU64)));
    }
    f->Chunks = 0;
    f->ChunkCapacity = 0;
}

static SharedFile files[FILES];

static void close_handle(WitU64 handle)
{
    WitU64 result = 0;
    wit_syscall(WIT_CALL_HANDLE_CLOSE, handle, 0, 0, &result);
}

static void drop_view(SharedFile *f)
{
    if (f->View) {
        WitU64 result = 0;
        wit_syscall(WIT_CALL_MEMORY_RELEASE, f->View, 0, 0, &result);
        f->View = 0;
    }
}

/* A file ends with its last reference once nothing names it: its chunk objects end with their last mapping. */
static void settle(SharedFile *f)
{
    if (f->References || f->NameLength || !f->Live) {
        return;
    }
    drop_view(f);
    for (WitU64 i = 0; i < f->ChunkCapacity; ++i) {
        if (f->Chunks[i]) {
            close_handle(f->Chunks[i]);
        }
    }
    drop_table(f);
    const WitU32 remote = f->Remote;
    memset(f, 0, sizeof(*f));
    if (remote) {
        __wit_remote_release(
            remote - 1); /* the last reference closes the service's handle, which writes the file back */
    }
}

static long create(WitU32 *index)
{
    for (WitU32 i = 0; i < FILES; ++i) {
        if (!files[i].Live) {
            memset(&files[i], 0, sizeof(files[i]));
            files[i].Live = 1;
            files[i].References = 1;
            files[i].Mode = 0600;
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
        files[*index].NameLength = (WitU8)length;
    }
    return status;
}

/* The file a name under /dev/shm gives, for stat and access (R7.2a). */
long __wit_shared_find(const unsigned char *name, WitU32 length, WitU32 *index)
{
    for (WitU32 i = 0; i < FILES; ++i) {
        const SharedFile *f = &files[i];
        if (f->Live && f->NameLength && f->NameLength == length && !memcmp(f->Name, name, length)) {
            *index = i;
            return 0;
        }
    }
    return length > NAME_BYTES ? -ENAMETOOLONG : -ENOENT;
}

/* memfd_create's file: no name, the descriptor's reference. */
long __wit_shared_anonymous(WitU32 *index)
{
    return create(index);
}

/* A file of the library's own for a copy of a known size (R9: the page or two of an executable package mapping that a
 * write reaches), whose chunk objects take only the pages the size needs, where a chunk is otherwise 64 pages: a copy
 * of one page costs one page. The caller's reference; the file never grows. */
long __wit_shared_anonymous_fixed(WitU64 size, WitU32 *index)
{
    long status = create(index);
    if (status < 0) {
        return status;
    }
    SharedFile *f = &files[*index];
    if (size <= CHUNK) {
        f->Chunks = &f->OneChunk;
        f->ChunkCapacity = 1;
        f->Size = size;
    } else if ((status = __wit_shared_truncate(*index, size)) < 0) {
        __wit_shared_release(*index);
        return status;
    }
    f->Fixed = 1;
    return 0;
}

/* A volume's file mapped shared (R4.2): a file of the process whose chunks are the memory objects the namespace service
 * shares for that file, asked for as a mapping first needs each, so that every shared mapping of the file in any process
 * shows the same pages; one per remote record and writability, which holds a reference of the record while it lives.
 * The caller's reference. */
long __wit_shared_remote(WitU32 remote, WitU64 size, int writable, WitU32 *index)
{
    for (WitU32 i = 0; i < FILES; ++i) {
        if (files[i].Live && files[i].Remote == remote + 1 && files[i].Writable == writable) {
            ++files[i].References;
            *index = i;
            return 0;
        }
    }
    long status = create(index);
    if (status < 0) {
        return status;
    }
    if ((status = __wit_shared_truncate(*index, size)) < 0) {
        __wit_shared_release(*index);
        return status;
    }
    files[*index].Remote = remote + 1;
    files[*index].Writable = (WitU8)(writable != 0);
    __wit_remote_reference(remote);
    return 0;
}

/* One more than the remote record whose chunks a file shows, zero for the process's own (R4.2). */
WitU32 __wit_shared_remote_of(WitU32 index)
{
    return files[index].Remote;
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

/* The permission bits a stat reports (N4.1). */
WitU32 __wit_shared_mode(WitU32 index)
{
    return files[index].Mode;
}

/* chmod and fchmod of a shared memory file (N4.1): the bits are kept, as tmpfs keeps them. */
long __wit_shared_chmod(WitU32 index, long mode)
{
    files[index].Mode = (WitU16)(mode & 07777);
    return 0;
}

/* ftruncate: a larger file gets chunks nobody mapped yet; a smaller one gives up the chunks wholly past its end, whose
 * objects live on while a mapping shows them. */
long __wit_shared_truncate(WitU32 index, WitU64 size)
{
    SharedFile *f = &files[index];
    if (size > SIZE_LIMIT) {
        return -EFBIG;
    }
    if (f->Fixed) {
        return -EPERM; /* its chunk objects are sized to it; nothing reaches it through a descriptor */
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
    if (f->View && f->ViewChunk >= needed) {
        drop_view(f);
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
    if (!*chunk && f->Remote) {
        /* A volume's file: the service's object for the chunk (R4.2). */
        WitU64 object = 0;
        const long status = __wit_remote_map(f->Remote - 1, offset - offset % CHUNK, f->Writable, &object);
        if (status < 0) {
            return status;
        }
        *chunk = object;
    }
    if (!*chunk) {
        WitU64 object = 0;
        const WitU64 status = wit_syscall(WIT_CALL_MEMORY_OBJECT_CREATE, chunk_bytes(f, offset / CHUNK), 0, 0, &object);
        if (status != WIT_STATUS_OK) {
            return status == WIT_STATUS_NO_MEMORY ? -ENOMEM : __wit_errno(status);
        }
        *chunk = object;
    }
    *handle = *chunk;
    return 0;
}

/* The library's view of a chunk that has an object, kept until another chunk of the file is read or written. */
static long view(SharedFile *f, WitU64 chunk, unsigned char **address)
{
    if (!f->View || f->ViewChunk != chunk) {
        WitMemoryMapRequest request;
        WitU64 result = 0;
        drop_view(f);
        request.Version = WIT_MEMORY_MAP_VERSION;
        request.Size = sizeof(request);
        request.Object = f->Chunks[chunk];
        request.Offset = 0;
        request.Bytes = chunk_bytes(f, chunk);
        request.Address = 0;
        request.Protection = WIT_MEMORY_READ | WIT_MEMORY_WRITE;
        request.Flags = 0;
        request.Target = WIT_PROCESS_SELF;
        const WitU64 status = wit_syscall(WIT_CALL_MEMORY_OBJECT_MAP, (WitU64)&request, sizeof(request), 0, &result);
        if (status != WIT_STATUS_OK) {
            return status == WIT_STATUS_NO_MEMORY ? -ENOMEM : __wit_errno(status);
        }
        f->View = result;
        f->ViewChunk = chunk;
    }
    *address = (unsigned char *)f->View;
    return 0;
}

/* read and pread (R7.2a): the bytes from the offset up to the end, zeros where no chunk object exists yet. */
long __wit_shared_read(WitU32 index, WitU64 offset, unsigned char *buffer, WitU64 bytes)
{
    SharedFile *f = &files[index];
    WitU64 done = 0;
    if (offset >= f->Size) {
        return 0;
    }
    if (bytes > f->Size - offset) {
        bytes = f->Size - offset;
    }
    while (done < bytes) {
        const WitU64 at = offset + done, chunk = at / CHUNK, within = at % CHUNK;
        const WitU64 step = CHUNK - within < bytes - done ? CHUNK - within : bytes - done;
        unsigned char *address;
        if (!f->Chunks[chunk]) {
            memset(buffer + done, 0, step);
        } else {
            const long status = view(f, chunk, &address);
            if (status < 0) {
                return done ? (long)done : status;
            }
            memcpy(buffer + done, address + within, step);
        }
        done += step;
    }
    return (long)done;
}

/* write and pwrite (R7.2a): a write past the end grows the file first; a chunk gets its object when first written. A
 * write that fails part way leaves the file the size of what it wrote. */
long __wit_shared_write(WitU32 index, WitU64 offset, const unsigned char *buffer, WitU64 bytes)
{
    SharedFile *f = &files[index];
    const WitU64 old_size = f->Size;
    WitU64 done = 0;
    long status = 0;
    if (offset > SIZE_LIMIT || bytes > SIZE_LIMIT - offset) {
        return -EFBIG;
    }
    if (offset + bytes > f->Size && (status = __wit_shared_truncate(index, offset + bytes)) < 0) {
        return status;
    }
    while (done < bytes) {
        const WitU64 at = offset + done, chunk = at / CHUNK, within = at % CHUNK;
        const WitU64 step = CHUNK - within < bytes - done ? CHUNK - within : bytes - done;
        WitU64 handle = 0;
        unsigned char *address;
        if ((status = __wit_shared_chunk(index, at, &handle)) < 0 || (status = view(f, chunk, &address)) < 0) {
            break;
        }
        memcpy(address + within, buffer + done, step);
        done += step;
    }
    if (done < bytes) {
        const WitU64 kept = offset + done > old_size ? offset + done : old_size;
        __wit_shared_truncate(index, kept);
        return done ? (long)done : status;
    }
    return (long)done;
}
