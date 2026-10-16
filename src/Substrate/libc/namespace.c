#define _GNU_SOURCE
#include "witos_libc.h"
#include "witos/channels.h"
#include "witos/memory_object.h"
#include "witos/namespace.h"
#include "witos/wait_objects.h"
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <string.h>
#include <sys/stat.h>
#include "kstat.h"

/* Files of the volumes the namespace service mounts (plan step D5.1, witos/namespace.h). A path below one of the
 * service's mount points (/data, /fat, /boot, as it names them) is the service's; every other path stays the
 * package's (files.c). The library connects once, at the first such path: it makes a session channel and a window of
 * its own memory, sends CONNECT on the namespace's endpoint the process started with, and from then on sends each
 * request on the session and waits for its reply, one at a time, under the table lock of __wit_syscall; a path, the
 * data and a listing travel in the window. A descriptor of such a file or directory is the index of a remote record,
 * the service's handle; the descriptors that share it count as its references, and the last one closes the handle. */

#define REMOTES \
    1024U /* CoreCLR keeps every assembly it loads open (R6), and a shared mapping holds its record after the \
                         descriptor closes (R4.2): as many as the descriptor table holds (N4.1) */
#define DIRECTORIES 64U /* the remote directories open at once, whose names fchdir and the *at calls need */
#define MOUNTS 16U
#define MOUNT_BYTES 64U
#define NAME_BYTES 1024U
#define WINDOW WIT_NAMESPACE_WINDOW_BYTES

typedef struct Remote {
    WitU64 Handle; /* the service's handle; zero when the record is free */
    WitU32 References; /* the descriptors that share it */
    WitU32 Directory; /* one more than the directory's slot of names for a directory, zero for a file */
} Remote;

static Remote remotes[REMOTES];
static unsigned char directory_names[DIRECTORIES][NAME_BYTES];
static WitU32 directory_lengths[DIRECTORIES];
static char mount_points[MOUNTS][MOUNT_BYTES];
static WitU32 mount_lengths[MOUNTS], mount_count;
static int connected; /* 1 connected, -1 failed for good */
static WitU64 session;
static unsigned char *window;

static void close_handle(WitU64 handle)
{
    WitU64 result = 0;
    if (handle) {
        wit_syscall(WIT_CALL_HANDLE_CLOSE, handle, 0, 0, &result);
    }
}

/* Sends a message on an endpoint, the handles moved with it. */
static long namespace_send(WitU64 endpoint, const void *bytes, WitU32 count, WitU64 *handles, WitU32 handle_count)
{
    WitChannelMessage message;
    WitU64 result = 0;
    memset(&message, 0, sizeof(message));
    message.Version = WIT_CHANNEL_MESSAGE_VERSION;
    message.Size = sizeof(message);
    message.Data = (WitU64)bytes;
    message.Bytes = count;
    message.Handles = (WitU64)handles;
    message.HandleCount = handle_count;
    const WitU64 status = wit_syscall(WIT_CALL_CHANNEL_SEND, endpoint, (WitU64)&message, sizeof(message), &result);
    return status == WIT_STATUS_OK ? 0 : __wit_errno(status);
}

/* The next message on the session, waiting for it; with a handle slot, the one handle a MAP's reply carries (R4.2). */
static long namespace_receive(void *bytes, WitU32 count, WitU64 *handle)
{
    WitChannelMessage message;
    WitUserWaitRequest wait;
    WitU64 result = 0, status;
    memset(&message, 0, sizeof(message));
    message.Version = WIT_CHANNEL_MESSAGE_VERSION;
    message.Size = sizeof(message);
    message.Data = (WitU64)bytes;
    message.Bytes = count;
    message.Handles = (WitU64)handle;
    message.HandleCount = handle ? 1 : 0;
    wait.Version = WIT_WAIT_OBJECTS_VERSION;
    wait.Size = sizeof(wait);
    wait.Handles = (WitU64)&session;
    wait.Count = 1;
    wait.Flags = 0;
    wait.Deadline = WIT_WAIT_INFINITE;
    while ((status = wit_syscall(WIT_CALL_CHANNEL_RECEIVE, session, (WitU64)&message, sizeof(message), &result)) ==
        WIT_STATUS_TIMED_OUT) {
        const WitU64 waited = wit_syscall(WIT_CALL_OBJECT_WAIT, (WitU64)&wait, sizeof(wait), 0, &result);
        if (waited != WIT_STATUS_OK && waited != WIT_STATUS_INTERRUPTED) {
            return __wit_errno(waited);
        }
    }
    return status == WIT_STATUS_OK ? (long)(result & 0xFFFFFFFFULL) : -EIO;
}

