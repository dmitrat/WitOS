#ifndef WITOS_NAMESPACE_H
#define WITOS_NAMESPACE_H
#include "witos/types.h"

/* The namespace service's protocol (plan step D5.1, storage document sections 21 and 53-57, RFC 0008 sections 12-17).
 * The namespace service /system/Namespace mounts the volumes the device manager hands it and serves their files. Its
 * clients hold a duplicate of one endpoint with SEND alone, which the process manager gives every process it starts
 * (witos/start.h, WIT_START_NAMESPACE); a client connects once: it sends WIT_NAMESPACE_CONNECT on that endpoint with
 * the end of a session channel it made and a duplicate of a memory object of its own, the window (MAP, WRITE,
 * TRANSFER), and from then on sends requests on the session and receives each reply there, one at a time. Paths,
 * data and listings travel in the window; a path is absolute in the namespace, below a mount point the service names
 * (WIT_NAMESPACE_MOUNTS). An error is an errno value. The device manager starts the service with the grant of a
 * channel to itself, moves each disk's block channel to it in a WitNamespaceDisk message, and the service answers
 * with a WitServiceStatus (witos/manager.h) whose one handle is the clients' endpoint (SEND, DUPLICATE, TRANSFER).
 * Nothing of the protocol is an application API: applications see System.IO through the libc. */
#define WIT_NAMESPACE_VERSION 1U
#define WIT_NAMESPACE_PATH "/system/Namespace"

/* Operations. */
#define WIT_NAMESPACE_CONNECT 1U /* on the clients' endpoint: the session's end and the window in the handle slots */
#define WIT_NAMESPACE_MOUNTS 2U /* the mount points into the window, each zero-terminated; Length their bytes */
#define WIT_NAMESPACE_OPEN 3U /* the path in the window (Length bytes), Flags, Mode; the reply's Handle and Stat */
#define WIT_NAMESPACE_CLOSE 4U /* Handle */
#define WIT_NAMESPACE_READ 5U /* Handle, Offset, Length: the bytes into the window; the reply's Length those read */
#define WIT_NAMESPACE_WRITE 6U /* Handle, Offset, Length bytes of the window; the reply's Stat after the write */
#define WIT_NAMESPACE_STAT 7U /* the path in the window (Length bytes); the reply's Stat; Flags 0 */
#define WIT_NAMESPACE_FSTAT 8U /* Handle; the reply's Stat */
#define WIT_NAMESPACE_LIST \
    9U /* Handle of a directory, Offset the first entry: WitNamespaceEntry records into the window,
                               * the reply's Length their bytes and Offset the next entry, zero at the end */
#define WIT_NAMESPACE_MKDIR 10U /* the path in the window (Length bytes) */
#define WIT_NAMESPACE_REMOVE 11U /* the path in the window (Length bytes); Flags WIT_NAMESPACE_DIRECTORY for rmdir */
#define WIT_NAMESPACE_RENAME 12U /* two zero-terminated paths in the window, the old then the new; Length their bytes */
#define WIT_NAMESPACE_TRUNCATE 13U /* Handle, Length: the new size; the reply's Stat */
#define WIT_NAMESPACE_SYNC 14U /* Handle: its file durable */
#define WIT_NAMESPACE_READLINK 15U /* the path in the window (Length bytes): the target into the window, Length bytes */
#define WIT_NAMESPACE_MAP \
    16U /* Handle of a file, Offset a multiple of WIT_NAMESPACE_CHUNK_BYTES, Flags WIT_NAMESPACE_MAP_WRITE: the reply's
         * one handle is a memory object of WIT_NAMESPACE_CHUNK_BYTES that holds the file's bytes from Offset (R4.2) */
#define WIT_NAMESPACE_CHMOD \
    17U /* the path in the window (Length bytes), Mode the permission bits; the reply's Stat (N4.1) */
#define WIT_NAMESPACE_FCHMOD 18U /* Handle, Mode the permission bits; the reply's Stat (N4.1) */

/* OPEN's flags; the libc maps O_ACCMODE, O_CREAT, O_EXCL, O_TRUNC and O_DIRECTORY onto them. */
#define WIT_NAMESPACE_READ_ACCESS 0x1U
#define WIT_NAMESPACE_WRITE_ACCESS 0x2U
#define WIT_NAMESPACE_CREATE 0x4U
#define WIT_NAMESPACE_EXCLUSIVE 0x8U
#define WIT_NAMESPACE_TRUNCATE_FILE 0x10U
#define WIT_NAMESPACE_DIRECTORY 0x20U /* OPEN: the path must be a directory; REMOVE: remove a directory */
#define WIT_NAMESPACE_MAP_WRITE 0x40U /* MAP: the object is writable, which a handle opened for writing alone grants */

