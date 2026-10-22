#define _GNU_SOURCE
#include "witos_libc.h"
#include "witos/socket.h"
#include <errno.h>
#include <fcntl.h>
#include <net/if.h>
#include <netinet/in.h>
#include <poll.h>
#include <stddef.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/socket.h>
#include <sys/syscall.h>
#include <sys/uio.h>

/* Netlink route sockets and the interface ioctls (plan step W4.2): what musl's getifaddrs and if_nameindex (dumps of
 * RTM_GETLINK and RTM_GETADDR), if_nametoindex and if_indextoname (SIOCGIFINDEX, SIOCGIFNAME), System.Native's
 * interface enumeration (SIOCGIFMTU on any socket) and .NET's NetworkChange (a socket bound to the route groups, read
 * for RTM_NEWADDR and the rest) ask of Linux's kernel, answered by the libc from the network service's configuration
 * (socket.c, WIT_SOCKET_CONFIGURATION): the loopback, index 1, and the adapter the service runs its stack on, eth0,
 * index 2, with the lease's address, mask and gateway, the adapter's address and MTU. The kernel knows no network; a
 * process without the service, or one whose service has no adapter, has the loopback alone.
 *
 * A netlink socket is a record with a queue of the messages a request's answers and the events make, read in whole
 * messages as Linux delivers them (one a buffer cannot take is cut, MSG_TRUNC); a dump's answers are queued by the
 * request itself, so a reader finds them at once; a reader of an empty queue parks on the record's sequence word, which
 * every enqueue bumps and which epoll's edge is. The events: when the service's configuration changes, socket.c's
 * events thread tells the libc (__wit_netlink_notify), and the address that went, the one that came and the link are
 * told to every socket that bound the groups, as the kernel's route multicast does. Linux's structures and numbers are
 * written here as the ABI fixes them, since musl ships no <linux/netlink.h>. */

#define NETLINKS 4U
#define QUEUE_BYTES \
    4096U /* a dump of the two interfaces takes some 600 bytes; the libc's tables are the root task's bounded segment */
#define REQUEST_BYTES 4096U
#define LOOPBACK_MTU 65536U
#define FUTEX_WAKE 1
#define FUTEX_PRIVATE 128

/* Linux's netlink ABI: <linux/netlink.h>, <linux/rtnetlink.h>, <linux/if_link.h>, <linux/if_addr.h>, <linux/if_arp.h>. */
#define NLMSG_ALIGNTO 4U
#define NLMSG_ALIGN(n) (((n) + NLMSG_ALIGNTO - 1U) & ~(NLMSG_ALIGNTO - 1U))
#define NLMSG_ERROR 2
#define NLMSG_DONE 3
#define NLM_F_REQUEST 1
#define NLM_F_MULTI 2
#define NLM_F_ACK 4
#define NLM_F_DUMP 0x300
#define RTM_NEWLINK 16
#define RTM_GETLINK 18
#define RTM_NEWADDR 20
#define RTM_DELADDR 21
#define RTM_GETADDR 22
#define RTM_NEWROUTE 24
#define RTM_GETROUTE 26
#define IFLA_ADDRESS 1
#define IFLA_BROADCAST 2
#define IFLA_IFNAME 3
#define IFLA_MTU 4
#define IFA_ADDRESS 1
#define IFA_LOCAL 2
#define IFA_LABEL 3
#define IFA_BROADCAST 4
#define IFA_F_PERMANENT 0x80
#define RT_SCOPE_UNIVERSE 0
#define RT_SCOPE_LINK 253
#define RT_SCOPE_HOST 254
#define RT_TABLE_MAIN 254
#define RTPROT_KERNEL 2
#define RTPROT_DHCP 16
#define RTN_UNICAST 1
#define RTA_DST 1
#define RTA_OIF 4
#define RTA_GATEWAY 5
#define RTA_PREFSRC 7
#define RTA_TABLE 15
#define ARPHRD_ETHER 1
#define ARPHRD_LOOPBACK 772
#define NETLINK_ROUTE 0
#ifndef SOL_NETLINK
#define SOL_NETLINK 270
#endif
#define NETLINK_ADD_MEMBERSHIP 1
#define NETLINK_DROP_MEMBERSHIP 2
#define RTMGRP_LINK 1U
#define RTMGRP_IPV4_IFADDR 0x10U
#define RTMGRP_IPV4_ROUTE 0x40U

struct nlmsghdr {
    WitU32 nlmsg_len;
    WitU16 nlmsg_type;
    WitU16 nlmsg_flags;
    WitU32 nlmsg_seq;
    WitU32 nlmsg_pid;
};

struct nlmsgerr {
    int error;
    struct nlmsghdr msg;
};

struct sockaddr_nl {
    WitU16 nl_family;
    WitU16 nl_pad;
    WitU32 nl_pid;
    WitU32 nl_groups;
};

struct ifinfomsg {
    unsigned char ifi_family;
    unsigned char ifi_pad;
    WitU16 ifi_type;
    int ifi_index;
    unsigned ifi_flags;
    unsigned ifi_change;
};

struct ifaddrmsg {
    WitU8 ifa_family;
    WitU8 ifa_prefixlen;
    WitU8 ifa_flags;
    WitU8 ifa_scope;
    WitU32 ifa_index;
};

