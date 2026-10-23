#define _GNU_SOURCE
#include <arpa/inet.h>
#include <errno.h>
#include <fcntl.h>
#include <ifaddrs.h>
#include <net/if.h>
#include <netdb.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <netpacket/packet.h>
#include <poll.h>
#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/time.h>
#include <time.h>
#include <unistd.h>

/* The libc's sockets over the network service (plan step W2.1a): /bin/sockets-check, which the runtime-devices
 * scenario's /bin/init starts, a process like any other, whose start message carried the network service's endpoint.
 * It checks the Linux socket calls as programs use them: what is refused (a raw socket, an AF_INET pair;
 * AF_UNIX is /bin/local-sockets-check's, W4.1), UDP and TCP over the loopback
 * (bind, getsockname, sendto and recvfrom with their addresses, MSG_PEEK, MSG_TRUNC, FIONREAD, a nonblocking connect
 * with poll and SO_ERROR, accept4's flags, EAGAIN, SO_RCVTIMEO, a blocking send larger than the buffers against a
 * reading thread, shutdown's end of the stream, a refused connection), names (/etc/hosts, /etc/resolv.conf from the
 * lease, a name through musl's resolver and slirp's forwarder), a connection to the runner's echo server and one from
 * the runner through a port forward, and the interfaces (W4.2: getifaddrs and if_nameindex over the libc's netlink route
 * socket, if_nametoindex and if_indextoname and the SIOCGIF* ioctls on a UDP socket, naming lo and eth0 with the lease),
 * and IPv6 (W4.3: datagrams and streams over ::1, a
 * dual-stack listener taking an IPv4 client with its peer in the mapped form, IPV6_V6ONLY, sockaddr_in6 names,
 * localhost's ::1, the interface's link-local and global addresses from slirp's router advertisement, and slirp's IPv6
 * DNS server in /etc/resolv.conf; a query to it and a connection to the host's IPv6 loopback are reported, not
 * required, since they need IPv6 on the host). */

#define LISTEN_PORT 8080
#define BIG (512 * 1024)
#define ECHO_BYTES (256 * 1024)
#define CHUNK (16 * 1024)

static int passed, failed;

static void check(int condition, const char *what)
{
    if (condition) {
        ++passed;
    } else {
        ++failed;
        printf("[SOCKETS] FAILED: %s (errno %d: %s)\n", what, errno, strerror(errno));
    }
}

static long milliseconds(void)
{
    struct timespec now;
    clock_gettime(CLOCK_MONOTONIC, &now);
    return now.tv_sec * 1000L + now.tv_nsec / 1000000L;
}

static struct sockaddr_in address_of(const char *ip, int port)
{
    struct sockaddr_in a;
    memset(&a, 0, sizeof(a));
    a.sin_family = AF_INET;
    a.sin_port = htons((unsigned short)port);
    inet_pton(AF_INET, ip, &a.sin_addr);
    return a;
}

static int port_of(int fd)
{
    struct sockaddr_in a;
    socklen_t length = sizeof(a);
    return getsockname(fd, (struct sockaddr *)&a, &length) == 0 && length == sizeof(a) ? ntohs(a.sin_port) : -1;
}

static struct sockaddr_in6 address6_of(const char *ip, int port)
{
    struct sockaddr_in6 a;
    memset(&a, 0, sizeof(a));
    a.sin6_family = AF_INET6;
    a.sin6_port = htons((unsigned short)port);
    inet_pton(AF_INET6, ip, &a.sin6_addr);
    return a;
}

static int port6_of(int fd)
{
    struct sockaddr_in6 a;
    socklen_t length = sizeof(a);
    return getsockname(fd, (struct sockaddr *)&a, &length) == 0 && length == sizeof(a) && a.sin6_family == AF_INET6
        ? ntohs(a.sin6_port)
        : -1;
}

static unsigned char pattern(long i)
{
    return (unsigned char)(i * 31 + (i >> 9) * 7 + (i >> 17));
}

static void refusals(void)
{
    errno = 0;
    check(
        socket(AF_INET6, SOCK_RAW, IPPROTO_ICMPV6) < 0 && errno == ESOCKTNOSUPPORT, "a raw socket is ESOCKTNOSUPPORT");
    int pair[2];
    errno = 0;
    check(socketpair(AF_INET, SOCK_STREAM, 0, pair) < 0 && errno == EOPNOTSUPP, "socketpair of AF_INET is EOPNOTSUPP");
}