/* WitNamespaceStat's Kind, as a stat's file type; Mode holds the permission bits alone. */
#define WIT_NAMESPACE_KIND_FILE 1U
#define WIT_NAMESPACE_KIND_DIRECTORY 2U
#define WIT_NAMESPACE_KIND_LINK 3U
#define WIT_NAMESPACE_KIND_OTHER 4U

#define WIT_NAMESPACE_WINDOW_BYTES (16U * 4096U) /* the window a client attaches */

/* A file's shared mapping (R4.2): the service shares a file's bytes as memory objects of WIT_NAMESPACE_CHUNK_BYTES, one
 * per chunk of the file a client maps, the same object for every client that maps the same chunk of the same file, so
 * that every shared mapping of it shows the same pages. The service's own reads and writes of the file go through the
 * objects while they live, so a read sees what a mapping wrote; it writes the objects' changed blocks back to the file
 * at SYNC and when the last handle that mapped the file writable closes, and drops the objects when the last handle
 * that mapped the file closes. Bytes past the file's end are zero and never reach the file. */
#define WIT_NAMESPACE_CHUNK_BYTES (64U * 4096U)

/* What a stat reports of an entry: times as seconds and nanoseconds since 1970 in UTC. */
typedef struct WitNamespaceStat {
    WitU32 Kind, Mode; /* WIT_NAMESPACE_KIND_*; the permission bits (07777) */
    WitU64 Size; /* the bytes of a file or a link */
    WitU64 Id; /* the entry's number in its volume */
    WitU32 Links, Volume; /* the hard links; the volume's index in the mount list */
    WitU32 UserId, GroupId;
    WitU32 BlockSize, Reserved; /* the volume's allocation unit */
    WitU64 Blocks; /* the 512-byte blocks the entry takes */
    WitU64 AccessedSeconds, ModifiedSeconds, ChangedSeconds;
    WitU32 AccessedNanoseconds, ModifiedNanoseconds, ChangedNanoseconds, Padding;
} WitNamespaceStat;

/* A request on the session, and the CONNECT on the clients' endpoint. */
typedef struct WitNamespaceRequest {
    WitU32 Version, Size; /* WIT_NAMESPACE_VERSION, the size of this structure */
    WitU32 Operation, Flags;
    WitU64 Handle; /* a file or a directory the session opened */
    WitU64 Offset;
    WitU64 Length;
    WitU32 Mode, Reserved; /* OPEN with CREATE: the permission bits of a new file; CHMOD and FCHMOD: the bits to set */
} WitNamespaceRequest;

/* A reply: zero or an errno value, and what the operation gives back. */
typedef struct WitNamespaceReply {
    WitU32 Version, Size; /* WIT_NAMESPACE_VERSION, the size of this structure */
    WitU32 Operation, Error;
    WitU64 Handle;
    WitU64 Offset;
    WitU64 Length;
    WitNamespaceStat Stat;
} WitNamespaceReply;

/* An entry of a listing in the window, 8-byte aligned: Bytes is the record's whole length. */
typedef struct WitNamespaceEntry {
    WitU64 Id;
    WitU32 Kind; /* WIT_NAMESPACE_KIND_* */
    WitU16 Bytes, NameBytes; /* the name follows, not terminated */
} WitNamespaceEntry;

/* A disk the device manager moves to the service on its granted channel, its block channel in the one handle slot. */
typedef struct WitNamespaceDisk {
    WitU32 Version, Size; /* WIT_NAMESPACE_VERSION, the size of this structure */
    WitU32 Flags, Reserved; /* WIT_NAMESPACE_DISK_READ_ONLY */
    WitU8 Device[64]; /* the device's identity (a UHI device id), zero-terminated */
} WitNamespaceDisk;

#define WIT_NAMESPACE_DISK_READ_ONLY 0x1U

WIT_STATIC_ASSERT(sizeof(WitNamespaceStat) == 96, "Namespace stat");
WIT_STATIC_ASSERT(sizeof(WitNamespaceRequest) == 48, "Namespace request");
WIT_STATIC_ASSERT(sizeof(WitNamespaceReply) == 136, "Namespace reply");
WIT_STATIC_ASSERT(sizeof(WitNamespaceEntry) == 16, "Namespace entry");
WIT_STATIC_ASSERT(sizeof(WitNamespaceDisk) == 80, "Namespace disk");
#endif