/* One request and its reply, which may carry a handle into the slot given; the reply's errno value as a negative result. */
static long call_with(WitNamespaceRequest *request, WitNamespaceReply *reply, WitU64 *handle)
{
    request->Version = WIT_NAMESPACE_VERSION;
    request->Size = sizeof(*request);
    long status = namespace_send(session, request, sizeof(*request), 0, 0);
    if (status < 0) {
        return status;
    }
    if ((status = namespace_receive(reply, sizeof(*reply), handle)) < 0) {
        return status;
    }
    if (status != (long)sizeof(*reply) ||
        reply->Version != WIT_NAMESPACE_VERSION ||
        reply->Size != sizeof(*reply) ||
        (reply->Operation != request->Operation && reply->Operation != 0)) {
        return -EPROTO;
    }
    return reply->Error ? -(long)reply->Error : 0;
}

/* One request and its reply; the reply's errno value as a negative result. */
static long call(WitNamespaceRequest *request, WitNamespaceReply *reply)
{
    return call_with(request, reply, 0);
}

/* The session and the window, made once; the mount points read with it. A process without the namespace's endpoint
 * looks again at its next path, since the root task's process manager receives the endpoint only when the service
 * answers (D5.3); a connection that failed stays failed. */
static long namespace_connect(void)
{
    if (connected) {
        return connected > 0 ? 0 : -ENOENT;
    }
    if (!__wit_process.Namespace) {
        return -ENOENT;
    }
    connected = -1;
    WitU64 ends[2] = {0, 0}, object = 0, duplicate = 0, result = 0, status;
    if ((status = wit_syscall(WIT_CALL_CHANNEL_CREATE, (WitU64)ends, 0, 0, &result)) != WIT_STATUS_OK) {
        return __wit_errno(status);
    }
    if ((status = wit_syscall(WIT_CALL_MEMORY_OBJECT_CREATE, WINDOW, 0, 0, &object)) == WIT_STATUS_OK) {
        WitMemoryMapRequest map;
        map.Version = WIT_MEMORY_MAP_VERSION;
        map.Size = sizeof(map);
        map.Object = object;
        map.Offset = 0;
        map.Bytes = WINDOW;
        map.Address = 0;
        map.Protection = WIT_MEMORY_READ | WIT_MEMORY_WRITE;
        map.Flags = 0;
        map.Target = WIT_PROCESS_SELF;
        status = wit_syscall(WIT_CALL_MEMORY_OBJECT_MAP, (WitU64)&map, sizeof(map), 0, &result);
        window = status == WIT_STATUS_OK ? (unsigned char *)result : 0;
    }
    if (status == WIT_STATUS_OK) {
        status = wit_syscall(WIT_CALL_HANDLE_DUPLICATE, object, (WitU64)&duplicate,
            WIT_RIGHT_MAP | WIT_RIGHT_WRITE | WIT_RIGHT_TRANSFER, &result);
    }
    close_handle(object); /* the mapping keeps it */
    if (status != WIT_STATUS_OK) {
        close_handle(ends[0]);
        close_handle(ends[1]);
        return __wit_errno(status);
    }
    WitNamespaceRequest request;
    memset(&request, 0, sizeof(request));
    request.Version = WIT_NAMESPACE_VERSION;
    request.Size = sizeof(request);
    request.Operation = WIT_NAMESPACE_CONNECT;
    request.Length = WINDOW;
    WitU64 moved[2] = {ends[1], duplicate};
    long sent = namespace_send(__wit_process.Namespace, &request, sizeof(request), moved, 2);
    if (sent < 0) {
        close_handle(ends[0]);
        close_handle(ends[1]);
        close_handle(duplicate);
        return sent;
    }
    session = ends[0];
    connected = 1;
    WitNamespaceReply reply;
    memset(&request, 0, sizeof(request));
    request.Operation = WIT_NAMESPACE_MOUNTS;
    if (call(&request, &reply) == 0 && reply.Length <= WINDOW) {
        for (WitU64 at = 0; at < reply.Length && mount_count < MOUNTS;) {
            const unsigned char *point = window + at;
            WitU32 length = 0;
            while (at + length < reply.Length && point[length]) {
                ++length;
            }
            if (length > 1 && length < MOUNT_BYTES && point[0] == '/') {
                memcpy(mount_points[mount_count], point + 1, length - 1); /* without its slash, as files.c names */
                mount_lengths[mount_count++] = length - 1;
            }
            at += length + 1;
        }
    }
    return 0;
}

