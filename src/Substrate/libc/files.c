#define _GNU_SOURCE
#include "witos_libc.h"
#include "witos/memory_object.h"
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <sys/uio.h>
#include "kstat.h"

/* Files over the read-only boot package (RFC 0011 section 9.3, plan step S1.2). The package is the root task's
 * initial capability: a memory object the kernel never parses for it, in the format of witos/package.h — a header,
 * a sorted table of entries (name offset and length, data offset and length), the names, then the data. The
 * library maps the header and the table once, read-only, and validates them, since the kernel never parses them
 * (K8.4a); a read
 * maps the window of the bytes it needs, copies and releases it, so a file of any size is readable through a
 * mapping of at most 64 pages. The namespace is the package's: "/" is its root, a name is a file, a prefix of a name
 * up to '/' is a directory, "/dev/null" and "/dev/zero" are the devices, and "/dev/urandom" and "/dev/random" read the
 * kernel's entropy (RANDOM; libc++'s random_device opens them, S4); nothing is writable (EROFS), and there are no
 * symbolic links. The current directory is the library's (S6.2): a relative path joins the current directory or a
 * directory descriptor and is normalized with it, so ".." climbs out of it; chdir and fchdir accept a directory of
 * the package, and a process starts in the directory its start message names. Descriptors 3 and above are the files;
 * 0–2 are the standard streams over the kernel log (syscall.c), which close takes away one by one.
 *
 * Duplicates (R2.3a): dup, dup2, dup3 and F_DUPFD give a descriptor the same open file description, as Linux does,
 * so the duplicates share one position and one set of status flags (O_NONBLOCK) and keep their own FD_CLOEXEC. A
 * duplicate of a standard stream is a stream descriptor that writes to the log as 1 and 2 do. Descriptors 0–2 are
 * slots of the same table: an empty slot is its standard stream until closed, a closed one is free for the lowest
 * descriptor an open or a dup takes, and dup2 may put a file, a pipe or another stream there, which then stands for
 * that descriptor within this process (a started process gets the log for each standard stream the caller has open
 * as the log, and no other). The ends of a pipe (pipe.c) are descriptors too, and so are shared memory files
 * (shared.c, R3.2b): /dev/shm/<name>, which open may create and unlink may remove, and memfd_create's. */

#define PACKAGE_HANDLE (__wit_process.Package)
#define PAGE 4096ULL
#define WINDOW_PAGES 64ULL /* one mapping of an object covers at most 64 pages */
#define WINDOW_BYTES (WINDOW_PAGES * PAGE)
#define HEADER_BYTES 32U
#define ENTRY_BYTES 32U
#define MAX_FILES 1024U /* WIT_PACKAGE_MAX_FILES */
#define FIRST_DESCRIPTOR 3
#define DESCRIPTORS 64
#define NAME_MAX_BYTES 1024U
#define DIRECTORY_INODE_BASE 0x10000ULL

typedef enum Kind {
    KindClosed,
    KindFile,
    KindDirectory,
    KindNull, /* /dev/null: reads nothing, accepts writes */
    KindZero, /* /dev/zero: reads zeros, accepts writes */
    KindRandom, /* /dev/urandom and /dev/random: read the kernel's entropy, accept writes, as Linux's do */
    KindStream, /* a duplicate of standard stream Index (0, 1 or 2) */
    KindPipeRead, /* the read end of pipe Index (pipe.c) */
    KindPipeWrite, /* the write end of pipe Index */
    KindShared /* shared memory file Index (shared.c, R3.2b) */
} Kind;

typedef struct Descriptor {
    Kind Kind;
    WitU32 Index; /* the entry of a file; the first entry under a directory's prefix; a stream; a pipe */
    WitU32 PrefixLength; /* of a directory: the name prefix including the trailing '/', 0 for the root */
    WitU32 Description; /* the open file description it shares with its duplicates */
    int CloseOnExec; /* FD_CLOEXEC, the descriptor's own */
} Descriptor;

/* An open file description: what an open made and its duplicates share. */
typedef struct Description {
    WitU64 Offset; /* the file position; a directory's reading cursor */
    int StatusFlags; /* the open's flags but O_CLOEXEC; F_SETFL changes O_NONBLOCK */
    WitU32 References; /* the descriptors that share it; zero when free */
} Description;

static Descriptor descriptors[DESCRIPTORS];
static Description descriptions[DESCRIPTORS];

#define POSITION(d) (descriptions[(d)->Description].Offset)
static unsigned char directory[NAME_MAX_BYTES]; /* the current directory: a name prefix without slashes at its ends */
static WitU32 directory_length; /* zero for the root */
static const unsigned char *table; /* the mapped header and table window */
static WitU64 table_window_bytes;
static WitU32 count;
static int mounted, broken;

static WitU32 read32(const unsigned char *p)
{
    return (WitU32)p[0] | ((WitU32)p[1] << 8) | ((WitU32)p[2] << 16) | ((WitU32)p[3] << 24);
}

static WitU64 read64(const unsigned char *p)
{
    return read32(p) | ((WitU64)read32(p + 4) << 32);
}

static long map_window(WitU64 offset, WitU64 bytes, WitU64 *address)
{
    WitMemoryMapRequest request;
    WitU64 result = 0;
    request.Version = WIT_MEMORY_MAP_VERSION;
    request.Size = sizeof(request);
    request.Object = PACKAGE_HANDLE;
    request.Offset = offset;
    request.Bytes = bytes;
    request.Address = 0;
    request.Protection = WIT_MEMORY_READ;
    request.Flags = 0;
    request.Target = WIT_PROCESS_SELF;
    const WitU64 status = wit_syscall(WIT_CALL_MEMORY_OBJECT_MAP, (WitU64)&request, sizeof(request), 0, &result);
    if (status != WIT_STATUS_OK) {
        return __wit_errno(status);
    }
    *address = result;
    return 0;
}