struct rtmsg {
    WitU8 rtm_family;
    WitU8 rtm_dst_len;
    WitU8 rtm_src_len;
    WitU8 rtm_tos;
    WitU8 rtm_table;
    WitU8 rtm_protocol;
    WitU8 rtm_scope;
    WitU8 rtm_type;
    unsigned rtm_flags;
};

struct rtattr {
    WitU16 rta_len;
    WitU16 rta_type;
};

typedef struct Netlink {
    WitU32 Id; /* the generation: epoll tells a registration's record from the one that took its place */
    WitU32 References; /* descriptors and calls in flight; zero when free */
    int Used;
    WitU32 Groups; /* the multicast groups bound (bind's nl_groups, NETLINK_ADD_MEMBERSHIP) */
    WitU32 Port; /* nl_pid: the process is 1, each socket beyond the first takes a number of its own, as Linux does */
    volatile int Sequence; /* bumped at every enqueue: the reader's word and epoll's edge */
    WitU32 Head, Tail; /* the queued messages: Queue[Head, Tail), each a whole message of NLMSG_ALIGN(nlmsg_len) */
    unsigned char Queue[QUEUE_BYTES];
} Netlink;

/* An interface as the answers describe it. */
typedef struct Interface {
    WitU32 Index;
    char Name[IFNAMSIZ];
    WitU16 Type; /* ARPHRD_* */
    unsigned Flags; /* IFF_* */
    WitU32 Mtu;
    unsigned char Mac[6];
    int Addressed; /* whether it has an IPv4 address */
    WitU8 Prefix;
    WitU8 Scope;
    unsigned char Address[4], Mask[4], Broadcast[4], Gateway[4];
} Interface;

/* A writer of messages into a buffer: nothing is written past the end, and Overflow says so. */
typedef struct Writer {
    unsigned char *Buffer;
    WitU32 Capacity, Used;
    int Overflow;
} Writer;

static Netlink netlinks[NETLINKS];
static volatile int netlink_lock;

static unsigned char popcount(const unsigned char mask[4])
{
    unsigned count = 0;
    for (int i = 0; i < 4; ++i) {
        for (unsigned bits = mask[i]; bits; bits &= bits - 1U) {
            ++count;
        }
    }
    return (unsigned char)count;
}

static void loopback(Interface *lo)
{
    memset(lo, 0, sizeof(*lo));
    lo->Index = 1;
    memcpy(lo->Name, "lo", 3);
    lo->Type = ARPHRD_LOOPBACK;
    lo->Flags = IFF_UP | IFF_LOOPBACK | IFF_RUNNING | IFF_LOWER_UP;
    lo->Mtu = LOOPBACK_MTU;
    lo->Addressed = 1;
    lo->Prefix = 8;
    lo->Scope = RT_SCOPE_HOST;
    lo->Address[0] = 127;
    lo->Address[3] = 1;
    lo->Mask[0] = 255;
}

/* The adapter from the service's configuration: eth0, with the lease's addresses once there is one. */
static void adapter(Interface *eth, const WitSocketConfiguration *c)
{
    memset(eth, 0, sizeof(*eth));
    eth->Index = 2;
    memcpy(eth->Name, "eth0", 5);
    eth->Type = ARPHRD_ETHER;
    eth->Flags = IFF_UP | IFF_BROADCAST | IFF_RUNNING | IFF_MULTICAST | IFF_LOWER_UP;
    eth->Mtu = c->Mtu;
    for (int i = 0; i < 6; ++i) {
        eth->Mac[i] = (unsigned char)(c->Mac >> (8 * i));
    }
    if (c->Leased) {
        eth->Addressed = 1;
        memcpy(eth->Address, c->Address, 4);
        memcpy(eth->Mask, c->Mask, 4);
        memcpy(eth->Gateway, c->Gateway, 4);
        for (int i = 0; i < 4; ++i) {
            eth->Broadcast[i] = (unsigned char)(c->Address[i] | (unsigned char)~c->Mask[i]);
        }
        eth->Prefix = popcount(c->Mask);
        eth->Scope = RT_SCOPE_UNIVERSE;
    }
}

/* The interfaces as the configuration gives them now: the loopback, then the adapter when the process has the service
 * and the service an adapter (its address is not zero; the stack of a board without one runs on no link). */
static int interfaces(Interface list[2])
{
    WitSocketConfiguration c;
    loopback(&list[0]);
    if (__wit_socket_configuration(&c) < 0 || c.Mac == 0) {
        return 1;
    }
    adapter(&list[1], &c);
    return 2;
}

static void *reserve(Writer *w, WitU32 bytes)
{
    const WitU32 aligned = NLMSG_ALIGN(bytes);
    if (w->Overflow || w->Used + aligned > w->Capacity) {
        w->Overflow = 1;
        return 0;
    }
    void *at = w->Buffer + w->Used;
    memset(at, 0, aligned);
    w->Used += aligned;
    return at;
}

/* Begins a message: its header's offset, to end it with. */
static WitU32 begin(Writer *w, WitU16 type, WitU16 flags, WitU32 seq, WitU32 pid)
{
    const WitU32 at = w->Used;
    struct nlmsghdr *h = reserve(w, sizeof(*h));
    if (h) {
        h->nlmsg_type = type;
        h->nlmsg_flags = flags;
        h->nlmsg_seq = seq;
        h->nlmsg_pid = pid;
    }
    return at;
}

static void end(Writer *w, WitU32 at)
{
    if (!w->Overflow) {
        ((struct nlmsghdr *)(w->Buffer + at))->nlmsg_len = w->Used - at;
    }
}