static void datagrams(void)
{
    const int a = socket(AF_INET, SOCK_DGRAM, 0), b = socket(AF_INET, SOCK_DGRAM | SOCK_CLOEXEC, 0);
    check(a >= 0 && b >= 0, "two datagram sockets");
    struct stat st;
    check(fstat(a, &st) == 0 && S_ISSOCK(st.st_mode), "fstat reports a socket");
    struct sockaddr_in where = address_of("127.0.0.1", 0);
    check(bind(a, (struct sockaddr *)&where, sizeof(where)) == 0, "bind to the loopback's ephemeral port");
    const int port = port_of(a);
    check(port >= 49152, "getsockname names the ephemeral port");
    where.sin_port = htons((unsigned short)port);
    check(sendto(b, "ping", 4, 0, (struct sockaddr *)&where, sizeof(where)) == 4, "sendto over the loopback");
    struct pollfd pfd = {a, POLLIN, 0};
    check(poll(&pfd, 1, 5000) == 1 && (pfd.revents & POLLIN), "poll sees the datagram");
    int available = 0;
    check(ioctl(a, FIONREAD, &available) == 0 && available == 4, "FIONREAD is the datagram's length");
    char buffer[16] = {0};
    check(recv(a, buffer, 2, MSG_PEEK | MSG_TRUNC) == 4 && memcmp(buffer, "pi", 2) == 0, "MSG_PEEK with MSG_TRUNC");
    struct sockaddr_in from;
    socklen_t length = sizeof(from);
    memset(buffer, 0, sizeof(buffer));
    check(recvfrom(a, buffer, sizeof(buffer), 0, (struct sockaddr *)&from, &length) == 4 &&
            memcmp(buffer, "ping", 4) == 0,
        "recvfrom takes the datagram");
    check(
        length == sizeof(from) && from.sin_addr.s_addr == htonl(INADDR_LOOPBACK) && ntohs(from.sin_port) == port_of(b),
        "recvfrom names the sender");
    check(recv(a, buffer, sizeof(buffer), MSG_DONTWAIT) < 0 && errno == EAGAIN,
        "MSG_DONTWAIT on an empty queue is EAGAIN");
    close(a);
    close(b);
}

static void *reader(void *argument)
{
    const int fd = *(const int *)argument;
    static unsigned char buffer[CHUNK];
    long got = 0, wrong = 0;
    while (got < BIG) {
        const ssize_t n = read(fd, buffer, sizeof(buffer));
        if (n <= 0) {
            break;
        }
        for (ssize_t i = 0; i < n; ++i) {
            wrong += buffer[i] != pattern(got + i);
        }
        got += n;
    }
    return (void *)(long)(got == BIG && !wrong);
}