static void unmap_window(WitU64 address)
{
    WitU64 result = 0;
    wit_syscall(WIT_CALL_MEMORY_RELEASE, address, 0, 0, &result);
}

/* The entry's name and data ranges, as the validated table says. */
static void entry(WitU32 index, const unsigned char **name, WitU32 *name_length, WitU64 *data, WitU64 *length)
{
    const unsigned char *e = table + HEADER_BYTES + (WitU64)index * ENTRY_BYTES;
    *name = table + read32(e);
    *name_length = read32(e + 4);
    *data = read64(e + 8);
    *length = read64(e + 16);
}

static int compare(const unsigned char *a, WitU32 a_length, const unsigned char *b, WitU32 b_length)
{
    const WitU32 common = a_length < b_length ? a_length : b_length;
    for (WitU32 i = 0; i < common; ++i) {
        if (a[i] != b[i]) {
            return a[i] < b[i] ? -1 : 1;
        }
    }
    return a_length < b_length ? -1 : (a_length > b_length ? 1 : 0);
}

/* Maps and validates the header and the table (the format's rules: magic, version, sizes, count, names within the
 * window, sorted and valid); a package that fails is reported as an unreadable volume. */
static long mount(void)
{
    static const unsigned char magic[8] = {'W', 'I', 'T', 'P', 'A', 'K', '0', '1'};
    WitU64 address = 0, bytes = __wit_process.PackageBytes;
    if (mounted) {
        return broken ? -EIO : 0;
    }
    mounted = 1;
    broken = 1;
    if (bytes < HEADER_BYTES) {
        return -EIO;
    }
    table_window_bytes = bytes > WINDOW_BYTES ? WINDOW_BYTES : ((bytes + PAGE - 1) & ~(PAGE - 1));
    const long mapped = map_window(0, table_window_bytes, &address);
    if (mapped < 0) {
        return mapped;
    }
    table = (const unsigned char *)address;
    if (memcmp(table, magic, sizeof(magic)) != 0 ||
        read32(table + 8) != 1 ||
        read32(table + 12) != HEADER_BYTES ||
        read32(table + 20) != ENTRY_BYTES ||
        read64(table + 24) != bytes) {
        return -EIO;
    }
    count = read32(table + 16);
    if (count > MAX_FILES || HEADER_BYTES + (WitU64)count * ENTRY_BYTES > table_window_bytes) {
        return -EIO;
    }
    WitU64 cursor = HEADER_BYTES + (WitU64)count * ENTRY_BYTES;
    const unsigned char *previous = 0;
    WitU32 previous_length = 0;
    for (WitU32 i = 0; i < count; ++i) {
        const unsigned char *name;
        WitU32 length;
        WitU64 data, data_length;
        entry(i, &name, &length, &data, &data_length);
        if ((WitU64)(name - table) != cursor ||
            length == 0 ||
            length > NAME_MAX_BYTES ||
            cursor + length > table_window_bytes ||
            name[0] == '/' ||
            name[length - 1] == '/' ||
            data > bytes ||
            data_length > bytes - data) {
            return -EIO;
        }
        for (WitU32 k = 0; k < length; ++k) {
            if (name[k] == 0 || (name[k] == '/' && name[k + 1] == '/')) {
                return -EIO;
            }
        }
        if (previous && compare(previous, previous_length, name, length) >= 0) {
            return -EIO;
        }
        previous = name;
        previous_length = length;
        cursor += length;
    }
    broken = 0;
    return 0;
}

/* "/a/./b//c" becomes "a/b/c"; ".." beyond the root and empty names are refused. */
static long normalize(const char *path, unsigned char *out, WitU32 *out_length)
{
    WitU32 length = 0;
    if (!path) {
        return -EFAULT;
    }
    for (const char *p = path; *p;) {
        while (*p == '/') {
            ++p;
        }
        if (!*p) {
            break;
        }
        const char *start = p;
        while (*p && *p != '/') {
            ++p;
        }
        const WitU32 part = (WitU32)(p - start);
        if (part == 1 && start[0] == '.') {
            continue;
        }
        if (part == 2 && start[0] == '.' && start[1] == '.') {
            if (length == 0) {
                return -ENOENT;
            }
            --length;
            while (length > 0 && out[length - 1] != '/') {
                --length;
            }
            if (length > 0) {
                --length; /* the separator before the name ".." removed */
            }
            continue;
        }
        if (length + part + 1 >= NAME_MAX_BYTES) {
            return -ENAMETOOLONG;
        }
        if (length) {
            out[length++] = '/';
        }
        memcpy(out + length, start, part);
        length += part;
    }
    *out_length = length;
    return 0;
}

/* The entry of a name, or the first entry under a directory's prefix; -1 when neither. */
static int is_random_device(const unsigned char *name, WitU32 length)
{
    return (length == 11 && memcmp(name, "dev/urandom", 11) == 0) ||
        (length == 10 && memcmp(name, "dev/random", 10) == 0);
}

/* The kernel's entropy, in as many calls as the per-call limit needs. */
static long read_random(void *buffer, long bytes)
{
    long done = 0;
    while (done < bytes) {
        WitU64 result = 0;
        const WitU64 count = (WitU64)(bytes - done) > WIT_ABI_MAX_RANDOM ? WIT_ABI_MAX_RANDOM : (WitU64)(bytes - done);
        const WitU64 status = wit_syscall(WIT_CALL_RANDOM, (WitU64)buffer + (WitU64)done, count, 0, &result);
        if (status != WIT_STATUS_OK || result == 0) {
            return done ? done : __wit_errno(status);
        }
        done += (long)result;
    }
    return done;
}