static void attribute(Writer *w, WitU16 type, const void *data, WitU32 length)
{
    struct rtattr *a = reserve(w, sizeof(*a) + length);
    if (a) {
        a->rta_len = (WitU16)(sizeof(*a) + length);
        a->rta_type = type;
        memcpy(a + 1, data, length);
    }
}

static void attribute_u32(Writer *w, WitU16 type, WitU32 value)
{
    attribute(w, type, &value, sizeof(value));
}

static void link_message(Writer *w, const Interface *i, WitU16 type, WitU16 flags, WitU32 seq, WitU32 pid)
{
    static const unsigned char broadcast[6] = {0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF};
    static const unsigned char none[6];
    const WitU32 at = begin(w, type, flags, seq, pid);
    struct ifinfomsg *m = reserve(w, sizeof(*m));
    if (m) {
        m->ifi_family = AF_UNSPEC;
        m->ifi_type = i->Type;
        m->ifi_index = (int)i->Index;
        m->ifi_flags = i->Flags;
    }
    attribute(w, IFLA_IFNAME, i->Name, (WitU32)strlen(i->Name) + 1U);
    attribute(w, IFLA_ADDRESS, i->Mac, 6);
    attribute(w, IFLA_BROADCAST, (i->Flags & IFF_BROADCAST) ? broadcast : none, 6);
    attribute_u32(w, IFLA_MTU, i->Mtu);
    end(w, at);
}

static void address_message(Writer *w, const Interface *i, WitU16 type, WitU16 flags, WitU32 seq, WitU32 pid)
{
    const WitU32 at = begin(w, type, flags, seq, pid);
    struct ifaddrmsg *m = reserve(w, sizeof(*m));
    if (m) {
        m->ifa_family = AF_INET;
        m->ifa_prefixlen = i->Prefix;
        m->ifa_flags = IFA_F_PERMANENT;
        m->ifa_scope = i->Scope;
        m->ifa_index = i->Index;
    }
    attribute(w, IFA_ADDRESS, i->Address, 4);
    attribute(w, IFA_LOCAL, i->Address, 4);
    if (i->Flags & IFF_BROADCAST) {
        attribute(w, IFA_BROADCAST, i->Broadcast, 4);
    }
    attribute(w, IFA_LABEL, i->Name, (WitU32)strlen(i->Name) + 1U);
    end(w, at);
}

/* The main table's routes of the adapter: its subnet, and the default route through the gateway when the lease names
 * one; the loopback's are the local table's, which a dump of the main table leaves out, as Linux's does. */
static void route_messages(Writer *w, const Interface *eth, WitU16 flags, WitU32 seq, WitU32 pid)
{
    static const unsigned char none[4];
    unsigned char network[4];
    for (int i = 0; i < 4; ++i) {
        network[i] = (unsigned char)(eth->Address[i] & eth->Mask[i]);
    }
    WitU32 at = begin(w, RTM_NEWROUTE, flags, seq, pid);
    struct rtmsg *m = reserve(w, sizeof(*m));
    if (m) {
        m->rtm_family = AF_INET;
        m->rtm_dst_len = eth->Prefix;
        m->rtm_table = RT_TABLE_MAIN;
        m->rtm_protocol = RTPROT_KERNEL;
        m->rtm_scope = RT_SCOPE_LINK;
        m->rtm_type = RTN_UNICAST;
    }
    attribute_u32(w, RTA_TABLE, RT_TABLE_MAIN);
    attribute(w, RTA_DST, network, 4);
    attribute(w, RTA_PREFSRC, eth->Address, 4);
    attribute_u32(w, RTA_OIF, eth->Index);
    end(w, at);
    if (memcmp(eth->Gateway, none, 4) == 0) {
        return;
    }
    at = begin(w, RTM_NEWROUTE, flags, seq, pid);
    m = reserve(w, sizeof(*m));
    if (m) {
        m->rtm_family = AF_INET;
        m->rtm_table = RT_TABLE_MAIN;
        m->rtm_protocol = RTPROT_DHCP;
        m->rtm_scope = RT_SCOPE_UNIVERSE;
        m->rtm_type = RTN_UNICAST;
    }
    attribute_u32(w, RTA_TABLE, RT_TABLE_MAIN);
    attribute(w, RTA_GATEWAY, eth->Gateway, 4);
    attribute_u32(w, RTA_OIF, eth->Index);
    end(w, at);
}

static void done_message(Writer *w, WitU32 seq, WitU32 pid)
{
    const WitU32 at = begin(w, NLMSG_DONE, NLM_F_MULTI, seq, pid);
    reserve(w, sizeof(int));
    end(w, at);
}

static void error_message(Writer *w, const struct nlmsghdr *request, int error, WitU32 pid)
{
    const WitU32 at = begin(w, NLMSG_ERROR, 0, request->nlmsg_seq, pid);
    struct nlmsgerr *e = reserve(w, sizeof(*e));
    if (e) {
        e->error = -error;
        e->msg = *request;
    }
    end(w, at);
}

/* The answers of one request, written after the queue's tail: a dump of the links, the addresses or the routes of a
 * family, each ending with DONE; anything else is refused with EOPNOTSUPP, as the kernel refuses what it does not
 * serve. The family a dump asks for is the rtgenmsg after the header, AF_UNSPEC when the request carries none. The
 * interfaces were read before the record's lock was taken, since reading them asks the service. */