static void streams(void)
{
    const int listener = socket(AF_INET, SOCK_STREAM, 0);
    int one = 1;
    check(listener >= 0 && setsockopt(listener, SOL_SOCKET, SO_REUSEADDR, &one, sizeof(one)) == 0, "a stream socket");
    struct sockaddr_in where = address_of("127.0.0.1", 0);
    check(bind(listener, (struct sockaddr *)&where, sizeof(where)) == 0 && listen(listener, 4) == 0, "bind and listen");
    int value = 0;
    socklen_t length = sizeof(value);
    check(getsockopt(listener, SOL_SOCKET, SO_ACCEPTCONN, &value, &length) == 0 && value == 1, "SO_ACCEPTCONN");
    where.sin_port = htons((unsigned short)port_of(listener));
    const int client = socket(AF_INET, SOCK_STREAM | SOCK_NONBLOCK, 0);
    check(connect(client, (struct sockaddr *)&where, sizeof(where)) < 0 && errno == EINPROGRESS,
        "a nonblocking connect is EINPROGRESS");
    struct pollfd pfd = {client, POLLOUT, 0};
    check(poll(&pfd, 1, 5000) == 1 && (pfd.revents & POLLOUT), "poll sees the connection established");
    value = -1;
    length = sizeof(value);
    check(getsockopt(client, SOL_SOCKET, SO_ERROR, &value, &length) == 0 && value == 0, "SO_ERROR is zero");
    struct sockaddr_in peer;
    length = sizeof(peer);
    const int server = accept4(listener, (struct sockaddr *)&peer, &length, SOCK_CLOEXEC);
    check(server >= 0 && peer.sin_addr.s_addr == htonl(INADDR_LOOPBACK) && ntohs(peer.sin_port) == port_of(client),
        "accept4 names the peer");
    check(fcntl(server, F_GETFD) == FD_CLOEXEC, "accept4's SOCK_CLOEXEC");
    check(getsockopt(server, SOL_SOCKET, SO_TYPE, &value, &length) == 0 && value == SOCK_STREAM, "SO_TYPE");
    check(setsockopt(server, IPPROTO_TCP, TCP_NODELAY, &one, sizeof(one)) == 0, "TCP_NODELAY");
    char buffer[32] = {0};
    check(recv(client, buffer, sizeof(buffer), 0) < 0 && errno == EAGAIN, "a nonblocking recv is EAGAIN");
    check(write(server, "hello", 5) == 5, "write to a socket");
    pfd = (struct pollfd){client, POLLIN, 0};
    check(poll(&pfd, 1, 5000) == 1 && (pfd.revents & POLLIN), "poll sees the bytes");
    int available = 0;
    check(ioctl(client, FIONREAD, &available) == 0 && available == 5, "FIONREAD is the bytes waiting");
    check(recv(client, buffer, sizeof(buffer), MSG_PEEK) == 5, "MSG_PEEK");
    check(read(client, buffer, sizeof(buffer)) == 5 && memcmp(buffer, "hello", 5) == 0, "read from a socket");
    /* SO_RCVTIMEO: a blocking recv ends with EAGAIN once the timeout passed. */
    const struct timeval timeout = {0, 200000};
    check(setsockopt(server, SOL_SOCKET, SO_RCVTIMEO, &timeout, sizeof(timeout)) == 0, "SO_RCVTIMEO");
    const long started = milliseconds();
    check(recv(server, buffer, sizeof(buffer), 0) < 0 && errno == EAGAIN && milliseconds() - started >= 150,
        "a blocking recv ends at SO_RCVTIMEO");
    /* A blocking send larger than both buffers, against a thread that reads. */
    const int flags = fcntl(client, F_GETFL);
    check(fcntl(client, F_SETFL, flags & ~O_NONBLOCK) == 0, "F_SETFL clears O_NONBLOCK");
    pthread_t thread;
    check(pthread_create(&thread, 0, reader, (void *)&client) == 0, "the reading thread");
    static unsigned char big[BIG];
    for (long i = 0; i < BIG; ++i) {
        big[i] = pattern(i);
    }
    check(send(server, big, BIG, MSG_NOSIGNAL) == BIG, "a blocking send of 512 KiB");
    void *whole = 0;
    pthread_join(thread, &whole);
    check(whole != 0, "the reading thread read every byte in order");
    check(shutdown(server, SHUT_WR) == 0, "shutdown of the writing");
    check(read(client, buffer, sizeof(buffer)) == 0, "the end of the stream");
    length = sizeof(peer);
    check(getpeername(client, (struct sockaddr *)&peer, &length) == 0 && ntohs(peer.sin_port) == port_of(listener),
        "getpeername");
    close(client);
    close(server);
    close(listener);
    /* A refused connection: the loopback's port nobody listens on. */
    const int refused = socket(AF_INET, SOCK_STREAM, 0);
    where.sin_port = htons(9);
    check(connect(refused, (struct sockaddr *)&where, sizeof(where)) < 0 && errno == ECONNREFUSED, "ECONNREFUSED");
    close(refused);
}

static void names(void)
{
    char line[64] = {0};
    FILE *resolv = fopen("/etc/resolv.conf", "r");
    check(resolv && fgets(line, sizeof(line), resolv) && strcmp(line, "nameserver 10.0.2.3\n") == 0,
        "/etc/resolv.conf names the lease's DNS server");
    if (resolv) {
        fclose(resolv);
    }
    struct addrinfo hints, *found = 0;
    memset(&hints, 0, sizeof(hints));
    hints.ai_family = AF_INET;
    hints.ai_socktype = SOCK_STREAM;
    int error = getaddrinfo("localhost", "80", &hints, &found);
    check(error == 0 && found && ((struct sockaddr_in *)found->ai_addr)->sin_addr.s_addr == htonl(INADDR_LOOPBACK),
        "localhost through /etc/hosts");
    if (found) {
        freeaddrinfo(found);
        found = 0;
    }
    error = getaddrinfo("example.com", "80", &hints, &found);
    char text[INET_ADDRSTRLEN] = "none";
    if (error == 0 && found) {
        inet_ntop(AF_INET, &((struct sockaddr_in *)found->ai_addr)->sin_addr, text, sizeof(text));
    }
    check(error == 0 && found, "example.com through musl's resolver and slirp's forwarder");
    printf(
        "[SOCKETS] example.com resolves to %s (getaddrinfo %d: %s)\n", text, error, error ? gai_strerror(error) : "ok");
    if (found) {
        freeaddrinfo(found);
    }
}