static long lookup(const unsigned char *name, WitU32 length, Kind *kind, WitU32 *index)
{
    if (length == 0) {
        *kind = KindDirectory;
        *index = 0;
        return 0;
    }
    WitU32 low = 0, high = count;
    while (low < high) {
        const WitU32 middle = low + (high - low) / 2;
        const unsigned char *candidate;
        WitU32 candidate_length;
        WitU64 data, data_length;
        entry(middle, &candidate, &candidate_length, &data, &data_length);
        const int order = compare(name, length, candidate, candidate_length);
        if (order == 0) {
            *kind = KindFile;
            *index = middle;
            return 0;
        }
        if (order < 0) {
            high = middle;
        } else {
            low = middle + 1;
        }
    }
    /* The first entry sorting after the name is a child when it carries the name as a prefix before a '/'. */
    if (low < count) {
        const unsigned char *candidate;
        WitU32 candidate_length;
        WitU64 data, data_length;
        entry(low, &candidate, &candidate_length, &data, &data_length);
        if (candidate_length > length && candidate[length] == '/' && compare(name, length, candidate, length) == 0) {
            *kind = KindDirectory;
            *index = low;
            return 0;
        }
    }
    return -ENOENT;
}

/* An open descriptor of the table; a standard descriptor that is still its stream has none. */
static Descriptor *descriptor(long fd)
{
    if (fd < 0 || fd >= DESCRIPTORS || descriptors[fd].Kind == KindClosed) {
        return 0;
    }
    return &descriptors[fd];
}

/* A slot a new descriptor may take: empty, and below 3 only once its standard stream is closed. */
static int slot_free(long fd)
{
    return descriptors[fd].Kind == KindClosed && (fd >= FIRST_DESCRIPTOR || (__wit_process.ClosedStreams & (1U << fd)));
}

static long free_descriptor(long lowest)
{
    for (long fd = lowest < 0 ? 0 : lowest; fd < DESCRIPTORS; ++fd) {
        if (slot_free(fd)) {
            return fd;
        }
    }
    return -EMFILE;
}

static long open_descriptor(Kind kind, WitU32 index, WitU32 prefix_length, int flags)
{
    const long fd = free_descriptor(0);
    if (fd < 0) {
        return fd;
    }
    WitU32 description = 0;
    while (descriptions[description].References) {
        ++description; /* a free description exists: each one in use has a descriptor of its own */
    }
    descriptions[description] = (Description){0, flags & ~O_CLOEXEC, 1};
    descriptors[fd] = (Descriptor){kind, index, prefix_length, description, (flags & O_CLOEXEC) != 0};
    return fd;
}

/* A descriptor goes; its description and a pipe's end go with the last one. */
static void close_descriptor(Descriptor *d)
{
    if (d->Kind == KindPipeRead || d->Kind == KindPipeWrite) {
        __wit_pipe_end(d->Index, d->Kind == KindPipeWrite, -1);
        __wit_pipe_release(d->Index);
    }
    if (d->Kind == KindShared) {
        __wit_shared_release(d->Index);
    }
    --descriptions[d->Description].References;
    d->Kind = KindClosed;
}

/* The name of a directory descriptor: its prefix without the trailing '/', empty for the root. */
static WitU32 directory_name(const Descriptor *d, unsigned char *name)
{
    if (!d->PrefixLength) {
        return 0;
    }
    const unsigned char *first;
    WitU32 first_length;
    WitU64 data, data_length;
    entry(d->Index, &first, &first_length, &data, &data_length);
    memcpy(name, first, d->PrefixLength - 1);
    return d->PrefixLength - 1;
}

/* An absolute path alone, or a relative one after a base name, normalized together (S6.2). */
static long join(
    const unsigned char *base, WitU32 base_length, const char *path, unsigned char *name, WitU32 *name_length)
{
    char combined[2 * NAME_MAX_BYTES + 2];
    if (!path) {
        return -EFAULT;
    }
    if (path[0] == '/') {
        return normalize(path, name, name_length);
    }
    const size_t length = strlen(path);
    if (base_length + 1 + length + 1 > sizeof(combined)) {
        return -ENAMETOOLONG;
    }
    memcpy(combined, base, base_length);
    combined[base_length] = '/';
    memcpy(combined + base_length + 1, path, length + 1);
    return normalize(combined, name, name_length);
}

/* The name a path gives from a directory descriptor, AT_FDCWD being the current directory. */
static long resolve(long dirfd, const char *path, unsigned char *name, WitU32 *name_length)
{
    if (dirfd == AT_FDCWD || (path && path[0] == '/')) {
        return join(directory, directory_length, path, name, name_length);
    }
    const Descriptor *d = descriptor(dirfd);
    if (!d) {
        return -EBADF;
    }
    if (d->Kind != KindDirectory) {
        return -ENOTDIR;
    }
    unsigned char base[NAME_MAX_BYTES];
    const WitU32 base_length = directory_name(d, base);
    return join(base, base_length, path, name, name_length);
}

/* Whether a name is a directory of the package: the root, or a prefix of names. */
static long require_directory(const unsigned char *name, WitU32 length)
{
    Kind kind;
    WitU32 index;
    if (!length) {
        return 0;
    }
    const long status = lookup(name, length, &kind, &index);
    if (status < 0) {
        return status;
    }
    return kind == KindDirectory ? 0 : -ENOTDIR;
}

static long absolute(const unsigned char *name, WitU32 length, char *out, long size)
{
    if ((long)length + 2 > size) {
        return -ENAMETOOLONG;
    }
    out[0] = '/';
    memcpy(out + 1, name, length);
    out[length + 1] = 0;
    return (long)length + 1;
}

long __wit_chdir(const char *path)
{
    unsigned char name[NAME_MAX_BYTES];
    WitU32 length = 0;
    long status;
    if ((status = mount()) < 0 ||
        (status = resolve(AT_FDCWD, path, name, &length)) < 0 ||
        (status = require_directory(name, length)) < 0) {
        return status;
    }
    memcpy(directory, name, length);
    directory_length = length;
    return 0;
}