/* The mount table as /etc/mtab (C1.2): one mntent line for the package at / and one for each point the service names,
 * what System.Native reads for DriveInfo.GetDrives() and Environment.GetLogicalDrives() where Linux reads /proc/mounts,
 * and what PowerShell builds its root drive from. A process without the namespace's endpoint sees the package alone.
 * The fields are the device, the point, the type, the options and the dump and pass numbers; the libc knows the points,
 * not the volumes' formats, so the type names the layer that serves them and the options claim nothing. */
WitU32 __wit_mounts_text(unsigned char *buffer, WitU32 capacity)
{
    static const char package[] = "package / witos-package ro 0 0\n";
    static const char volume[] = " witos-volume defaults 0 0\n";
    WitU32 length = 0;
    namespace_connect(); /* reads the mount points when the endpoint is there */
    if (sizeof(package) - 1 <= capacity) {
        memcpy(buffer, package, sizeof(package) - 1);
        length = sizeof(package) - 1;
    }
    for (WitU32 i = 0; i < mount_count; ++i) {
        const WitU32 point = mount_lengths[i]; /* stored without its slash: "<point> /<point> witos-volume ..." */
        if (length + point + 2 + point + sizeof(volume) - 1 > capacity) {
            break;
        }
        memcpy(buffer + length, mount_points[i], point);
        length += point;
        buffer[length++] = ' ';
        buffer[length++] = '/';
        memcpy(buffer + length, mount_points[i], point);
        length += point;
        memcpy(buffer + length, volume, sizeof(volume) - 1);
        length += (WitU32)(sizeof(volume) - 1);
    }
    return length;
}

int __wit_remote_name(const unsigned char *name, WitU32 length)
{
    if (namespace_connect() < 0) {
        return 0;
    }
    for (WitU32 i = 0; i < mount_count; ++i) {
        const WitU32 point = mount_lengths[i];
        if (length >= point && memcmp(name, mount_points[i], point) == 0 && (length == point || name[point] == '/')) {
            return 1;
        }
    }
    return 0;
}

/* A name of files.c as the service's absolute path in the window; its bytes, or -ENAMETOOLONG. */
static long put_path(WitU64 at, const unsigned char *name, WitU32 length)
{
    if (at + length + 2 > WINDOW) {
        return -ENAMETOOLONG;
    }
    window[at] = '/';
    memcpy(window + at + 1, name, length);
    window[at + 1 + length] = 0;
    return (long)length + 1;
}