/* The runner's echo server, which QEMU's guestfwd puts at 10.0.2.100:7: a quarter of a megabyte there and back. */
static void outbound(void)
{
    const int fd = socket(AF_INET, SOCK_STREAM, 0);
    const struct sockaddr_in echo = address_of("10.0.2.100", 7);
    check(connect(fd, (const struct sockaddr *)&echo, sizeof(echo)) == 0,
        "a blocking connect to the runner's echo server");
    static unsigned char chunk[CHUNK], back[CHUNK];
    long sent = 0, wrong = 0;
    while (sent < ECHO_BYTES) {
        for (long i = 0; i < CHUNK; ++i) {
            chunk[i] = pattern(sent + i);
        }
        if (send(fd, chunk, CHUNK, MSG_NOSIGNAL) != CHUNK) {
            break;
        }
        ssize_t got = 0;
        while (got < CHUNK) {
            const ssize_t n = recv(fd, back + got, (size_t)(CHUNK - got), 0);
            if (n <= 0) {
                break;
            }
            got += n;
        }
        if (got != CHUNK) {
            break;
        }
        wrong += memcmp(chunk, back, CHUNK) != 0;
        sent += CHUNK;
    }
    check(sent == ECHO_BYTES && wrong == 0, "the echo server's bytes came back unchanged");
    printf(
        "[SOCKETS] %ld bytes echoed by the runner through 10.0.2.100:7, %s\n", sent, wrong ? "changed" : "unchanged");
    close(fd);
}

/* The runner's connection through the port forward: what it sends goes back until it ends its sending. */
static void inbound(void)
{
    const int listener = socket(AF_INET, SOCK_STREAM, 0);
    int one = 1;
    const struct timeval timeout = {300, 0}; /* the guest's time, which runs ahead while it waits for the host */
    struct sockaddr_in where = address_of("0.0.0.0", LISTEN_PORT);
    check(setsockopt(listener, SOL_SOCKET, SO_REUSEADDR, &one, sizeof(one)) == 0 &&
            setsockopt(listener, SOL_SOCKET, SO_RCVTIMEO, &timeout, sizeof(timeout)) == 0 &&
            bind(listener, (struct sockaddr *)&where, sizeof(where)) == 0 &&
            listen(listener, 1) == 0,
        "a socket listening on port 8080");
    printf("[SOCKETS] listening on port %d for the runner\n", LISTEN_PORT);
    fflush(stdout);
    struct sockaddr_in peer;
    socklen_t length = sizeof(peer);
    const int fd = accept(listener, (struct sockaddr *)&peer, &length);
    check(fd >= 0, "accept of the runner's connection");
    static unsigned char buffer[CHUNK];
    long echoed = 0;
    for (ssize_t n; fd >= 0 && (n = read(fd, buffer, sizeof(buffer))) > 0;) {
        if (write(fd, buffer, (size_t)n) != n) {
            break;
        }
        echoed += n;
    }
    if (fd >= 0) {
        shutdown(fd, SHUT_WR);
        close(fd);
    }
    close(listener);
    char text[INET_ADDRSTRLEN] = "none";
    inet_ntop(AF_INET, &peer.sin_addr, text, sizeof(text));
    printf("[SOCKETS] echoed %ld bytes back to the runner's connection from %s, inbound through the port forward\n",
        echoed, text);
}

#define ARPHRD_ETHER_TYPE 1
#define ARPHRD_LOOPBACK_TYPE 772

/* The interfaces (W4.2): what the libc answers from the network service's configuration through netlink and the
 * interface ioctls — the loopback, index 1, and eth0, index 2, with the lease's address and the adapter's MAC and MTU. */