long __wit_fchdir(long fd)
{
    const Descriptor *d = descriptor(fd);
    if (!d) {
        return -EBADF;
    }
    if (d->Kind != KindDirectory) {
        return -ENOTDIR;
    }
    directory_length = directory_name(d, directory);
    return 0;
}

long __wit_directory_resolve(const char *base, const char *path, char *out, long size)
{
    unsigned char base_name[NAME_MAX_BYTES], name[NAME_MAX_BYTES];
    WitU32 base_length = directory_length, length = 0;
    long status;
    if ((status = mount()) < 0) {
        return status;
    }
    if (base) {
        if ((status = normalize(base, base_name, &base_length)) < 0) {
            return status;
        }
    } else {
        memcpy(base_name, directory, directory_length);
    }
    if ((status = join(base_name, base_length, path, name, &length)) < 0 ||
        (status = require_directory(name, length)) < 0) {
        return status;
    }
    return absolute(name, length, out, size);
}

long __wit_descriptor_directory(long fd, char *out, long size)
{
    unsigned char name[NAME_MAX_BYTES];
    const Descriptor *d = descriptor(fd);
    if (!d) {
        return -EBADF;
    }
    if (d->Kind != KindDirectory) {
        return -ENOTDIR;
    }
    return absolute(name, directory_name(d, name), out, size);
}

long __wit_set_directory(const char *path, long bytes)
{
    char terminated[NAME_MAX_BYTES];
    if (bytes < 0 || bytes >= (long)sizeof(terminated)) {
        return -ENAMETOOLONG;
    }
    memcpy(terminated, path, (size_t)bytes);
    terminated[bytes] = 0;
    return bytes ? normalize(terminated, directory, &directory_length) : 0;
}

static int shared_name(const unsigned char *name, WitU32 length);

long __wit_openat(long dirfd, const char *path, long flags, long mode)
{
    unsigned char name[NAME_MAX_BYTES];
    WitU32 length = 0;
    Kind kind;
    WitU32 index;
    long status;
    (void)mode;
    if ((status = mount()) < 0) {
        return status;
    }
    if ((status = resolve(dirfd, path, name, &length)) < 0) {
        return status;
    }
    if (shared_name(name, length)) {
        WitU32 file = 0;
        if ((flags & O_ACCMODE) == O_WRONLY || (flags & (O_APPEND | O_DIRECTORY))) {
            return -EINVAL;
        }
        if ((status = __wit_shared_open(name + 8, length - 8, flags, &file)) < 0) {
            return status;
        }
        const long fd = open_descriptor(KindShared, file, 0, (int)flags);
        if (fd < 0) {
            __wit_shared_release(file);
        }
        return fd;
    }
    if ((flags & (O_CREAT | O_TRUNC | O_APPEND)) || (flags & O_TMPFILE) == O_TMPFILE) {
        return -EROFS; /* nothing is created, truncated or appended to; O_TMPFILE carries O_DIRECTORY, so it is compared whole */
    }
    if (length == 8 && memcmp(name, "dev/null", 8) == 0) {
        return open_descriptor(KindNull, 0, 0, (int)flags); /* readable and writable, like the device */
    }
    if (length == 8 && memcmp(name, "dev/zero", 8) == 0) {
        return open_descriptor(KindZero, 0, 0, (int)flags);
    }
    if (is_random_device(name, length)) {
        return open_descriptor(KindRandom, 0, 0, (int)flags);
    }
    if ((flags & O_ACCMODE) != O_RDONLY) {
        return -EROFS;
    }
    if ((status = lookup(name, length, &kind, &index)) < 0) {
        return status;
    }
    if (kind == KindFile && (flags & O_DIRECTORY)) {
        return -ENOTDIR;
    }
    return open_descriptor(kind, index, kind == KindDirectory ? (length ? length + 1 : 0) : 0, (int)flags);
}

long __wit_close(long fd)
{
    Descriptor *d = descriptor(fd);
    if (!d) {
        if (fd < 0 || fd >= FIRST_DESCRIPTOR || (__wit_process.ClosedStreams & (1U << fd))) {
            return -EBADF;
        }
        __wit_process.ClosedStreams |= 1U << fd; /* a standard stream goes until a dup or dup2 fills the slot (S6.2) */
        return 0;
    }
    close_descriptor(d);
    return 0;
}

/* A path under /dev/shm names a shared memory file (R3.2b). */
static int shared_name(const unsigned char *name, WitU32 length)
{
    return length > 8 && memcmp(name, "dev/shm/", 8) == 0;
}

/* memfd_create: an anonymous shared memory file, read and written through its mappings (R3.2b). */
long __wit_memfd_create(const char *name, long flags)
{
    WitU32 file = 0;
    long status;
    (void)name;
    if (flags & ~(long)(MFD_CLOEXEC | MFD_ALLOW_SEALING)) {
        return -EINVAL;
    }
    if ((status = __wit_shared_anonymous(&file)) < 0) {
        return status;
    }
    const long fd = open_descriptor(KindShared, file, 0, O_RDWR | ((flags & MFD_CLOEXEC) ? O_CLOEXEC : 0));
    if (fd < 0) {
        __wit_shared_release(file);
    }
    return fd;
}

/* ftruncate: a shared memory file takes the size; a file of the package is never writable. */
long __wit_ftruncate(long fd, long size)
{
    const Descriptor *d = descriptor(fd);
    if (!d) {
        return __wit_stream_of(fd) >= 0 ? -EINVAL : -EBADF;
    }
    if (size < 0 || d->Kind != KindShared || (descriptions[d->Description].StatusFlags & O_ACCMODE) == O_RDONLY) {
        return -EINVAL;
    }
    return __wit_shared_truncate(d->Index, (WitU64)size);
}

