#define _GNU_SOURCE
#include "witos_libc.h"
#include "witos/memory_object.h"
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/uio.h>
#include "kstat.h"

/* Files over the read-only boot package (RFC 0011 section 9.3, plan step S1.2). The package is the root task's
 * initial capability: a memory object the kernel never parses for it, in the format of witos/package.h — a header,
 * a sorted table of entries (name offset and length, data offset and length), the names, then the data. The
 * library maps the header and the table once, read-only, and validates them as the kernel's loader does; a read
 * maps the window of the bytes it needs, copies and releases it, so a file of any size is readable through a
 * mapping of at most 64 pages. The namespace is the package's: "/" is its root, a name is a file, a prefix of a name
 * up to '/' is a directory, "/dev/null" and "/dev/zero" are the devices; nothing is writable (EROFS), there is no current
 * directory but "/" (S6 moves it to the library), and no symbolic links. Descriptors 3 and above are the files;
 * 0–2 stay the kernel log. */

#define PACKAGE_HANDLE (__wit_startup->Handles[WIT_ROOT_HANDLE_PACKAGE])
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
    KindZero /* /dev/zero: reads zeros, accepts writes */
} Kind;

typedef struct Descriptor {
    Kind Kind;
    WitU32 Index; /* the entry of a file; the first entry under a directory's prefix */
    WitU32 PrefixLength; /* of a directory: the name prefix including the trailing '/', 0 for the root */
    WitU64 Offset; /* the file position; a directory's reading cursor */
    int Flags;
} Descriptor;

static Descriptor descriptors[DESCRIPTORS];
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

/* Maps and validates the header and the table (the kernel's rules: magic, version, sizes, count, names within the
 * window, sorted and valid); a package that fails is reported as an unreadable volume. */
static long mount(void)
{
    static const unsigned char magic[8] = {'W', 'I', 'T', 'P', 'A', 'K', '0', '1'};
    WitU64 address = 0, bytes = __wit_startup->PackageBytes;
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

static Descriptor *descriptor(long fd)
{
    if (fd < FIRST_DESCRIPTOR || fd >= DESCRIPTORS || descriptors[fd].Kind == KindClosed) {
        return 0;
    }
    return &descriptors[fd];
}

static long open_descriptor(Kind kind, WitU32 index, WitU32 prefix_length, int flags)
{
    for (long fd = FIRST_DESCRIPTOR; fd < DESCRIPTORS; ++fd) {
        if (descriptors[fd].Kind == KindClosed) {
            descriptors[fd].Kind = kind;
            descriptors[fd].Index = index;
            descriptors[fd].PrefixLength = prefix_length;
            descriptors[fd].Offset = 0;
            descriptors[fd].Flags = flags;
            return fd;
        }
    }
    return -EMFILE;
}

/* The path relative to a directory descriptor, AT_FDCWD being the root. */
static long resolve(long dirfd, const char *path, unsigned char *name, WitU32 *name_length)
{
    unsigned char relative[NAME_MAX_BYTES];
    WitU32 relative_length = 0;
    const long normalized = normalize(path, relative, &relative_length);
    if (normalized < 0) {
        return normalized;
    }
    if (dirfd == AT_FDCWD || (path && path[0] == '/')) {
        memcpy(name, relative, relative_length);
        *name_length = relative_length;
        return 0;
    }
    const Descriptor *d = descriptor(dirfd);
    if (!d) {
        return -EBADF;
    }
    if (d->Kind != KindDirectory) {
        return -ENOTDIR;
    }
    if (d->PrefixLength + relative_length >= NAME_MAX_BYTES) {
        return -ENAMETOOLONG;
    }
    WitU32 length = 0;
    if (d->PrefixLength) {
        const unsigned char *first;
        WitU32 first_length;
        WitU64 data, data_length;
        entry(d->Index, &first, &first_length, &data, &data_length);
        memcpy(name, first, d->PrefixLength - 1);
        length = d->PrefixLength - 1;
    }
    if (relative_length) {
        if (length) {
            name[length++] = '/';
        }
        memcpy(name + length, relative, relative_length);
        length += relative_length;
    }
    *name_length = length;
    return 0;
}

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
    if ((flags & (O_CREAT | O_TRUNC | O_APPEND)) || (flags & O_TMPFILE) == O_TMPFILE) {
        return -EROFS; /* nothing is created, truncated or appended to; O_TMPFILE carries O_DIRECTORY, so it is compared whole */
    }
    if (length == 8 && memcmp(name, "dev/null", 8) == 0) {
        return open_descriptor(KindNull, 0, 0, (int)flags); /* readable and writable, like the device */
    }
    if (length == 8 && memcmp(name, "dev/zero", 8) == 0) {
        return open_descriptor(KindZero, 0, 0, (int)flags);
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
        return fd >= 0 && fd < FIRST_DESCRIPTOR ? 0 : -EBADF;
    }
    d->Kind = KindClosed;
    return 0;
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
        const WitU64 object_bytes = (__wit_startup->PackageBytes + PAGE - 1) & ~(PAGE - 1);
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
    const long read = read_file(d, buffer, (WitU64)bytes, d->Offset);
    if (read > 0) {
        d->Offset += (WitU64)read;
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
    return d->Kind == KindNull || d->Kind == KindZero ? bytes : -EBADF; /* files are read-only descriptors */
}

long __wit_lseek(long fd, long offset, long whence)
{
    Descriptor *d = descriptor(fd);
    if (!d) {
        return fd >= 0 && fd < FIRST_DESCRIPTOR ? -ESPIPE : -EBADF;
    }
    WitU64 length = 0;
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
        base = d->Offset;
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
    d->Offset = base + (WitU64)offset;
    return (long)d->Offset;
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
        if (dirfd >= 0 && dirfd < FIRST_DESCRIPTOR) {
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
        WitU64 data = 0, file_length = 0;
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
    if (length == 8 && (memcmp(name, "dev/null", 8) == 0 || memcmp(name, "dev/zero", 8) == 0)) {
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
    if (length == 8 && (memcmp(name, "dev/null", 8) == 0 || memcmp(name, "dev/zero", 8) == 0)) {
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
        if (d->Offset == 0 || d->Offset == 1) {
            child = (const unsigned char *)(d->Offset == 0 ? "." : "..");
            child_length = d->Offset == 0 ? 1 : 2;
            inode = DIRECTORY_INODE_BASE + d->Index;
            next = (WitU32)d->Offset + 1;
        } else {
            WitU32 i = (WitU32)(d->Offset - 2);
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
        d->Offset = next;
    }
    return filled;
}

long __wit_getcwd(char *buffer, long size)
{
    if (size < 2) {
        return -ERANGE;
    }
    buffer[0] = '/';
    buffer[1] = 0;
    return 2;
}

long __wit_fcntl(long fd, long command, long argument)
{
    Descriptor *d = descriptor(fd);
    (void)argument;
    if (!d) {
        if (fd >= 0 && fd < FIRST_DESCRIPTOR) {
            return command == F_GETFL ? (fd == 0 ? O_RDONLY : O_WRONLY)
                                      : (command == F_GETFD || command == F_SETFD ? 0 : -EINVAL);
        }
        return -EBADF;
    }
    switch (command) {
    case F_GETFL:
        return d->Flags & (O_ACCMODE | O_DIRECTORY | O_NOFOLLOW);
    case F_GETFD:
        return (d->Flags & O_CLOEXEC) ? FD_CLOEXEC : 0;
    case F_SETFD:
        return 0;
    default:
        return -EINVAL;
    }
}

int __wit_is_file_descriptor(long fd)
{
    return descriptor(fd) != 0;
}