static void interfaces(void)
{
    struct ifaddrs *list = 0;
    int lo_inet = 0, lo_packet = 0, eth_inet = 0, eth_packet = 0, eth_mask = 0;
    unsigned eth_flags = 0;
    char eth_text[INET_ADDRSTRLEN] = "none";
    check(getifaddrs(&list) == 0, "getifaddrs lists the interfaces");
    for (const struct ifaddrs *a = list; a; a = a->ifa_next) {
        if (!a->ifa_addr) {
            continue;
        }
        const int lo = strcmp(a->ifa_name, "lo") == 0, eth = strcmp(a->ifa_name, "eth0") == 0;
        if (a->ifa_addr->sa_family == AF_INET) {
            const struct sockaddr_in *in = (const struct sockaddr_in *)a->ifa_addr;
            const struct sockaddr_in *mask = (const struct sockaddr_in *)a->ifa_netmask;
            if (lo) {
                lo_inet = in->sin_addr.s_addr == htonl(INADDR_LOOPBACK) &&
                    mask &&
                    mask->sin_addr.s_addr == htonl(0xFF000000U);
            } else if (eth) {
                eth_inet = 1;
                eth_flags = a->ifa_flags;
                eth_mask = mask && mask->sin_addr.s_addr == htonl(0xFFFFFF00U);
                inet_ntop(AF_INET, &in->sin_addr, eth_text, sizeof(eth_text));
            }
        } else if (a->ifa_addr->sa_family == AF_PACKET) {
            const struct sockaddr_ll *ll = (const struct sockaddr_ll *)a->ifa_addr;
            if (lo) {
                lo_packet = ll->sll_ifindex == 1 && ll->sll_hatype == ARPHRD_LOOPBACK_TYPE;
            } else if (eth) {
                eth_packet = ll->sll_ifindex == 2 &&
                    ll->sll_hatype == ARPHRD_ETHER_TYPE &&
                    ll->sll_halen == 6 &&
                    ll->sll_addr[0] == 0x52 &&
                    ll->sll_addr[1] == 0x54;
            }
        }
    }
    freeifaddrs(list);
    check(lo_inet, "lo has 127.0.0.1/8");
    check(lo_packet, "lo's link-layer entry names index 1 and the loopback type");
    check(eth_inet && eth_mask && strcmp(eth_text, "10.0.2.15") == 0, "eth0 has the lease's address, 10.0.2.15/24");
    check(eth_packet, "eth0's link-layer entry names index 2 and slirp's 52:54 address");
    check((eth_flags & (IFF_UP | IFF_RUNNING | IFF_BROADCAST | IFF_MULTICAST)) ==
                (IFF_UP | IFF_RUNNING | IFF_BROADCAST | IFF_MULTICAST) &&
            !(eth_flags & IFF_LOOPBACK),
        "eth0 is up, running, broadcast and multicast");
    struct if_nameindex *names = if_nameindex();
    int named = 0;
    for (const struct if_nameindex *n = names; n && n->if_index; ++n) {
        named +=
            (n->if_index == 1 && strcmp(n->if_name, "lo") == 0) || (n->if_index == 2 && strcmp(n->if_name, "eth0") == 0)
            ? 1
            : 100;
    }
    if (names) {
        if_freenameindex(names);
    }
    check(named == 2, "if_nameindex names lo (1) and eth0 (2) alone");
    check(if_nametoindex("lo") == 1 && if_nametoindex("eth0") == 2 && if_nametoindex("eth9") == 0,
        "if_nametoindex knows the two");
    char name[IF_NAMESIZE] = "";
    check(
        if_indextoname(2, name) == name && strcmp(name, "eth0") == 0 && if_indextoname(9, name) == 0 && errno == ENXIO,
        "if_indextoname names index 2 eth0 and no index 9");
    const int udp = socket(AF_INET, SOCK_DGRAM, 0);
    struct ifreq request;
    memset(&request, 0, sizeof(request));
    memcpy(request.ifr_name, "eth0", 5);
    check(udp >= 0 && ioctl(udp, SIOCGIFMTU, &request) == 0 && request.ifr_mtu == 1500, "SIOCGIFMTU of eth0 is 1500");
    const int eth_mtu = request.ifr_mtu;
    check(ioctl(udp, SIOCGIFHWADDR, &request) == 0 &&
            request.ifr_hwaddr.sa_family == ARPHRD_ETHER_TYPE &&
            (unsigned char)request.ifr_hwaddr.sa_data[0] == 0x52 &&
            (unsigned char)request.ifr_hwaddr.sa_data[1] == 0x54,
        "SIOCGIFHWADDR of eth0 is Ethernet with slirp's address");
    check(ioctl(udp, SIOCGIFADDR, &request) == 0 &&
            ((struct sockaddr_in *)&request.ifr_addr)->sin_addr.s_addr == htonl(0x0A00020FU),
        "SIOCGIFADDR of eth0 is 10.0.2.15");
    check(ioctl(udp, SIOCGIFBRDADDR, &request) == 0 &&
            ((struct sockaddr_in *)&request.ifr_broadaddr)->sin_addr.s_addr == htonl(0x0A0002FFU),
        "SIOCGIFBRDADDR of eth0 is 10.0.2.255");
    check(ioctl(udp, SIOCGIFINDEX, &request) == 0 && request.ifr_ifindex == 2, "SIOCGIFINDEX of eth0 is 2");
    memcpy(request.ifr_name, "lo", 3);
    check(ioctl(udp, SIOCGIFMTU, &request) == 0 && request.ifr_mtu == 65536, "SIOCGIFMTU of lo is 65536");
    check(ioctl(udp, SIOCGIFFLAGS, &request) == 0 &&
            (request.ifr_flags & (IFF_UP | IFF_LOOPBACK | IFF_RUNNING)) == (IFF_UP | IFF_LOOPBACK | IFF_RUNNING),
        "SIOCGIFFLAGS of lo is up, loopback, running");
    memcpy(request.ifr_name, "eth9", 5);
    check(ioctl(udp, SIOCGIFMTU, &request) < 0 && errno == ENODEV, "SIOCGIFMTU of eth9 is ENODEV");
    struct ifreq entries[4];
    struct ifconf conf;
    memset(&conf, 0, sizeof(conf));
    check(ioctl(udp, SIOCGIFCONF, &conf) == 0 && conf.ifc_len == 2 * (int)sizeof(struct ifreq),
        "SIOCGIFCONF asks room for two interfaces");
    conf.ifc_len = sizeof(entries);
    conf.ifc_req = entries;
    check(ioctl(udp, SIOCGIFCONF, &conf) == 0 &&
            conf.ifc_len == 2 * (int)sizeof(struct ifreq) &&
            strcmp(entries[0].ifr_name, "lo") == 0 &&
            strcmp(entries[1].ifr_name, "eth0") == 0 &&
            ((struct sockaddr_in *)&entries[1].ifr_addr)->sin_addr.s_addr == htonl(0x0A00020FU),
        "SIOCGIFCONF lists lo and eth0 with their addresses");
    if (udp >= 0) {
        close(udp);
    }
    printf("[SOCKETS] interfaces: lo and eth0 (%s/24, MTU %d), named by getifaddrs, if_nameindex and the interface "
           "ioctls\n",
        eth_text, eth_mtu);
}