/* unlinkat: a shared memory file's name goes (R3.2b); the package is read-only, and a directory is never removed. */
long __wit_unlinkat(long dirfd, const char *path, long flags)
{
    unsigned char name[NAME_MAX_BYTES];
    WitU32 length = 0, index = 0;
    Kind kind;
    long status;
    if (flags & ~(long)AT_REMOVEDIR) {
        return -EINVAL;
    }
    if ((status = mount()) < 0) {
        return status;
    }
    if ((status = resolve(dirfd, path, name, &length)) < 0) {
        return status;
    }
    if (shared_name(name, length)) {
        return (flags & AT_REMOVEDIR) ? -ENOTDIR : __wit_shared_unlink(name + 8, length - 8);
    }
    if ((status = lookup(name, length, &kind, &index)) < 0) {
        return status;
    }
    return -EROFS;
}

/* Reads bytes of a file through windows of the package object. */
static long read_file(const Descriptor *d, unsigned char *buffer, WitU64 bytes, WitU64 position)
{
    const unsigned char *name;
    WitU32 name_length;
    WitU64 data, length, done = 0;
    entry(d->Index, &name, &name_length, &data, &length);
    if (position >= length) {
        return 0;
    }
    if (bytes > length - position) {
        bytes = length - position;
    }
    while (done < bytes) {
        const WitU64 absolute = data + position + done;
        const WitU64 window_start = absolute & ~(PAGE - 1);
        WitU64 window_bytes = WINDOW_BYTES, address = 0;
        const WitU64 object_bytes = (__wit_process.PackageBytes + PAGE - 1) & ~(PAGE - 1);
        if (window_start + window_bytes > object_bytes) {
            window_bytes = object_bytes - window_start;
        }
        const long mapped = map_window(window_start, window_bytes, &address);
        if (mapped < 0) {
            return done ? (long)done : mapped;
        }
        WitU64 chunk = window_start + window_bytes - absolute;
        if (chunk > bytes - done) {
            chunk = bytes - done;
        }
        memcpy(buffer + done, (const unsigned char *)(address + (absolute - window_start)), chunk);
        unmap_window(address);
        done += chunk;
    }
    return (long)done;
}

long __wit_read(long fd, void *buffer, long bytes)
{
    Descriptor *d = descriptor(fd);
    if (!d) {
        return -EBADF;
    }
    if (bytes < 0) {
        return -EINVAL;
    }
    if (d->Kind == KindDirectory) {
        return -EISDIR;
    }
    if (d->Kind == KindNull) {
        return 0;
    }
    if (d->Kind == KindZero) {
        memset(buffer, 0, (unsigned long)bytes);
        return bytes;
    }
    if (d->Kind == KindRandom) {
        return read_random(buffer, bytes);
    }
    if (d->Kind == KindShared) {
        return -EINVAL; /* a shared memory file is read through a mapping */
    }
    if (d->Kind != KindFile) {
        return -EBADF; /* a stream or a pipe never reaches here (syscall.c) */
    }
    const long read = read_file(d, buffer, (WitU64)bytes, POSITION(d));
    if (read > 0) {
        POSITION(d) += (WitU64)read;
    }
    return read;
}

long __wit_pread(long fd, void *buffer, long bytes, long offset)
{
    const Descriptor *d = descriptor(fd);
    if (!d) {
        return -EBADF;
    }
    if (bytes < 0 || offset < 0) {
        return -EINVAL;
    }
    if (d->Kind == KindDirectory) {
        return -EISDIR;
    }
    if (d->Kind == KindNull) {
        return 0;
    }
    if (d->Kind == KindZero) {
        memset(buffer, 0, (unsigned long)bytes);
        return bytes;
    }
    if (d->Kind == KindRandom) {
        return read_random(buffer, bytes);
    }
    if (d->Kind == KindShared) {
        return -EINVAL; /* a shared memory file is read through a mapping */
    }
    if (d->Kind != KindFile) {
        return -ESPIPE;
    }
    return read_file(d, buffer, (WitU64)bytes, (WitU64)offset);
}

long __wit_readv(long fd, const struct iovec *vectors, long count_vectors)
{
    long total = 0;
    if (count_vectors < 0 || count_vectors > 1024) {
        return -EINVAL;
    }
    for (long i = 0; i < count_vectors; ++i) {
        if (vectors[i].iov_len == 0) {
            continue;
        }
        const long read = __wit_read(fd, vectors[i].iov_base, (long)vectors[i].iov_len);
        if (read < 0) {
            return total ? total : read;
        }
        total += read;
        if ((unsigned long)read < vectors[i].iov_len) {
            break;
        }
    }
    return total;
}

long __wit_write_file(long fd, long bytes)
{
    const Descriptor *d = descriptor(fd);
    if (!d) {
        return -EBADF;
    }
    /* Files are read-only descriptors; the devices accept writes. */
    return d->Kind == KindNull || d->Kind == KindZero || d->Kind == KindRandom ? bytes : -EBADF;
}

long __wit_lseek(long fd, long offset, long whence)
{
    Descriptor *d = descriptor(fd);
    if (!d) {
        return __wit_stream_of(fd) >= 0 ? -ESPIPE : -EBADF;
    }
    if (d->Kind == KindStream || d->Kind == KindPipeRead || d->Kind == KindPipeWrite) {
        return -ESPIPE;
    }
    WitU64 length = d->Kind == KindShared ? __wit_shared_size(d->Index) : 0;
    if (d->Kind == KindFile) {
        const unsigned char *name;
        WitU32 name_length;
        WitU64 data;
        entry(d->Index, &name, &name_length, &data, &length);
    }
    WitU64 base;
    switch (whence) {
    case SEEK_SET:
        base = 0;
        break;
    case SEEK_CUR:
        base = POSITION(d);
        break;
    case SEEK_END:
        base = length;
        break;
    default:
        return -EINVAL;
    }
    if (offset < 0 && (WitU64)(-offset) > base) {
        return -EINVAL;
    }
    POSITION(d) = base + (WitU64)offset;
    return (long)POSITION(d);
}