static long path_call(
    WitU32 operation, WitU32 flags, const unsigned char *name, WitU32 length, WitNamespaceReply *reply)
{
    WitNamespaceRequest request;
    memset(&request, 0, sizeof(request));
    const long bytes = put_path(0, name, length);
    if (bytes < 0) {
        return bytes;
    }
    request.Operation = operation;
    request.Flags = flags;
    request.Length = (WitU64)bytes;
    request.Mode = 0644;
    return call(&request, reply);
}

static long handle_call(WitU32 operation, WitU32 index, WitU64 offset, WitU64 length, WitNamespaceReply *reply)
{
    WitNamespaceRequest request;
    memset(&request, 0, sizeof(request));
    request.Operation = operation;
    request.Handle = remotes[index].Handle;
    request.Offset = offset;
    request.Length = length;
    return call(&request, reply);
}

static void fill(struct kstat *st, const WitNamespaceStat *stat)
{
    memset(st, 0, sizeof(*st));
    st->st_dev = 16 + stat->Volume;
    st->st_ino = (ino_t)stat->Id;
    st->st_mode = (stat->Kind == WIT_NAMESPACE_KIND_DIRECTORY     ? S_IFDIR
                          : stat->Kind == WIT_NAMESPACE_KIND_LINK ? S_IFLNK
                          : stat->Kind == WIT_NAMESPACE_KIND_FILE ? S_IFREG
                                                                  : S_IFCHR) |
        (stat->Mode & 07777);
    st->st_nlink = stat->Links;
    st->st_uid = stat->UserId;
    st->st_gid = stat->GroupId;
    st->st_size = (off_t)stat->Size;
    st->st_blksize = (blksize_t)stat->BlockSize;
    st->st_blocks = (blkcnt_t)stat->Blocks;
    st->st_atime_sec = (long)stat->AccessedSeconds;
    st->st_atime_nsec = stat->AccessedNanoseconds;
    st->st_mtime_sec = (long)stat->ModifiedSeconds;
    st->st_mtime_nsec = stat->ModifiedNanoseconds;
    st->st_ctime_sec = (long)stat->ChangedSeconds;
    st->st_ctime_nsec = stat->ChangedNanoseconds;
}

long __wit_remote_open(const unsigned char *name, WitU32 length, long flags, long mode, WitU32 *index, int *directory)
{
    WitU32 vacant = REMOTES, slot = DIRECTORIES;
    for (WitU32 i = 0; i < REMOTES && vacant == REMOTES; ++i) {
        vacant = remotes[i].Handle ? vacant : i;
    }
    if (vacant == REMOTES) {
        return -ENFILE;
    }
    if ((flags & O_TMPFILE) == O_TMPFILE) {
        return -EOPNOTSUPP;
    }
    const long access = flags & O_ACCMODE;
    WitU32 wanted = (access == O_RDONLY || access == O_RDWR ? WIT_NAMESPACE_READ_ACCESS : 0) |
        (access == O_WRONLY || access == O_RDWR ? WIT_NAMESPACE_WRITE_ACCESS : 0) |
        (flags & O_CREAT ? WIT_NAMESPACE_CREATE : 0) |
        (flags & O_EXCL ? WIT_NAMESPACE_EXCLUSIVE : 0) |
        (flags & O_TRUNC ? WIT_NAMESPACE_TRUNCATE_FILE : 0) |
        (flags & O_DIRECTORY ? WIT_NAMESPACE_DIRECTORY : 0);
    WitNamespaceRequest request;
    WitNamespaceReply reply;
    memset(&request, 0, sizeof(request));
    const long bytes = put_path(0, name, length);
    if (bytes < 0) {
        return bytes;
    }
    request.Operation = WIT_NAMESPACE_OPEN;
    request.Flags = wanted;
    request.Length = (WitU64)bytes;
    request.Mode = (WitU32)(mode & 07777);
    const long status = call(&request, &reply);
    if (status < 0) {
        return status;
    }
    *directory = reply.Stat.Kind == WIT_NAMESPACE_KIND_DIRECTORY;
    if (*directory) {
        for (WitU32 i = 0; i < DIRECTORIES && slot == DIRECTORIES; ++i) {
            slot = directory_lengths[i] ? slot : i;
        }
        if (slot == DIRECTORIES || length == 0) {
            remotes[vacant].Handle = reply.Handle;
            handle_call(WIT_NAMESPACE_CLOSE, vacant, 0, 0, &reply);
            remotes[vacant].Handle = 0;
            return -ENFILE;
        }
        memcpy(directory_names[slot], name, length);
        directory_lengths[slot] = length;
    }
    remotes[vacant].Handle = reply.Handle;
    remotes[vacant].References = 1;
    remotes[vacant].Directory = *directory ? slot + 1 : 0;
    *index = vacant;
    return 0;
}