static void answer(Writer *w, const struct nlmsghdr *h, WitU32 pid, const Interface *list, int count)
{
    const int dump = (h->nlmsg_flags & NLM_F_DUMP) == NLM_F_DUMP;
    const unsigned char family = h->nlmsg_len > sizeof(*h) ? *(const unsigned char *)(h + 1) : AF_UNSPEC;
    const int inet = family == AF_UNSPEC || family == AF_INET;
    if (!dump || (h->nlmsg_type != RTM_GETLINK && h->nlmsg_type != RTM_GETADDR && h->nlmsg_type != RTM_GETROUTE)) {
        error_message(w, h, EOPNOTSUPP, pid);
        return;
    }
    for (int i = 0; i < count; ++i) {
        if (h->nlmsg_type == RTM_GETLINK) {
            link_message(w, &list[i], RTM_NEWLINK, NLM_F_MULTI, h->nlmsg_seq, pid);
        } else if (h->nlmsg_type == RTM_GETADDR && inet && list[i].Addressed) {
            address_message(w, &list[i], RTM_NEWADDR, NLM_F_MULTI, h->nlmsg_seq, pid);
        } else if (h->nlmsg_type == RTM_GETROUTE && inet && list[i].Addressed && list[i].Index != 1) {
            route_messages(w, &list[i], NLM_F_MULTI, h->nlmsg_seq, pid);
        }
    }
    done_message(w, h->nlmsg_seq, pid);
}

static long allocate(WitU32 *index)
{
    __wit_lock(&netlink_lock);
    for (WitU32 i = 0; i < NETLINKS; ++i) {
        Netlink *n = &netlinks[i];
        if (!n->Used && !n->References) {
            const WitU32 id = n->Id + 1;
            n->Id = id;
            n->Used = 1;
            n->References = 1;
            n->Groups = 0;
            n->Port = i == 0 ? 1U : 0x10000U + i; /* the first is the process's number, 1 */
            n->Head = n->Tail = 0;
            *index = i;
            __wit_unlock(&netlink_lock);
            return 0;
        }
    }
    __wit_unlock(&netlink_lock);
    return -ENFILE;
}

void __wit_netlink_reference(WitU32 index)
{
    __wit_lock(&netlink_lock);
    ++netlinks[index].References;
    __wit_unlock(&netlink_lock);
}

void __wit_netlink_release(WitU32 index)
{
    __wit_lock(&netlink_lock);
    Netlink *n = &netlinks[index];
    if (n->References && --n->References == 0) {
        n->Used = 0;
        n->Head = n->Tail = 0;
        n->Groups = 0;
    }
    __wit_unlock(&netlink_lock);
}

WitU32 __wit_netlink_id(WitU32 index)
{
    return netlinks[index].Id;
}

/* Bumps the record's sequence and wakes its readers and every poll. */
static void changed(Netlink *n)
{
    __atomic_add_fetch(&n->Sequence, 1, __ATOMIC_RELEASE);
    __wit_futex(&n->Sequence, FUTEX_WAKE | FUTEX_PRIVATE, 0x7FFFFFFF, 0, 0, 0);
    __wit_poll_bump();
}

/* Writes a request's answers into the queue: nothing of them when they do not fit (ENOBUFS). Under the record's lock. */
static long enqueue_answers(Netlink *n, const struct nlmsghdr *h, const Interface *list, int count)
{
    Writer w = {n->Queue + n->Tail, QUEUE_BYTES - n->Tail, 0, 0};
    answer(&w, h, n->Port, list, count);
    if (w.Overflow) {
        return -ENOBUFS;
    }
    n->Tail += w.Used;
    return 0;
}

static long open_netlink(long type, long protocol)
{
    const long kind = type & 0xF, flags = type & ~0xFL;
    WitU32 index = 0;
    if (flags & ~(long)(SOCK_NONBLOCK | SOCK_CLOEXEC)) {
        return -EINVAL;
    }
    if (kind != SOCK_RAW && kind != SOCK_DGRAM) {
        return -ESOCKTNOSUPPORT;
    }
    if (protocol != NETLINK_ROUTE) {
        return -EPROTONOSUPPORT; /* the route family alone: what the interfaces need */
    }
    long r = allocate(&index);
    if (r < 0) {
        return r;
    }
    r = __wit_netlink_install(index, (flags & SOCK_NONBLOCK ? O_NONBLOCK : 0) | (flags & SOCK_CLOEXEC ? O_CLOEXEC : 0));
    if (r < 0) {
        __wit_netlink_release(index);
    }
    return r;
}

static long bind_netlink(Netlink *n, const struct sockaddr *address, socklen_t length)
{
    const struct sockaddr_nl *nl = (const struct sockaddr_nl *)address;
    if (!address) {
        return -EFAULT;
    }
    if (length < sizeof(*nl) || nl->nl_family != AF_NETLINK) {
        return -EINVAL;
    }
    __wit_lock(&netlink_lock);
    n->Groups = nl->nl_groups;
    if (nl->nl_pid != 0) {
        n->Port = nl->nl_pid;
    }
    __wit_unlock(&netlink_lock);
    return 0;
}