static void fill_stat(struct kstat *st, Kind kind, WitU32 index, WitU64 length)
{
    memset(st, 0, sizeof(*st));
    st->st_dev = 1;
    st->st_nlink = 1;
    st->st_blksize = (blksize_t)PAGE;
    if (kind == KindFile) {
        st->st_ino = (ino_t)index + 1;
        st->st_mode = S_IFREG | 0444;
        st->st_size = (off_t)length;
        st->st_blocks = (blkcnt_t)((length + 511) / 512);
    } else if (kind == KindDirectory) {
        st->st_ino = (ino_t)(DIRECTORY_INODE_BASE + index);
        st->st_mode = S_IFDIR | 0555;
    } else if (kind == KindPipeRead || kind == KindPipeWrite) {
        st->st_dev = 2;
        st->st_ino = (ino_t)index + 1;
        st->st_mode = S_IFIFO | 0600;
    } else if (kind == KindStream) {
        st->st_ino = 3; /* the standard streams' device */
        st->st_mode = S_IFCHR | 0666;
    } else if (kind == KindShared) {
        st->st_dev = 3;
        st->st_ino = (ino_t)index + 1;
        st->st_mode = S_IFREG | 0600;
        st->st_size = (off_t)length;
        st->st_blocks = (blkcnt_t)((length + 511) / 512);
    } else {
        st->st_ino = 2;
        st->st_mode = S_IFCHR | 0666;
    }
}

long __wit_fstatat(long dirfd, const char *path, struct kstat *st, long flags)
{
    unsigned char name[NAME_MAX_BYTES];
    WitU32 length = 0;
    Kind kind;
    WitU32 index;
    long status;
    if ((status = mount()) < 0) {
        return status;
    }
    if ((flags & AT_EMPTY_PATH) && (!path || !path[0])) {
        if (dirfd >= 0 && dirfd < FIRST_DESCRIPTOR && !(__wit_process.ClosedStreams & (1U << dirfd))) {
            memset(st, 0, sizeof(*st));
            st->st_dev = 1;
            st->st_ino = 3;
            st->st_nlink = 1;
            st->st_mode = S_IFCHR | 0666;
            st->st_blksize = (blksize_t)PAGE;
            return 0;
        }
        const Descriptor *d = descriptor(dirfd);
        if (!d) {
            return -EBADF;
        }
        WitU64 data = 0, file_length = d->Kind == KindShared ? __wit_shared_size(d->Index) : 0;
        if (d->Kind == KindFile) {
            const unsigned char *n;
            WitU32 n_length;
            entry(d->Index, &n, &n_length, &data, &file_length);
        }
        fill_stat(st, d->Kind, d->Index, file_length);
        return 0;
    }
    if ((status = resolve(dirfd, path, name, &length)) < 0) {
        return status;
    }
    if ((length == 8 && (memcmp(name, "dev/null", 8) == 0 || memcmp(name, "dev/zero", 8) == 0)) ||
        is_random_device(name, length)) {
        fill_stat(st, KindNull, 0, 0);
        return 0;
    }
    if (length == 3 && memcmp(name, "dev", 3) == 0) {
        fill_stat(st, KindDirectory, 0, 0);
        return 0;
    }
    if ((status = lookup(name, length, &kind, &index)) < 0) {
        return status;
    }
    WitU64 data = 0, file_length = 0;
    if (kind == KindFile) {
        const unsigned char *n;
        WitU32 n_length;
        entry(index, &n, &n_length, &data, &file_length);
    }
    fill_stat(st, kind, index, file_length);
    return 0;
}

long __wit_faccessat(long dirfd, const char *path, long mode)
{
    unsigned char name[NAME_MAX_BYTES];
    WitU32 length = 0;
    Kind kind;
    WitU32 index;
    long status;
    if ((status = mount()) < 0) {
        return status;
    }
    if ((status = resolve(dirfd, path, name, &length)) < 0) {
        return status;
    }
    if ((length == 8 && (memcmp(name, "dev/null", 8) == 0 || memcmp(name, "dev/zero", 8) == 0)) ||
        is_random_device(name, length)) {
        return 0;
    }
    if ((status = lookup(name, length, &kind, &index)) < 0) {
        return status;
    }
    if (mode & W_OK) {
        return -EROFS;
    }
    if ((mode & X_OK) && kind == KindFile) {
        return -EACCES;
    }
    return 0;
}

/* The package has no symbolic links: a path that names a file or a directory is not a link (EINVAL), and one that
 * names nothing is ENOENT, as Linux answers. musl's realpath tells the two apart (R1.2b). */
long __wit_readlinkat(long dirfd, const char *path, long size)
{
    if (size <= 0) {
        return -EINVAL;
    }
    const long status = __wit_faccessat(dirfd, path, F_OK);
    return status < 0 ? status : -EINVAL;
}

/* The children of a directory in table order: a file as itself, a subdirectory once at its first entry. The
 * cursor is the next table index; "." and ".." come first as on Linux. */