void __wit_remote_reference(WitU32 index)
{
    remotes[index].References++;
}

void __wit_remote_release(WitU32 index)
{
    if (--remotes[index].References) {
        return;
    }
    WitNamespaceReply reply;
    handle_call(WIT_NAMESPACE_CLOSE, index, 0, 0, &reply);
    if (remotes[index].Directory) {
        directory_lengths[remotes[index].Directory - 1] = 0;
    }
    remotes[index].Handle = 0;
    remotes[index].Directory = 0;
}

WitU32 __wit_remote_directory(WitU32 index, unsigned char *name)
{
    const WitU32 slot = remotes[index].Directory;
    if (!slot) {
        return 0;
    }
    memcpy(name, directory_names[slot - 1], directory_lengths[slot - 1]);
    return directory_lengths[slot - 1];
}

long __wit_remote_read(WitU32 index, WitU64 offset, void *buffer, WitU64 bytes)
{
    WitU64 done = 0;
    while (done < bytes) {
        const WitU64 chunk = bytes - done > WINDOW ? WINDOW : bytes - done;
        WitNamespaceReply reply;
        const long status = handle_call(WIT_NAMESPACE_READ, index, offset + done, chunk, &reply);
        if (status < 0) {
            return done ? (long)done : status;
        }
        if (reply.Length > chunk) {
            return -EPROTO;
        }
        memcpy((unsigned char *)buffer + done, window, reply.Length);
        done += reply.Length;
        if (reply.Length < chunk) {
            break; /* the end of the file */
        }
    }
    return (long)done;
}

long __wit_remote_write(WitU32 index, WitU64 offset, const void *buffer, WitU64 bytes)
{
    WitU64 done = 0;
    while (done < bytes) {
        const WitU64 chunk = bytes - done > WINDOW ? WINDOW : bytes - done;
        WitNamespaceReply reply;
        memcpy(window, (const unsigned char *)buffer + done, chunk);
        const long status = handle_call(WIT_NAMESPACE_WRITE, index, offset + done, chunk, &reply);
        if (status < 0) {
            return done ? (long)done : status;
        }
        done += chunk;
    }
    return (long)done;
}

long __wit_remote_fstat(WitU32 index, struct kstat *st)
{
    WitNamespaceReply reply;
    const long status = handle_call(WIT_NAMESPACE_FSTAT, index, 0, 0, &reply);
    if (status == 0) {
        fill(st, &reply.Stat);
    }
    return status;
}

long __wit_remote_stat(const unsigned char *name, WitU32 length, struct kstat *st)
{
    WitNamespaceReply reply;
    const long status = path_call(WIT_NAMESPACE_STAT, 0, name, length, &reply);
    if (status == 0) {
        fill(st, &reply.Stat);
    }
    return status;
}

/* getdents64 of a remote directory: "." and ".." first, then the service's entries; the cursor is 2 plus the index of
 * the next entry, so a listing goes on where the caller's buffer ended. */