static long name_netlink(const Netlink *n, struct sockaddr *address, socklen_t *length, int peer)
{
    struct sockaddr_nl nl;
    if (!address || !length) {
        return -EFAULT;
    }
    memset(&nl, 0, sizeof(nl));
    nl.nl_family = AF_NETLINK;
    nl.nl_pid = peer ? 0 : n->Port; /* the peer is the kernel, port 0 */
    nl.nl_groups = peer ? 0 : n->Groups;
    memcpy(address, &nl, *length < sizeof(nl) ? *length : sizeof(nl));
    *length = sizeof(nl);
    return 0;
}

/* Requests, back to back in the bytes sent: each is answered into the queue before the next is read, and the bytes are
 * counted as sent. */
static long send_requests(Netlink *n, const unsigned char *bytes, WitU32 length)
{
    WitU32 at = 0;
    long r = 0;
    Interface list[2];
    if (length < sizeof(struct nlmsghdr)) {
        return -EINVAL;
    }
    const int count = interfaces(list); /* a round trip to the service: before the lock */
    __wit_lock(&netlink_lock);
    while (at + sizeof(struct nlmsghdr) <= length) {
        const struct nlmsghdr *h = (const struct nlmsghdr *)(bytes + at);
        if (h->nlmsg_len < sizeof(*h) || at + h->nlmsg_len > length) {
            r = at ? (long)at : -EINVAL;
            break;
        }
        if (h->nlmsg_flags & NLM_F_REQUEST) {
            r = enqueue_answers(n, h, list, count);
            if (r < 0) {
                break;
            }
        }
        at += NLMSG_ALIGN(h->nlmsg_len);
        r = (long)at;
    }
    __wit_unlock(&netlink_lock);
    if (r > 0) {
        changed(n);
    }
    return r;
}

/* Gathers a send's vectors into one request buffer. */
static long gather(const struct iovec *iov, int count, unsigned char *buffer, WitU32 capacity, WitU32 *length)
{
    WitU32 used = 0;
    if (count < 0 || count > 1024) {
        return -EMSGSIZE;
    }
    for (int i = 0; i < count; ++i) {
        if (iov[i].iov_len > capacity - used) {
            return -EMSGSIZE;
        }
        memcpy(buffer + used, iov[i].iov_base, iov[i].iov_len);
        used += (WitU32)iov[i].iov_len;
    }
    *length = used;
    return 0;
}

/* Takes queued messages into the vectors: whole messages while they fit, the first alone cut to the room when it does
 * not fit (*truncated); nothing while the queue is empty (0 bytes, the caller waits or answers EAGAIN). Under the
 * record's lock. */
static long dequeue(Netlink *n, const struct iovec *iov, int count, int peek, int *truncated)
{
    WitU32 room = 0, copied = 0, at = n->Head, vector = 0, offset = 0;
    for (int i = 0; i < count; ++i) {
        room += (WitU32)iov[i].iov_len;
    }
    *truncated = 0;
    while (at < n->Tail) {
        const struct nlmsghdr *h = (const struct nlmsghdr *)(n->Queue + at);
        const WitU32 whole = NLMSG_ALIGN(h->nlmsg_len);
        WitU32 take = h->nlmsg_len;
        if (take > room - copied) {
            if (copied) {
                break; /* the next message waits for the next read */
            }
            take = room - copied;
            *truncated = 1;
        }
        for (WitU32 done = 0; done < take;) {
            while (vector < (WitU32)count && offset == iov[vector].iov_len) {
                ++vector;
                offset = 0;
            }
            WitU32 piece = (WitU32)iov[vector].iov_len - offset;
            if (piece > take - done) {
                piece = take - done;
            }
            memcpy((unsigned char *)iov[vector].iov_base + offset, n->Queue + at + done, piece);
            offset += piece;
            done += piece;
        }
        copied += take;
        at += whole;
        if (*truncated) {
            break;
        }
    }
    if (!peek) {
        n->Head = at;
        if (n->Head == n->Tail) {
            n->Head = n->Tail = 0;
        } else if (n->Head > QUEUE_BYTES / 2) {
            memmove(n->Queue, n->Queue + n->Head, n->Tail - n->Head);
            n->Tail -= n->Head;
            n->Head = 0;
        }
    }
    return (long)copied;
}

/* A receive: the messages queued, or a park on the sequence word until some are, EAGAIN when the caller must not wait
 * and EINTR after a handler. */
static long receive(Netlink *n, const struct iovec *iov, int count, int flags, int nonblocking, struct msghdr *message)
{
    int zero = 1; /* a receive into no room waits for a message and takes none: .NET's wait for readiness */
    for (int i = 0; i < count; ++i) {
        if (iov[i].iov_len) {
            zero = 0;
        }
    }
    for (;;) {
        __wit_lock(&netlink_lock);
        const int seen = __atomic_load_n(&n->Sequence, __ATOMIC_ACQUIRE);
        const int queued = n->Head < n->Tail;
        int truncated = 0;
        const long r = queued ? dequeue(n, iov, count, (flags & MSG_PEEK) != 0 || zero, &truncated) : 0;
        __wit_unlock(&netlink_lock);
        if (queued) {
            if (message) {
                message->msg_flags = truncated && !zero ? MSG_TRUNC : 0;
                if (message->msg_name && message->msg_namelen >= sizeof(struct sockaddr_nl)) {
                    struct sockaddr_nl nl;
                    memset(&nl, 0, sizeof(nl));
                    nl.nl_family = AF_NETLINK;
                    memcpy(message->msg_name, &nl, sizeof(nl));
                    message->msg_namelen = sizeof(nl);
                } else if (message->msg_name) {
                    message->msg_namelen = sizeof(struct sockaddr_nl);
                }
            }
            return r;
        }
        if (nonblocking || (flags & MSG_DONTWAIT)) {
            return -EAGAIN;
        }
        if (__wit_cancel_requested()) {
            return -EINTR;
        }
        const long parked = __wit_futex_wait_until(&n->Sequence, seen, WIT_WAIT_INFINITE);
        if (parked == -EINTR) {
            return -EINTR;
        }
    }
}