long __wit_getdents(long fd, unsigned char *buffer, long bytes)
{
    Descriptor *d = descriptor(fd);
    if (!d) {
        return -EBADF;
    }
    if (d->Kind != KindDirectory) {
        return -ENOTDIR;
    }
    long filled = 0;
    while (1) {
        const unsigned char *child = 0;
        WitU32 child_length = 0;
        unsigned char type = DT_DIR;
        WitU64 inode = 0;
        WitU32 next;
        if (POSITION(d) == 0 || POSITION(d) == 1) {
            child = (const unsigned char *)(POSITION(d) == 0 ? "." : "..");
            child_length = POSITION(d) == 0 ? 1 : 2;
            inode = DIRECTORY_INODE_BASE + d->Index;
            next = (WitU32)POSITION(d) + 1;
        } else {
            WitU32 i = (WitU32)(POSITION(d) - 2);
            if (i < d->Index) {
                i = d->Index;
            }
            if (i >= count) {
                break;
            }
            const unsigned char *name;
            WitU32 name_length;
            WitU64 data, length;
            entry(i, &name, &name_length, &data, &length);
            if (d->PrefixLength) {
                const unsigned char *first;
                WitU32 first_length;
                WitU64 fd_, fl_;
                entry(d->Index, &first, &first_length, &fd_, &fl_);
                if (name_length <= d->PrefixLength || compare(first, d->PrefixLength, name, d->PrefixLength) != 0) {
                    break; /* past the directory's entries */
                }
            }
            WitU32 end = d->PrefixLength;
            while (end < name_length && name[end] != '/') {
                ++end;
            }
            child = name + d->PrefixLength;
            child_length = end - d->PrefixLength;
            if (end < name_length) {
                /* a subdirectory: skip its remaining entries */
                next = i + 1;
                while (next < count) {
                    const unsigned char *following;
                    WitU32 following_length;
                    WitU64 fdata, flength;
                    entry(next, &following, &following_length, &fdata, &flength);
                    if (following_length <= end || following[end] != '/' || compare(name, end, following, end) != 0) {
                        break;
                    }
                    ++next;
                }
                inode = DIRECTORY_INODE_BASE + i;
            } else {
                type = DT_REG;
                inode = (WitU64)i + 1;
                next = i + 1;
            }
            next += 2;
        }
        const long record = (long)((19 + child_length + 1 + 7) & ~7UL);
        if (filled + record > bytes) {
            if (filled == 0) {
                return -EINVAL;
            }
            break;
        }
        unsigned char *out = buffer + filled;
        memset(out, 0, (unsigned long)record);
        memcpy(out, &inode, 8); /* d_ino */
        const WitU64 off = next;
        memcpy(out + 8, &off, 8); /* d_off: the next cursor */
        out[16] = (unsigned char)record;
        out[17] = (unsigned char)(record >> 8); /* d_reclen */
        out[18] = type; /* d_type */
        memcpy(out + 19, child, child_length);
        filled += record;
        POSITION(d) = next;
    }
    return filled;
}

long __wit_getcwd(char *buffer, long size)
{
    if (size < (long)directory_length + 2) {
        return -ERANGE;
    }
    return absolute(directory, directory_length, buffer, size) +
        1; /* the bytes with the terminator, as Linux returns */
}

/* The standard stream fd stands for: itself while open, a stream duplicate's own; -1 for anything else. */
long __wit_stream_of(long fd)
{
    const Descriptor *d = descriptor(fd);
    if (d) {
        return d->Kind == KindStream ? (long)d->Index : -1;
    }
    return fd >= 0 && fd < FIRST_DESCRIPTOR && !(__wit_process.ClosedStreams & (1U << fd)) ? fd : -1;
}

/* poll's readiness of a descriptor (R3.2), under the table lock: a pipe end as its pipe reports it; a file, a directory,
 * a device or a standard stream ready for what was asked, as a regular file is on Linux (a stream's reads end the
 * input at once); POLLNVAL for a descriptor that is not open. */
short __wit_descriptor_poll(long fd, short events)
{
    const short asked = (short)(events & (POLLIN | POLLOUT | POLLRDNORM | POLLWRNORM));
    const Descriptor *d = descriptor(fd);
    if (!d) {
        return __wit_stream_of(fd) >= 0 ? asked : POLLNVAL;
    }
    if (d->Kind == KindPipeRead || d->Kind == KindPipeWrite) {
        return __wit_pipe_poll(d->Index, d->Kind == KindPipeWrite, events);
    }
    return asked;
}

/* The pipe an end descriptor names, with a reference for the transfer the caller makes outside the table lock. */
int __wit_pipe_of(long fd, WitU32 *index, int *write_end, int *nonblocking)
{
    const Descriptor *d = descriptor(fd);
    if (!d || (d->Kind != KindPipeRead && d->Kind != KindPipeWrite)) {
        return 0;
    }
    *index = d->Index;
    *write_end = d->Kind == KindPipeWrite;
    *nonblocking = (descriptions[d->Description].StatusFlags & O_NONBLOCK) != 0;
    __wit_pipe_reference(d->Index);
    return 1;
}

/* pipe2: two descriptors over a new pipe, the read end first. */
long __wit_pipe2(int *fds, long flags)
{
    WitU32 index;
    if (flags & ~(O_CLOEXEC | O_NONBLOCK)) {
        return -EINVAL;
    }
    const long reader = free_descriptor(0);
    if (reader < 0) {
        return reader;
    }
    descriptors[reader].Kind = KindPipeRead; /* held while the second is found */
    const long writer = free_descriptor(0);
    descriptors[reader].Kind = KindClosed;
    if (writer < 0) {
        return writer;
    }
    const long created = __wit_pipe_create(&index);
    if (created < 0) {
        return created;
    }
    const long read_end = open_descriptor(KindPipeRead, index, 0, (int)(O_RDONLY | flags));
    const long write_end = open_descriptor(KindPipeWrite, index, 0, (int)(O_WRONLY | flags));
    fds[0] = (int)read_end;
    fds[1] = (int)write_end;
    return 0;
}

/* What fd names, as a source of a duplicate: a stream (its number) or a descriptor; -EBADF for neither. */
static long duplicate_source(long fd, const Descriptor **source)
{
    *source = 0;
    const long stream = __wit_stream_of(fd);
    if (stream >= 0) {
        return stream;
    }
    *source = descriptor(fd);
    return *source ? 3 : -EBADF;
}