long __wit_remote_list(WitU32 index, WitU64 *cursor, unsigned char *buffer, long bytes)
{
    long filled = 0;
    while (*cursor < 2) {
        const long record = 24;
        if (filled + record > bytes) {
            return filled ? filled : -EINVAL;
        }
        unsigned char *out = buffer + filled;
        memset(out, 0, (unsigned long)record);
        const WitU64 next = *cursor + 1, inode = 1;
        memcpy(out, &inode, 8);
        memcpy(out + 8, &next, 8);
        out[16] = (unsigned char)record;
        out[18] = DT_DIR;
        memcpy(out + 19, *cursor == 0 ? "." : "..", *cursor == 0 ? 1 : 2);
        filled += record;
        *cursor = next;
    }
    for (;;) {
        WitNamespaceReply reply;
        const long status = handle_call(WIT_NAMESPACE_LIST, index, *cursor - 2, 0, &reply);
        if (status < 0) {
            return filled ? filled : status;
        }
        if (reply.Length == 0 || reply.Length > WINDOW) {
            return filled;
        }
        WitU64 at = 0, taken = *cursor - 2;
        while (at + sizeof(WitNamespaceEntry) <= reply.Length) {
            WitNamespaceEntry entry;
            memcpy(&entry, window + at, sizeof(entry));
            if (entry.Bytes < sizeof(entry) ||
                at + entry.Bytes > reply.Length ||
                sizeof(entry) + entry.NameBytes > entry.Bytes) {
                return filled ? filled : -EPROTO;
            }
            const long record = (long)((19 + entry.NameBytes + 1 + 7) & ~7UL);
            if (filled + record > bytes) {
                return filled ? filled : -EINVAL; /* the next call takes this entry again */
            }
            unsigned char *out = buffer + filled;
            memset(out, 0, (unsigned long)record);
            const WitU64 next = taken + 1 + 2;
            memcpy(out, &entry.Id, 8);
            memcpy(out + 8, &next, 8);
            out[16] = (unsigned char)record;
            out[17] = (unsigned char)(record >> 8);
            out[18] = entry.Kind == WIT_NAMESPACE_KIND_DIRECTORY ? DT_DIR
                : entry.Kind == WIT_NAMESPACE_KIND_FILE          ? DT_REG
                : entry.Kind == WIT_NAMESPACE_KIND_LINK          ? DT_LNK
                                                                 : DT_UNKNOWN;
            memcpy(out + 19, window + at + sizeof(entry), entry.NameBytes);
            filled += record;
            at += entry.Bytes;
            ++taken;
            *cursor = taken + 2;
        }
        if (reply.Offset == 0) {
            return filled; /* the listing's end */
        }
    }
}

long __wit_remote_mkdir(const unsigned char *name, WitU32 length)
{
    WitNamespaceReply reply;
    return path_call(WIT_NAMESPACE_MKDIR, 0, name, length, &reply);
}

long __wit_remote_remove(const unsigned char *name, WitU32 length, int directory)
{
    WitNamespaceReply reply;
    return path_call(WIT_NAMESPACE_REMOVE, directory ? WIT_NAMESPACE_DIRECTORY : 0, name, length, &reply);
}

long __wit_remote_rename(const unsigned char *from, WitU32 from_length, const unsigned char *to, WitU32 to_length)
{
    WitNamespaceRequest request;
    WitNamespaceReply reply;
    memset(&request, 0, sizeof(request));
    const long first = put_path(0, from, from_length);
    if (first < 0) {
        return first;
    }
    const long second = put_path((WitU64)first + 1, to, to_length);
    if (second < 0) {
        return second;
    }
    request.Operation = WIT_NAMESPACE_RENAME;
    request.Length = (WitU64)(first + 1 + second + 1);
    return call(&request, &reply);
}

long __wit_remote_truncate(WitU32 index, WitU64 size)
{
    WitNamespaceReply reply;
    return handle_call(WIT_NAMESPACE_TRUNCATE, index, 0, size, &reply);
}

long __wit_remote_sync(WitU32 index)
{
    WitNamespaceReply reply;
    return handle_call(WIT_NAMESPACE_SYNC, index, 0, 0, &reply);
}