static long receive_from(Netlink *n, void *buffer, long length, int flags, int nonblocking, struct sockaddr *address,
    socklen_t *address_length)
{
    const struct iovec iov = {buffer, length < 0 ? 0 : (size_t)length};
    struct msghdr message;
    struct sockaddr_nl nl;
    if (length < 0 || (!buffer && length)) {
        return -EFAULT;
    }
    memset(&message, 0, sizeof(message));
    message.msg_name = &nl;
    message.msg_namelen = sizeof(nl);
    const long r = receive(n, &iov, 1, flags, nonblocking, &message);
    if (r >= 0 && address && address_length) {
        memcpy(address, &nl, *address_length < sizeof(nl) ? *address_length : sizeof(nl));
        *address_length = sizeof(nl);
    }
    return r;
}

static long option_get(const Netlink *n, int level, int name, void *value, socklen_t *length)
{
    int answer = 0;
    if (!value || !length || *length < (socklen_t)sizeof(int)) {
        return -EINVAL;
    }
    if (level == SOL_SOCKET) {
        switch (name) {
        case SO_TYPE:
            answer = SOCK_RAW;
            break;
        case SO_DOMAIN:
            answer = AF_NETLINK;
            break;
        case SO_PROTOCOL:
            answer = NETLINK_ROUTE;
            break;
        case SO_RCVBUF:
        case SO_SNDBUF:
            answer = (int)QUEUE_BYTES;
            break;
        case SO_ERROR:
            break;
        default:
            return -ENOPROTOOPT;
        }
    } else if (level == SOL_NETLINK) {
        (void)n;
        return -ENOPROTOOPT;
    } else {
        return -ENOPROTOOPT;
    }
    memcpy(value, &answer, sizeof(answer));
    *length = sizeof(answer);
    return 0;
}

static long option_set(Netlink *n, int level, int name, const void *value, socklen_t length)
{
    if (level == SOL_SOCKET) {
        switch (name) {
        case SO_RCVBUF:
        case SO_SNDBUF:
        case SO_RCVTIMEO:
        case SO_SNDTIMEO:
        case SO_REUSEADDR:
            return 0; /* the queue has its size; a timeout changes nothing a reader meets */
        default:
            return -ENOPROTOOPT;
        }
    }
    if (level == SOL_NETLINK && (name == NETLINK_ADD_MEMBERSHIP || name == NETLINK_DROP_MEMBERSHIP)) {
        int group = 0;
        if (!value || length < (socklen_t)sizeof(int)) {
            return -EINVAL;
        }
        memcpy(&group, value, sizeof(group));
        if (group < 1 || group > 32) {
            return -EINVAL;
        }
        __wit_lock(&netlink_lock);
        if (name == NETLINK_ADD_MEMBERSHIP) {
            n->Groups |= 1U << (group - 1);
        } else {
            n->Groups &= ~(1U << (group - 1));
        }
        __wit_unlock(&netlink_lock);
        return 0;
    }
    return -ENOPROTOOPT;
}

/* Readiness: readable with a message queued, always writable. */
static short readiness(const Netlink *n, short events, WitU64 *token)
{
    short ready = 0;
    __wit_lock(&netlink_lock);
    if (n->Head < n->Tail) {
        ready |= (short)(POLLIN | POLLRDNORM);
    }
    *token = (WitU64)(WitU32)__atomic_load_n(&n->Sequence, __ATOMIC_ACQUIRE);
    __wit_unlock(&netlink_lock);
    ready |= (short)(POLLOUT | POLLWRNORM);
    return (short)(ready & (events | POLLERR | POLLHUP));
}

short __wit_netlink_poll(WitU32 index, short events)
{
    WitU64 token = 0;
    return readiness(&netlinks[index], events, &token);
}

short __wit_netlink_edge(WitU32 index, short events, WitU64 *token)
{
    return readiness(&netlinks[index], events, token);
}