/* Makes the free slot target a duplicate of the source. A standard descriptor given its own stream back, or an output
 * given an output (both are the log), is that stream again; any other duplicate there stands for it within this
 * process. */
static void duplicate_into(long target, long stream, const Descriptor *source, int close_on_exec)
{
    if (target < FIRST_DESCRIPTOR) {
        if (!source && (stream == target || (stream > 0 && target > 0))) {
            __wit_process.ClosedStreams &= ~(1U << target);
            return;
        }
        __wit_process.ClosedStreams |= 1U << target; /* closing the duplicate there leaves the descriptor closed */
    }
    if (!source) {
        WitU32 description = 0;
        while (descriptions[description].References) {
            ++description;
        }
        descriptions[description] = (Description){0, stream == 0 ? O_RDONLY : O_WRONLY, 1};
        descriptors[target] = (Descriptor){KindStream, (WitU32)stream, 0, description, close_on_exec};
        return;
    }
    descriptors[target] = *source;
    descriptors[target].CloseOnExec = close_on_exec;
    ++descriptions[source->Description].References;
    if (source->Kind == KindPipeRead || source->Kind == KindPipeWrite) {
        __wit_pipe_reference(source->Index);
        __wit_pipe_end(source->Index, source->Kind == KindPipeWrite, 1);
    }
    if (source->Kind == KindShared) {
        __wit_shared_reference(source->Index);
    }
}

/* dup and F_DUPFD: the lowest free descriptor at or above lowest. */
long __wit_dup(long fd, long lowest, int close_on_exec)
{
    const Descriptor *source;
    const long stream = duplicate_source(fd, &source);
    if (stream < 0) {
        return stream;
    }
    if (lowest < 0) {
        return -EINVAL;
    }
    const long target = free_descriptor(lowest);
    if (target < 0) {
        return target;
    }
    duplicate_into(target, stream, source, close_on_exec);
    return target;
}

/* dup2 (flags -1) and dup3: target becomes fd's duplicate, closed first when open. */
long __wit_dup3(long fd, long target, long flags)
{
    const Descriptor *source;
    const long stream = duplicate_source(fd, &source);
    if (stream < 0) {
        return stream;
    }
    if (flags != -1 && (flags & ~O_CLOEXEC)) {
        return -EINVAL;
    }
    if (target == fd) {
        return flags == -1 ? target : -EINVAL;
    }
    if (target < 0 || target >= DESCRIPTORS) {
        return -EBADF;
    }
    Descriptor *old = descriptor(target);
    if (old) {
        close_descriptor(old);
    } else if (target < FIRST_DESCRIPTOR) {
        __wit_process.ClosedStreams |= 1U << target; /* the standard stream there is replaced */
    }
    duplicate_into(target, stream, source, flags != -1 && (flags & O_CLOEXEC));
    return target;
}

long __wit_fcntl(long fd, long command, long argument)
{
    if (command == F_DUPFD || command == F_DUPFD_CLOEXEC) {
        return __wit_dup(fd, argument, command == F_DUPFD_CLOEXEC);
    }
    Descriptor *d = descriptor(fd);
    if (!d) {
        if (fd >= 0 && fd < FIRST_DESCRIPTOR && !(__wit_process.ClosedStreams & (1U << fd))) {
            return command == F_GETFL ? (fd == 0 ? O_RDONLY : O_WRONLY)
                                      : (command == F_GETFD || command == F_SETFD || command == F_SETFL ? 0 : -EINVAL);
        }
        return -EBADF;
    }
    Description *description = &descriptions[d->Description];
    switch (command) {
    case F_GETFL:
        return description->StatusFlags & (O_ACCMODE | O_DIRECTORY | O_NOFOLLOW | O_NONBLOCK);
    case F_SETFL:
        /* Linux changes O_APPEND, O_ASYNC, O_DIRECT, O_NOATIME and O_NONBLOCK here; only O_NONBLOCK means anything. */
        description->StatusFlags = (description->StatusFlags & ~O_NONBLOCK) | ((int)argument & O_NONBLOCK);
        return 0;
    case F_GETFD:
        return d->CloseOnExec ? FD_CLOEXEC : 0;
    case F_SETFD:
        d->CloseOnExec = (argument & FD_CLOEXEC) != 0;
        return 0;
    default:
        return -EINVAL;
    }
}

/* A descriptor of the package or a device: what files.c reads and writes itself. */
int __wit_is_file_descriptor(long fd)
{
    const Descriptor *d = descriptor(fd);
    return d && d->Kind != KindStream && d->Kind != KindPipeRead && d->Kind != KindPipeWrite;
}

/* FIONREAD of a pipe's read end: -ENOTTY for anything else. */
long __wit_pipe_bytes(long fd)
{
    const Descriptor *d = descriptor(fd);
    return d && d->Kind == KindPipeRead ? __wit_pipe_available(d->Index) : -ENOTTY;
}

/* What mmap (memory.c, S5.1) maps for a descriptor: a package file's data offset in the package and its length
 * (0), /dev/zero, which maps as anonymous memory (1), or a shared memory file, its index and size (2, R3.2b);
 * nothing else is mappable. libwitos's loader reads the same (S5.2). */
long __wit_file_map_source(long fd, WitU64 *source, WitU64 *length)
{
    const Descriptor *d = descriptor(fd);
    if (!d) {
        return -EBADF;
    }
    if (d->Kind == KindZero) {
        return 1;
    }
    if (d->Kind == KindShared) {
        *source = d->Index;
        *length = __wit_shared_size(d->Index);
        return (descriptions[d->Description].StatusFlags & O_ACCMODE) == O_RDONLY ? 3 : 2;
    }
    if (d->Kind != KindFile) {
        return -ENODEV;
    }
    const unsigned char *name;
    WitU32 name_length;
    entry(d->Index, &name, &name_length, source, length);
    return 0;
}