/* A chunk of the file as the memory object the service shares (R4.2): MAP, writable when asked, whose reply carries the
 * object; *object becomes the process's handle. */
long __wit_remote_map(WitU32 index, WitU64 offset, int writable, WitU64 *object)
{
    WitNamespaceRequest request;
    WitNamespaceReply reply;
    WitU64 handle = 0;
    memset(&request, 0, sizeof(request));
    request.Operation = WIT_NAMESPACE_MAP;
    request.Flags = writable ? WIT_NAMESPACE_MAP_WRITE : 0;
    request.Handle = remotes[index].Handle;
    request.Offset = offset;
    const long status = call_with(&request, &reply, &handle);
    if (status < 0 || !handle) {
        close_handle(handle);
        return status < 0 ? status : -EPROTO;
    }
    *object = handle;
    return 0;
}

/* chmod of a path of a volume (N4.1): the service keeps what the volume's format records and answers ENOTSUP where it
 * cannot keep the mode (FAT records read-only alone). */
long __wit_remote_chmod(const unsigned char *name, WitU32 length, long mode)
{
    WitNamespaceRequest request;
    WitNamespaceReply reply;
    memset(&request, 0, sizeof(request));
    const long bytes = put_path(0, name, length);
    if (bytes < 0) {
        return bytes;
    }
    request.Operation = WIT_NAMESPACE_CHMOD;
    request.Length = (WitU64)bytes;
    request.Mode = (WitU32)(mode & 07777);
    return call(&request, &reply);
}

/* fchmod of an open file or directory of a volume (N4.1). */
long __wit_remote_fchmod(WitU32 index, long mode)
{
    WitNamespaceRequest request;
    WitNamespaceReply reply;
    memset(&request, 0, sizeof(request));
    request.Operation = WIT_NAMESPACE_FCHMOD;
    request.Handle = remotes[index].Handle;
    request.Mode = (WitU32)(mode & 07777);
    return call(&request, &reply);
}

/* utimensat of a path of a volume (N4.1): the times after the path in the window, the flags saying which are set and
 * which are the service's now; the service keeps what the volume's format records and answers EPERM where it cannot. */
long __wit_remote_utimes(const unsigned char *name, WitU32 length, const WitNamespaceTimes *times, WitU32 flags)
{
    WitNamespaceRequest request;
    WitNamespaceReply reply;
    memset(&request, 0, sizeof(request));
    const long bytes = put_path(0, name, length);
    if (bytes < 0) {
        return bytes;
    }
    const WitU64 at = ((WitU64)bytes + 7) & ~7ULL;
    if (at + sizeof(*times) > WINDOW) {
        return -ENAMETOOLONG;
    }
    memcpy(window + at, times, sizeof(*times));
    request.Operation = WIT_NAMESPACE_UTIMES;
    request.Flags = flags;
    request.Offset = at;
    request.Length = (WitU64)bytes;
    return call(&request, &reply);
}

/* futimens of an open file or directory of a volume (N4.1). */
long __wit_remote_futimes(WitU32 index, const WitNamespaceTimes *times, WitU32 flags)
{
    WitNamespaceRequest request;
    WitNamespaceReply reply;
    memset(&request, 0, sizeof(request));
    memcpy(window, times, sizeof(*times));
    request.Operation = WIT_NAMESPACE_FUTIMES;
    request.Flags = flags;
    request.Handle = remotes[index].Handle;
    return call(&request, &reply);
}

long __wit_remote_readlink(const unsigned char *name, WitU32 length, char *buffer, long size)
{
    WitNamespaceReply reply;
    const long status = path_call(WIT_NAMESPACE_READLINK, 0, name, length, &reply);
    if (status < 0) {
        return status;
    }
    const long bytes = (long)reply.Length < size ? (long)reply.Length : size;
    memcpy(buffer, window, (unsigned long)bytes);
    return bytes;
}