/* IPv6 (W4.3). */
static void ipv6(void)
{
    const int a = socket(AF_INET6, SOCK_DGRAM, 0), b = socket(AF_INET6, SOCK_DGRAM, 0);
    check(a >= 0 && b >= 0, "two AF_INET6 datagram sockets");
    int value = -1;
    socklen_t length = sizeof(value);
    check(getsockopt(a, SOL_SOCKET, SO_DOMAIN, &value, &length) == 0 && value == AF_INET6, "SO_DOMAIN is AF_INET6");
    length = sizeof(value);
    check(
        getsockopt(a, IPPROTO_IPV6, IPV6_V6ONLY, &value, &length) == 0 && value == 0, "IPV6_V6ONLY is off by default");
    struct sockaddr_in6 where = address6_of("::1", 0);
    check(bind(a, (struct sockaddr *)&where, sizeof(where)) == 0, "bind to [::1]'s ephemeral port");
    const int port = port6_of(a);
    check(port >= 49152, "getsockname names the ephemeral port in a sockaddr_in6");
    where.sin6_port = htons((unsigned short)port);
    check(sendto(b, "six", 3, 0, (struct sockaddr *)&where, sizeof(where)) == 3, "sendto over ::1");
    struct sockaddr_in6 from;
    length = sizeof(from);
    char buffer[64] = {0};
    check(
        recvfrom(a, buffer, sizeof(buffer), 0, (struct sockaddr *)&from, &length) == 3 && memcmp(buffer, "six", 3) == 0,
        "recvfrom takes the datagram");
    check(length == sizeof(from) &&
            from.sin6_family == AF_INET6 &&
            IN6_IS_ADDR_LOOPBACK(&from.sin6_addr) &&
            ntohs(from.sin6_port) == port6_of(b),
        "recvfrom names the sender, ::1");
    close(a);
    close(b);
    /* A dual-stack listener on [::]: an IPv6 client over ::1 and an IPv4 one over 127.0.0.1, whose peer is mapped. */
    const int listener = socket(AF_INET6, SOCK_STREAM, 0);
    where = address6_of("::", 0);
    check(listener >= 0 && bind(listener, (struct sockaddr *)&where, sizeof(where)) == 0 && listen(listener, 4) == 0,
        "a dual-stack listener on [::]");
    const int lport = port6_of(listener);
    value = 1;
    check(setsockopt(listener, IPPROTO_IPV6, IPV6_V6ONLY, &value, sizeof(value)) < 0 && errno == EINVAL,
        "IPV6_V6ONLY cannot change once bound");
    const int client6 = socket(AF_INET6, SOCK_STREAM, 0);
    where = address6_of("::1", lport);
    check(connect(client6, (struct sockaddr *)&where, sizeof(where)) == 0, "connect over ::1");
    length = sizeof(from);
    const int server6 = accept(listener, (struct sockaddr *)&from, &length);
    check(server6 >= 0 &&
            from.sin6_family == AF_INET6 &&
            IN6_IS_ADDR_LOOPBACK(&from.sin6_addr) &&
            ntohs(from.sin6_port) == port6_of(client6),
        "accept names the IPv6 peer");
    check(write(client6, "hello6", 6) == 6 &&
            read(server6, buffer, sizeof(buffer)) == 6 &&
            memcmp(buffer, "hello6", 6) == 0,
        "bytes over a TCP connection on ::1");
    const int client4 = socket(AF_INET, SOCK_STREAM, 0);
    struct sockaddr_in where4 = address_of("127.0.0.1", lport);
    check(connect(client4, (struct sockaddr *)&where4, sizeof(where4)) == 0,
        "an IPv4 client connects to the dual-stack listener");
    length = sizeof(from);
    const int server4 = accept(listener, (struct sockaddr *)&from, &length);
    check(server4 >= 0 &&
            from.sin6_family == AF_INET6 &&
            IN6_IS_ADDR_V4MAPPED(&from.sin6_addr) &&
            memcmp(&from.sin6_addr.s6_addr[12], "\x7f\x00\x00\x01", 4) == 0 &&
            ntohs(from.sin6_port) == port_of(client4),
        "accept names the IPv4 peer in its mapped form, ::ffff:127.0.0.1");
    check(
        write(server4, "four", 4) == 4 && read(client4, buffer, sizeof(buffer)) == 4 && memcmp(buffer, "four", 4) == 0,
        "bytes between the families");
    struct sockaddr_in peer4;
    length = sizeof(peer4);
    check(getpeername(client4, (struct sockaddr *)&peer4, &length) == 0 &&
            peer4.sin_family == AF_INET &&
            ntohs(peer4.sin_port) == lport,
        "the IPv4 client's peer is AF_INET");
    close(client6);
    close(server6);
    close(client4);
    close(server4);
    close(listener);
    /* IPV6_V6ONLY: no IPv4 at the port, which another AF_INET socket may then bind. */
    const int only = socket(AF_INET6, SOCK_STREAM, 0);
    value = 1;
    check(setsockopt(only, IPPROTO_IPV6, IPV6_V6ONLY, &value, sizeof(value)) == 0, "IPV6_V6ONLY set");
    where = address6_of("::", 0);
    check(bind(only, (struct sockaddr *)&where, sizeof(where)) == 0 && listen(only, 1) == 0, "an IPV6_V6ONLY listener");
    const int oport = port6_of(only);
    const int taker = socket(AF_INET, SOCK_STREAM, 0);
    where4 = address_of("0.0.0.0", oport);
    check(bind(taker, (struct sockaddr *)&where4, sizeof(where4)) == 0,
        "an AF_INET socket binds the IPV6_V6ONLY listener's port");
    close(taker);
    const int refused = socket(AF_INET, SOCK_STREAM, 0);
    where4 = address_of("127.0.0.1", oport);
    check(connect(refused, (struct sockaddr *)&where4, sizeof(where4)) < 0 && errno == ECONNREFUSED,
        "an IPv4 connection to an IPV6_V6ONLY listener is refused");
    close(refused);
    close(only);
    const int v4 = socket(AF_INET, SOCK_STREAM, 0);
    check(setsockopt(v4, IPPROTO_IPV6, IPV6_V6ONLY, &value, sizeof(value)) < 0 && errno == ENOPROTOOPT,
        "IPV6_V6ONLY of an AF_INET socket is ENOPROTOOPT");
    where = address6_of("::1", 1);
    check(bind(v4, (struct sockaddr *)&where, sizeof(where)) < 0 && errno == EAFNOSUPPORT,
        "an AF_INET socket binds no sockaddr_in6");
    close(v4);
    /* localhost's ::1 from /etc/hosts. */
    struct addrinfo hints, *found = 0;
    memset(&hints, 0, sizeof(hints));
    hints.ai_family = AF_INET6;
    hints.ai_socktype = SOCK_STREAM;
    check(getaddrinfo("localhost", "80", &hints, &found) == 0 &&
            found &&
            IN6_IS_ADDR_LOOPBACK(&((struct sockaddr_in6 *)found->ai_addr)->sin6_addr),
        "localhost's ::1 through /etc/hosts");
    if (found) {
        freeaddrinfo(found);
    }
    /* The interfaces' IPv6 addresses: ::1 of lo; the link-local and the global address of eth0, the latter from slirp's
     * router advertisement, each assigned a second after its solicitation went unanswered. */
    char link_local[INET6_ADDRSTRLEN] = "none", global[INET6_ADDRSTRLEN] = "none";
    int lo6 = 0;
    for (int attempt = 0; attempt < 100 && strcmp(global, "none") == 0; ++attempt) {
        struct ifaddrs *list = 0;
        if (attempt) {
            usleep(100000);
        }
        if (getifaddrs(&list) != 0) {
            continue;
        }
        for (const struct ifaddrs *i = list; i; i = i->ifa_next) {
            if (!i->ifa_addr || i->ifa_addr->sa_family != AF_INET6) {
                continue;
            }
            const struct sockaddr_in6 *in = (const struct sockaddr_in6 *)i->ifa_addr;
            if (strcmp(i->ifa_name, "lo") == 0) {
                lo6 = IN6_IS_ADDR_LOOPBACK(&in->sin6_addr);
            } else if (strcmp(i->ifa_name, "eth0") == 0) {
                inet_ntop(AF_INET6, &in->sin6_addr, IN6_IS_ADDR_LINKLOCAL(&in->sin6_addr) ? link_local : global,
                    INET6_ADDRSTRLEN);
            }
        }
        freeifaddrs(list);
    }
    check(lo6, "lo has ::1");
    check(strncmp(link_local, "fe80::", 6) == 0, "eth0 has a link-local address");
    check(strncmp(global, "fec0::", 6) == 0, "eth0 has a global address of slirp's prefix fec0::/64");
    /* /etc/resolv.conf names slirp's IPv6 DNS server once the global address is there, when slirp advertised one: it
     * puts the RDNSS option in its advertisement only where the host has an IPv6 DNS server, which CI's runners lack. */
    FILE *resolv = fopen("/etc/resolv.conf", "r");
    char line[64];
    int dns6 = 0, other6 = 0;
    while (resolv && fgets(line, sizeof(line), resolv)) {
        if (strncmp(line, "nameserver ", 11) == 0 && strchr(line, ':')) {
            if (strcmp(line, "nameserver fec0::3\n") == 0) {
                dns6 = 1;
            } else {
                other6 = 1;
            }
        }
    }
    if (resolv) {
        fclose(resolv);
    }
    check(!other6, "an IPv6 nameserver in /etc/resolv.conf, if any, is slirp's fec0::3");
    printf("[SOCKETS] IPv6: lo ::1, eth0 %s and %s, a dual-stack listener took an IPv4 client\n", link_local, global);
    /* Reported, not required: slirp forwards an IPv6 datagram to fec0::3 to the host's IPv6 DNS server, and a connection
     * to [fec0::2] to the host's ::1, where the host has them. */
    const int query = socket(AF_INET6, SOCK_DGRAM, 0);
    where = address6_of("fec0::3", 53);
    const unsigned char dns[] = {0x12, 0x34, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 7, 'e', 'x', 'a', 'm', 'p', 'l', 'e', 3, 'c',
        'o', 'm', 0, 0, 28, 0, 1};
    const char *answered = "no answer";
    for (int attempt = 0; attempt < 2 && answered[0] == 'n'; ++attempt) {
        if (sendto(query, dns, sizeof(dns), 0, (struct sockaddr *)&where, sizeof(where)) != (ssize_t)sizeof(dns)) {
            answered = "not sent";
            break;
        }
        struct pollfd pfd = {query, POLLIN, 0};
        unsigned char answer[512];
        if (poll(&pfd, 1, 2000) == 1) {
            const ssize_t n = recv(query, answer, sizeof(answer), 0);
            if (n >= 12 && answer[0] == 0x12 && answer[1] == 0x34) {
                answered = (answer[3] & 0xF) == 0 ? "answered" : "answered with an error code";
            }
        }
    }
    close(query);
    const int host = socket(AF_INET6, SOCK_STREAM, 0);
    const struct timeval timeout = {3, 0};
    setsockopt(host, SOL_SOCKET, SO_SNDTIMEO, &timeout, sizeof(timeout));
    where = address6_of("fec0::2", 7);
    const int connected = connect(host, (struct sockaddr *)&where, sizeof(where));
    const int host_errno = errno;
    close(host);
    printf("[SOCKETS] IPv6 over the link: an AAAA query to fec0::3 %s; a connection to [fec0::2]:7 %s; %s\n", answered,
        connected == 0 ? "connected" : strerror(host_errno),
        dns6 ? "the router advertised the DNS server fec0::3" : "the router advertised no DNS server");
}

int main(void)
{
    refusals();
    datagrams();
    streams();
    names();
    interfaces();
    ipv6();
    outbound();
    inbound();
    printf("[SOCKETS] sockets over the network service: %d checks passed, %d failed\n", passed, failed);
    return failed ? 1 : 0;
}