/* A socket call of the netlink family or of a netlink socket's descriptor; WIT_NOT_A_SOCKET for any other. */
long __wit_netlink_call(long n, long a1, long a2, long a3, long a4, long a5, long a6)
{
    WitU32 index = 0;
    int nonblocking = 0;
    unsigned char request[REQUEST_BYTES];
    (void)a6;
    if (n == SYS_socket) {
        return a1 == AF_NETLINK ? open_netlink(a2, a3) : WIT_NOT_A_SOCKET;
    }
    if (n == SYS_socketpair) {
        return a1 == AF_NETLINK ? -EOPNOTSUPP : WIT_NOT_A_SOCKET;
    }
    const long found = __wit_netlink_lookup(a1, &index, &nonblocking);
    if (found <= 0) {
        return WIT_NOT_A_SOCKET;
    }
    Netlink *s = &netlinks[index];
    long r = 0;
    switch (n) {
    case SYS_bind:
        r = bind_netlink(s, (const struct sockaddr *)a2, (socklen_t)a3);
        break;
    case SYS_getsockname:
        r = name_netlink(s, (struct sockaddr *)a2, (socklen_t *)a3, 0);
        break;
    case SYS_getpeername:
        r = name_netlink(s, (struct sockaddr *)a2, (socklen_t *)a3, 1);
        break;
    case SYS_connect:
        r = a2 ? 0 : -EFAULT; /* the kernel is the one peer */
        break;
    case SYS_listen:
    case SYS_accept:
    case SYS_accept4:
        r = -EOPNOTSUPP;
        break;
    case SYS_shutdown:
        r = 0;
        break;
    case SYS_sendto: {
        if (!a2 && a3) {
            r = -EFAULT;
        } else if (a3 < 0 || a3 > (long)REQUEST_BYTES) {
            r = -EMSGSIZE;
        } else {
            memcpy(request, (const void *)a2, (unsigned long)a3);
            r = send_requests(s, request, (WitU32)a3);
        }
        break;
    }
    case SYS_sendmsg: {
        const struct msghdr *message = (const struct msghdr *)a2;
        WitU32 length = 0;
        if (!message) {
            r = -EFAULT;
        } else if ((r = gather(message->msg_iov, message->msg_iovlen, request, REQUEST_BYTES, &length)) == 0) {
            r = send_requests(s, request, length);
        }
        break;
    }
    case SYS_recvfrom:
        r = receive_from(s, (void *)a2, a3, (int)a4, nonblocking, (struct sockaddr *)a5, (socklen_t *)a6);
        break;
    case SYS_recvmsg: {
        struct msghdr *message = (struct msghdr *)a2;
        r = !message || (message->msg_iovlen && !message->msg_iov)
            ? -EFAULT
            : receive(s, message->msg_iov, message->msg_iovlen, (int)a3, nonblocking, message);
        break;
    }
    case SYS_getsockopt:
        r = option_get(s, (int)a2, (int)a3, (void *)a4, (socklen_t *)a5);
        break;
    case SYS_setsockopt:
        r = option_set(s, (int)a2, (int)a3, (const void *)a4, (socklen_t)a5);
        break;
    default:
        r = -EOPNOTSUPP;
        break;
    }
    __wit_netlink_release(index);
    return r;
}

/* read, write, readv and writev of a netlink socket's descriptor; WIT_NOT_A_SOCKET for another. */
long __wit_netlink_transfer(long n, long fd, long a2, long a3)
{
    WitU32 index = 0;
    int nonblocking = 0;
    unsigned char request[REQUEST_BYTES];
    if (__wit_netlink_lookup(fd, &index, &nonblocking) <= 0) {
        return WIT_NOT_A_SOCKET;
    }
    Netlink *s = &netlinks[index];
    long r;
    if (n == SYS_read) {
        r = receive_from(s, (void *)a2, a3, 0, nonblocking, 0, 0);
    } else if (n == SYS_readv) {
        r = !a2 && a3 ? -EFAULT : receive(s, (const struct iovec *)a2, (int)a3, 0, nonblocking, 0);
    } else if (n == SYS_write) {
        if (!a2 && a3) {
            r = -EFAULT;
        } else if (a3 < 0 || a3 > (long)REQUEST_BYTES) {
            r = -EMSGSIZE;
        } else {
            memcpy(request, (const void *)a2, (unsigned long)a3);
            r = send_requests(s, request, (WitU32)a3);
        }
    } else {
        WitU32 length = 0;
        r = gather((const struct iovec *)a2, (int)a3, request, REQUEST_BYTES, &length);
        if (r == 0) {
            r = send_requests(s, request, length);
        }
    }
    __wit_netlink_release(index);
    return r;
}

/* ioctl of a netlink socket's descriptor: the interface ioctls, FIONREAD (the next message's bytes) and FIONBIO as a
 * socket's (files.c); WIT_NOT_A_SOCKET for another descriptor. */
long __wit_netlink_ioctl(long fd, long command, void *argument)
{
    WitU32 index = 0;
    int nonblocking = 0;
    if (__wit_netlink_lookup(fd, &index, &nonblocking) <= 0) {
        return WIT_NOT_A_SOCKET;
    }
    long r = __wit_interface_ioctl(command, argument);
    if (r == WIT_NOT_A_SOCKET) {
        if (command == FIONREAD) {
            Netlink *s = &netlinks[index];
            int bytes = 0;
            __wit_lock(&netlink_lock);
            if (s->Head < s->Tail) {
                bytes = (int)((const struct nlmsghdr *)(s->Queue + s->Head))->nlmsg_len;
            }
            __wit_unlock(&netlink_lock);
            r = argument ? (memcpy(argument, &bytes, sizeof(bytes)), 0) : -EFAULT;
        } else if (command == FIONBIO) {
            r = argument ? __wit_socket_nonblocking(fd, *(const int *)argument != 0) : -EFAULT;
        } else {
            r = -ENOTTY;
        }
    }
    __wit_netlink_release(index);
    return r;
}

/* The interface ioctls of any socket (SIOCGIF*), answered from the interfaces as they are now: WIT_NOT_A_SOCKET for a
 * command that is none of them, ENODEV for an interface that is not there, EADDRNOTAVAIL for an address an interface
 * lacks. SIOCGIFCONF lists the addressed interfaces' names and IPv4 addresses, the room needed when ifc_req is null. */
