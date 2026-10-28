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
#include <sys/mount.h>
#include <sys/stat.h>
#include "kstat.h"

/* Files of the volumes the namespace service mounts (plan step D5.1, witos/namespace.h), and the names local sockets bind
 * (W4.1), which the service keeps beside them. A path below one of the
 * service's mount points (/data, /fat, /boot, as it names them) is the service's; every other path stays the
 * package's (files.c). The library connects once, at the first such path: it makes a session channel and a window of
 * its own memory, sends CONNECT on the namespace's endpoint the process started with, and from then on sends each
 * request on the session and waits for its reply, one at a time, holding the session's turn (S8.1): the metadata
 * calls take it under the table lock of __wit_syscall, while a read or a write of a file (files.c's transfer) takes
 * it outside that lock, so that no thread holds the tables while the service's disk answers, which on several
 * processors left every other thread spinning for them; a path, the data and a listing travel in the window. A
 * descriptor of such a file or directory is the index of a remote record, the service's handle; the descriptors that
 * share it count as its references, and the last one closes the handle. */

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
static volatile int turn; /* a request in flight on the session, the window its (S8.1) */

#define FUTEX_WAIT 0
#define FUTEX_WAKE 1
#define FUTEX_PRIVATE 128

/* The session's turn: one request at a time, the window the request's meanwhile. A thread that finds the session busy
 * parks on the word and never spins, since a transfer of files.c holds the turn outside the table lock for as long as
 * the service's disk takes, and the metadata calls hold it under the table lock. */
static void turn_take(void)
{
    while (__atomic_exchange_n(&turn, 1, __ATOMIC_ACQUIRE)) {
        __wit_futex(&turn, FUTEX_WAIT | FUTEX_PRIVATE, 1, 0, 0, 0);
    }
}

static void turn_give(void)
{
    __atomic_store_n(&turn, 0, __ATOMIC_RELEASE);
    __wit_futex(&turn, FUTEX_WAKE | FUTEX_PRIVATE, 1, 0, 0, 0);
}

void __wit_remote_turn_take(void)
{
    turn_take();
}

void __wit_remote_turn_give(void)
{
    turn_give();
}

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

/* One request that moves a handle to the service (W4.1's SOCKET_LISTEN), and its reply: *moved says whether the handle
 * left the process, which it did whenever the request was sent, whatever the reply. */