long __wit_interface_ioctl(long command, void *argument)
{
    Interface list[2];
    switch (command) {
    case SIOCGIFINDEX:
    case SIOCGIFNAME:
    case SIOCGIFFLAGS:
    case SIOCGIFMTU:
    case SIOCGIFHWADDR:
    case SIOCGIFADDR:
    case SIOCGIFNETMASK:
    case SIOCGIFBRDADDR:
    case SIOCGIFCONF:
        break;
    default:
        return WIT_NOT_A_SOCKET;
    }
    if (!argument) {
        return -EFAULT;
    }
    const int count = interfaces(list);
    if (command == SIOCGIFCONF) {
        struct ifconf *c = argument;
        int used = 0;
        for (int i = 0; i < count; ++i) {
            if (!list[i].Addressed) {
                continue;
            }
            if (c->ifc_req) {
                if (used + (int)sizeof(struct ifreq) > c->ifc_len) {
                    break;
                }
                struct ifreq *r = &c->ifc_req[used / (int)sizeof(struct ifreq)];
                struct sockaddr_in *in = (struct sockaddr_in *)&r->ifr_addr;
                memset(r, 0, sizeof(*r));
                memcpy(r->ifr_name, list[i].Name, IFNAMSIZ);
                in->sin_family = AF_INET;
                memcpy(&in->sin_addr, list[i].Address, 4);
            }
            used += (int)sizeof(struct ifreq);
        }
        c->ifc_len = used;
        return 0;
    }
    struct ifreq *r = argument;
    const Interface *i = 0;
    if (command == SIOCGIFNAME) {
        for (int k = 0; k < count; ++k) {
            if ((int)list[k].Index == r->ifr_ifindex) {
                i = &list[k];
            }
        }
        if (!i) {
            return -ENODEV;
        }
        memcpy(r->ifr_name, i->Name, IFNAMSIZ);
        return 0;
    }
    for (int k = 0; k < count; ++k) {
        if (strncmp(list[k].Name, r->ifr_name, IFNAMSIZ) == 0) {
            i = &list[k];
        }
    }
    if (!i) {
        return -ENODEV;
    }
    struct sockaddr_in *in = (struct sockaddr_in *)&r->ifr_addr;
    switch (command) {
    case SIOCGIFINDEX:
        r->ifr_ifindex = (int)i->Index;
        return 0;
    case SIOCGIFFLAGS:
        r->ifr_flags = (short)i->Flags;
        return 0;
    case SIOCGIFMTU:
        r->ifr_mtu = (int)i->Mtu;
        return 0;
    case SIOCGIFHWADDR:
        memset(&r->ifr_hwaddr, 0, sizeof(r->ifr_hwaddr));
        r->ifr_hwaddr.sa_family = i->Type;
        memcpy(r->ifr_hwaddr.sa_data, i->Mac, 6);
        return 0;
    default:
        break;
    }
    if (!i->Addressed) {
        return -EADDRNOTAVAIL;
    }
    memset(in, 0, sizeof(*in));
    in->sin_family = AF_INET;
    memcpy(&in->sin_addr, command == SIOCGIFADDR ? i->Address : command == SIOCGIFNETMASK ? i->Mask : i->Broadcast, 4);
    return 0;
}

/* The service's configuration changed (socket.c's events thread): every socket that bound the groups hears of the
 * adapter's address that went and the one that came (RTM_DELADDR, RTM_NEWADDR), and of its link (RTM_NEWLINK), as the
 * kernel's route multicast tells them; events carry the kernel's port, 0, and no sequence. A queue without room for
 * them drops them, as Linux's does with ENOBUFS at the reader, which asks again. */
void __wit_netlink_notify(const WitSocketConfiguration *before, const WitSocketConfiguration *after)
{
    Interface was, is;
    int had = before->Mac != 0, has = after->Mac != 0;
    if (had) {
        adapter(&was, before);
    }
    if (has) {
        adapter(&is, after);
    }
    const int address_changed = had != has ||
        (has &&
            (was.Addressed != is.Addressed ||
                memcmp(was.Address, is.Address, 4) != 0 ||
                memcmp(was.Mask, is.Mask, 4) != 0));
    const int link_changed = had != has || (has && (was.Mtu != is.Mtu || was.Flags != is.Flags));
    const int route_changed = address_changed || (has && memcmp(was.Gateway, is.Gateway, 4) != 0);
    for (WitU32 i = 0; i < NETLINKS; ++i) {
        Netlink *n = &netlinks[i];
        int told = 0;
        __wit_lock(&netlink_lock);
        if (n->Used && n->Groups) {
            Writer w = {n->Queue + n->Tail, QUEUE_BYTES - n->Tail, 0, 0};
            if ((n->Groups & RTMGRP_LINK) && link_changed && has) {
                link_message(&w, &is, RTM_NEWLINK, 0, 0, 0);
            }
            if ((n->Groups & RTMGRP_IPV4_IFADDR) && address_changed) {
                if (had && was.Addressed) {
                    address_message(&w, &was, RTM_DELADDR, 0, 0, 0);
                }
                if (has && is.Addressed) {
                    address_message(&w, &is, RTM_NEWADDR, 0, 0, 0);
                }
            }
            if ((n->Groups & RTMGRP_IPV4_ROUTE) && route_changed && has && is.Addressed) {
                route_messages(&w, &is, 0, 0, 0);
            }
            if (!w.Overflow && w.Used) {
                n->Tail += w.Used;
                told = 1;
            }
        }
        __wit_unlock(&netlink_lock);
        if (told) {
            changed(n);
        }
    }
}