static long call_moving(WitNamespaceRequest *request, WitNamespaceReply *reply, WitU64 handle, int *moved)
{
    request->Version = WIT_NAMESPACE_VERSION;
    request->Size = sizeof(*request);
    *moved = 0;
    long status = namespace_send(session, request, sizeof(*request), &handle, 1);
    if (status < 0) {
        return status;
    }
    *moved = 1;
    if ((status = namespace_receive(reply, sizeof(*reply), 0)) < 0) {
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
    turn_take();
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
    turn_give();
    return 0;
}

/* A volume's format as Linux names its file system type, what /etc/mtab and DriveInfo.DriveFormat show (E1). */
static const char *fs_type(const char *format)
{
    if (strncmp(format, "FAT", 3) == 0) {
        return "vfat";
    }
    if (strcmp(format, "exFAT") == 0) {
        return "exfat";
    }
    if (strcmp(format, "SMB3") == 0) {
        return "smb3"; /* a share (E2.2), as Linux names a mount of mount -t smb3 */
    }
    return format; /* ext2, ext3, ext4, tmpfs as they are */
}

static WitU32 append(unsigned char *buffer, WitU32 length, WitU32 capacity, const char *text, WitU32 bytes)
{
    if (length + bytes > capacity) {
        return capacity + 1; /* too long: the caller drops the line */
    }
    memcpy(buffer + length, text, bytes);
    return length + bytes;
}

/* The mount table as /etc/mtab (C1.2, E1): one mntent line for the package at / and one for each volume of the
 * namespace's catalog (WIT_NAMESPACE_VOLUMES), read anew at each open, so that a volume that came later is there; what
 * System.Native reads for DriveInfo.GetDrives() and Environment.GetLogicalDrives() where Linux reads /proc/mounts, and
 * what PowerShell builds its root drive from. A process without the namespace's endpoint sees the package alone. The
 * fields are the volume's alias as the device, the point, the type as Linux names it, ro or rw, and the dump and pass
 * numbers. */
WitU32 __wit_mounts_text(unsigned char *buffer, WitU32 capacity)
{
    static const char package[] = "package / witos-package ro 0 0\n";
    WitU32 length = 0;
    if (sizeof(package) - 1 <= capacity) {
        memcpy(buffer, package, sizeof(package) - 1);
        length = sizeof(package) - 1;
    }
    if (namespace_connect() < 0 || !connected) {
        return length;
    }
    WitNamespaceRequest request;
    WitNamespaceReply reply;
    memset(&request, 0, sizeof(request));
    request.Operation = WIT_NAMESPACE_VOLUMES;
    turn_take();
    if (call(&request, &reply) == 0 && reply.Length <= WINDOW) {
        for (WitU64 at = 0; at + sizeof(WitNamespaceVolume) <= reply.Length; at += sizeof(WitNamespaceVolume)) {
            WitNamespaceVolume volume;
            memcpy(&volume, window + at, sizeof(volume));
            volume.Alias[sizeof(volume.Alias) - 1] = 0;
            volume.Point[sizeof(volume.Point) - 1] = 0;
            volume.Format[sizeof(volume.Format) - 1] = 0;
            const char *type = fs_type(volume.Format);
            const char *options = (volume.Flags & WIT_NAMESPACE_VOLUME_READ_ONLY) ? " ro 0 0\n" : " rw 0 0\n";
            WitU32 line = append(buffer, length, capacity, volume.Alias, (WitU32)strlen(volume.Alias));
            line = append(buffer, line, capacity, " ", 1);
            line = append(buffer, line, capacity, volume.Point, (WitU32)strlen(volume.Point));
            line = append(buffer, line, capacity, " ", 1);
            line = append(buffer, line, capacity, type, (WitU32)strlen(type));
            line = append(buffer, line, capacity, options, 8);
            if (line > capacity) {
                break;
            }
            length = line;
        }
    }
    turn_give();
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
    st->st_mode = (stat->Kind == WIT_NAMESPACE_KIND_DIRECTORY       ? S_IFDIR
                          : stat->Kind == WIT_NAMESPACE_KIND_LINK   ? S_IFLNK
                          : stat->Kind == WIT_NAMESPACE_KIND_FILE   ? S_IFREG
                          : stat->Kind == WIT_NAMESPACE_KIND_SOCKET ? S_IFSOCK /* a local socket's name (W4.1) */
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

static long open_in_turn(const unsigned char *name, WitU32 length, long flags, long mode, WitU32 *index, int *directory)
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
    turn_take();
    handle_call(WIT_NAMESPACE_CLOSE, index, 0, 0, &reply);
    turn_give();
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

long __wit_remote_read_in_turn(WitU32 index, WitU64 offset, void *buffer, WitU64 bytes)
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

long __wit_remote_write_in_turn(WitU32 index, WitU64 offset, const void *buffer, WitU64 bytes)
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

long __wit_remote_fstat_in_turn(WitU32 index, struct kstat *st)
{
    WitNamespaceReply reply;
    const long status = handle_call(WIT_NAMESPACE_FSTAT, index, 0, 0, &reply);
    if (status == 0) {
        fill(st, &reply.Stat);
    }
    return status;
}

static long stat_in_turn(const unsigned char *name, WitU32 length, struct kstat *st)
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
static long list_in_turn(WitU32 index, WitU64 *cursor, unsigned char *buffer, long bytes)
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
                : entry.Kind == WIT_NAMESPACE_KIND_SOCKET        ? DT_SOCK
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

static long mkdir_in_turn(const unsigned char *name, WitU32 length)
{
    WitNamespaceReply reply;
    return path_call(WIT_NAMESPACE_MKDIR, 0, name, length, &reply);
}

static long remove_in_turn(const unsigned char *name, WitU32 length, int directory)
{
    WitNamespaceReply reply;
    return path_call(WIT_NAMESPACE_REMOVE, directory ? WIT_NAMESPACE_DIRECTORY : 0, name, length, &reply);
}

static long rename_in_turn(const unsigned char *from, WitU32 from_length, const unsigned char *to, WitU32 to_length)
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

static long truncate_in_turn(WitU32 index, WitU64 size)
{
    WitNamespaceReply reply;
    return handle_call(WIT_NAMESPACE_TRUNCATE, index, 0, size, &reply);
}

static long sync_in_turn(WitU32 index)
{
    WitNamespaceReply reply;
    return handle_call(WIT_NAMESPACE_SYNC, index, 0, 0, &reply);
}

/* A chunk of the file as the memory object the service shares (R4.2): MAP, writable when asked, whose reply carries the
 * object; *object becomes the process's handle. */
static long map_in_turn(WitU32 index, WitU64 offset, int writable, WitU64 *object)
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
static long chmod_in_turn(const unsigned char *name, WitU32 length, long mode)
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
static long fchmod_in_turn(WitU32 index, long mode)
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
static long utimes_in_turn(const unsigned char *name, WitU32 length, const WitNamespaceTimes *times, WitU32 flags)
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
static long futimes_in_turn(WitU32 index, const WitNamespaceTimes *times, WitU32 flags)
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

static long readlink_in_turn(const unsigned char *name, WitU32 length, char *buffer, long size)
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

/* The public forms: each takes the session's turn around its request (S8.1). */
long __wit_remote_open(const unsigned char *name, WitU32 length, long flags, long mode, WitU32 *index, int *directory)
{
    turn_take();
    const long r = open_in_turn(name, length, flags, mode, index, directory);
    turn_give();
    return r;
}

long __wit_remote_read(WitU32 index, WitU64 offset, void *buffer, WitU64 bytes)
{
    turn_take();
    const long r = __wit_remote_read_in_turn(index, offset, buffer, bytes);
    turn_give();
    return r;
}

long __wit_remote_write(WitU32 index, WitU64 offset, const void *buffer, WitU64 bytes)
{
    turn_take();
    const long r = __wit_remote_write_in_turn(index, offset, buffer, bytes);
    turn_give();
    return r;
}

long __wit_remote_fstat(WitU32 index, struct kstat *st)
{
    turn_take();
    const long r = __wit_remote_fstat_in_turn(index, st);
    turn_give();
    return r;
}

long __wit_remote_stat(const unsigned char *name, WitU32 length, struct kstat *st)
{
    turn_take();
    const long r = stat_in_turn(name, length, st);
    turn_give();
    return r;
}

long __wit_remote_list(WitU32 index, WitU64 *cursor, unsigned char *buffer, long bytes)
{
    turn_take();
    const long r = list_in_turn(index, cursor, buffer, bytes);
    turn_give();
    return r;
}

long __wit_remote_mkdir(const unsigned char *name, WitU32 length)
{
    turn_take();
    const long r = mkdir_in_turn(name, length);
    turn_give();
    return r;
}

long __wit_remote_remove(const unsigned char *name, WitU32 length, int directory)
{
    turn_take();
    const long r = remove_in_turn(name, length, directory);
    turn_give();
    return r;
}

long __wit_remote_rename(const unsigned char *from, WitU32 from_length, const unsigned char *to, WitU32 to_length)
{
    turn_take();
    const long r = rename_in_turn(from, from_length, to, to_length);
    turn_give();
    return r;
}

long __wit_remote_truncate(WitU32 index, WitU64 size)
{
    turn_take();
    const long r = truncate_in_turn(index, size);
    turn_give();
    return r;
}

long __wit_remote_sync(WitU32 index)
{
    turn_take();
    const long r = sync_in_turn(index);
    turn_give();
    return r;
}

long __wit_remote_map(WitU32 index, WitU64 offset, int writable, WitU64 *object)
{
    turn_take();
    const long r = map_in_turn(index, offset, writable, object);
    turn_give();
    return r;
}

long __wit_remote_chmod(const unsigned char *name, WitU32 length, long mode)
{
    turn_take();
    const long r = chmod_in_turn(name, length, mode);
    turn_give();
    return r;
}

long __wit_remote_fchmod(WitU32 index, long mode)
{
    turn_take();
    const long r = fchmod_in_turn(index, mode);
    turn_give();
    return r;
}

long __wit_remote_utimes(const unsigned char *name, WitU32 length, const WitNamespaceTimes *times, WitU32 flags)
{
    turn_take();
    const long r = utimes_in_turn(name, length, times, flags);
    turn_give();
    return r;
}

long __wit_remote_futimes(WitU32 index, const WitNamespaceTimes *times, WitU32 flags)
{
    turn_take();
    const long r = futimes_in_turn(index, times, flags);
    turn_give();
    return r;
}

/* The names of local sockets (W4.1, unix.c): a path of files.c's form, or an abstract name's bytes, into the window. */
static long put_socket_name(const unsigned char *name, WitU32 length, int abstract)
{
    if (!abstract) {
        return put_path(0, name, length);
    }
    if (length > 107) {
        return -EINVAL; /* what sun_path holds after its zero */
    }
    memcpy(window, name, length);
    return (long)length;
}

static long socket_name_call(WitU32 operation, const unsigned char *name, WitU32 length, int abstract, int type,
    WitNamespaceReply *reply, WitU64 *handle)
{
    WitNamespaceRequest request;
    memset(&request, 0, sizeof(request));
    const long bytes = put_socket_name(name, length, abstract);
    if (bytes < 0) {
        return bytes;
    }
    request.Operation = operation;
    request.Flags = abstract ? WIT_NAMESPACE_ABSTRACT : 0;
    request.Length = (WitU64)bytes;
    request.Mode = (WitU32)type;
    return call_with(&request, reply, handle);
}

/* A local socket's name bound: *id names the binding to the service from now on. A process without the namespace has
 * no names (ENOENT). */
long __wit_remote_socket_bind(const unsigned char *name, WitU32 length, int abstract, int type, WitU64 *id)
{
    WitNamespaceReply reply;
    if (namespace_connect() < 0) {
        return -ENOENT;
    }
    turn_take();
    const long status = socket_name_call(WIT_NAMESPACE_SOCKET_BIND, name, length, abstract, type, &reply, 0);
    turn_give();
    if (status == 0) {
        *id = reply.Handle;
    }
    return status;
}

/* A bound socket listens: the endpoint its connections arrive on goes to the service, which keeps it. *moved says
 * whether it left the process. */
long __wit_remote_socket_listen(WitU64 id, WitU64 endpoint, int *moved)
{
    WitNamespaceRequest request;
    WitNamespaceReply reply;
    *moved = 0;
    if (namespace_connect() < 0) {
        return -ENOENT;
    }
    memset(&request, 0, sizeof(request));
    request.Operation = WIT_NAMESPACE_SOCKET_LISTEN;
    request.Handle = id;
    turn_take();
    const long status = call_moving(&request, &reply, endpoint, moved);
    turn_give();
    return status;
}

/* The endpoint a connection to a name is sent on: a duplicate of its listener's, SEND and TRANSFER. */
long __wit_remote_socket_connect(const unsigned char *name, WitU32 length, int abstract, int type, WitU64 *endpoint)
{
    WitNamespaceReply reply;
    WitU64 handle = 0;
    if (namespace_connect() < 0) {
        return -ENOENT;
    }
    turn_take();
    const long status = socket_name_call(WIT_NAMESPACE_SOCKET_CONNECT, name, length, abstract, type, &reply, &handle);
    turn_give();
    if (status < 0 || !handle) {
        close_handle(handle);
        return status < 0 ? status : -EPROTO;
    }
    *endpoint = handle;
    return 0;
}

/* A bound socket closed: nobody listens on its name any more; an abstract name goes. */
long __wit_remote_socket_close(WitU64 id)
{
    WitNamespaceRequest request;
    WitNamespaceReply reply;
    if (namespace_connect() < 0) {
        return -ENOENT;
    }
    memset(&request, 0, sizeof(request));
    request.Operation = WIT_NAMESPACE_SOCKET_CLOSE;
    request.Handle = id;
    turn_take();
    const long status = call(&request, &reply);
    turn_give();
    return status;
}

long __wit_remote_readlink(const unsigned char *name, WitU32 length, char *buffer, long size)
{
    turn_take();
    const long r = readlink_in_turn(name, length, buffer, size);
    turn_give();
    return r;
}

/* The volume a path or an open file of a volume lies on (E1): STATFS's WitNamespaceVolume with its space. */
static long volume_in_turn(
    const unsigned char *name, WitU32 length, int by_index, WitU32 index, WitNamespaceVolume *volume)
{
    WitNamespaceReply reply;
    const long status = by_index ? handle_call(WIT_NAMESPACE_STATFS, index, 0, 0, &reply)
                                 : path_call(WIT_NAMESPACE_STATFS, 0, name, length, &reply);
    if (status < 0) {
        return status;
    }
    if (reply.Length < sizeof(*volume)) {
        return -EPROTO;
    }
    memcpy(volume, window, sizeof(*volume));
    if (volume->Version != WIT_NAMESPACE_VERSION || volume->Size != sizeof(*volume)) {
        return -EPROTO;
    }
    volume->Format[sizeof(volume->Format) - 1] = 0;
    return 0;
}

long __wit_remote_volume(const unsigned char *name, WitU32 length, WitNamespaceVolume *volume)
{
    turn_take();
    const long r = volume_in_turn(name, length, 0, 0, volume);
    turn_give();
    return r;
}

long __wit_remote_fvolume(WitU32 index, WitNamespaceVolume *volume)
{
    turn_take();
    const long r = volume_in_turn(0, 0, 1, index, volume);
    turn_give();
    return r;
}

/* mount(2) of a share (E2.2): the source, the target (a normalized name, or none), the type and the options into the
 * window, each zero-terminated, as the service's MOUNT takes them. Linux's flags beyond MS_RDONLY, the nosuid, nodev,
 * noexec and silent ones apart, ask what the service does not do (a remount, a bind, a move) and are EINVAL; the magic
 * old callers put in the high half is dropped. A process without the namespace's endpoint mounts nothing (ENODEV). */
long __wit_remote_mount(const char *source, const unsigned char *target, WitU32 target_length, const char *type,
    unsigned long flags, const char *data)
{
    if ((flags & 0xFFFF0000UL) == 0xC0ED0000UL) { /* MS_MGC_VAL */
        flags &= 0xFFFFUL;
    }
    if (flags & ~(unsigned long)(MS_RDONLY | MS_NOSUID | MS_NODEV | MS_NOEXEC | MS_SILENT)) {
        return -EINVAL;
    }
    if (!source || !type) {
        return -EINVAL;
    }
    if (namespace_connect() < 0 || connected <= 0) {
        return -ENODEV;
    }
    const size_t source_bytes = strlen(source), type_bytes = strlen(type), data_bytes = data ? strlen(data) : 0;
    const size_t target_bytes = target_length ? (size_t)target_length + 1 : 0;
    const size_t total = source_bytes + 1 + target_bytes + 1 + type_bytes + 1 + data_bytes + 1;
    if (total > WINDOW) {
        return -EINVAL;
    }
    WitNamespaceRequest request;
    WitNamespaceReply reply;
    memset(&request, 0, sizeof(request));
    turn_take();
    size_t at = 0;
    memcpy(window + at, source, source_bytes + 1);
    at += source_bytes + 1;
    if (target_length) {
        window[at++] = '/';
        memcpy(window + at, target, target_length);
        at += target_length;
    }
    window[at++] = 0;
    memcpy(window + at, type, type_bytes + 1);
    at += type_bytes + 1;
    if (data_bytes) {
        memcpy(window + at, data, data_bytes);
    }
    window[at + data_bytes] = 0;
    request.Operation = WIT_NAMESPACE_MOUNT;
    request.Flags = (flags & MS_RDONLY) ? WIT_NAMESPACE_MOUNT_READ_ONLY : 0;
    request.Length = (WitU64)total;
    const long r = call(&request, &reply);
    turn_give();
    return r;
}

/* umount2(2) of a share (E2.2): MNT_DETACH leaves files of it open to fail; MNT_FORCE is taken as a plain unmount,
 * MNT_EXPIRE is EAGAIN as Linux answers its first call. */
long __wit_remote_unmount(const unsigned char *name, WitU32 length, long flags)
{
    if (flags & ~(long)(MNT_FORCE | MNT_DETACH | MNT_EXPIRE | UMOUNT_NOFOLLOW)) {
        return -EINVAL;
    }
    if (flags & MNT_EXPIRE) {
        return -EAGAIN;
    }
    if (namespace_connect() < 0 || connected <= 0 || !__wit_remote_name(name, length)) {
        return -EINVAL; /* no mount point of the service */
    }
    WitNamespaceReply reply;
    turn_take();
    const long r =
        path_call(WIT_NAMESPACE_UNMOUNT, (flags & MNT_DETACH) ? WIT_NAMESPACE_UNMOUNT_DETACH : 0, name, length, &reply);
    turn_give();
    return r;
}
