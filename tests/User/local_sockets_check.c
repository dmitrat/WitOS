#define _GNU_SOURCE
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <pthread.h>
#include <signal.h>
#include <spawn.h>
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/epoll.h>
#include <sys/ioctl.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/un.h>
#include <sys/wait.h>
#include <unistd.h>

/* The libc's local sockets (plan step W4.1): /bin/local-sockets-check, which the runtime-devices scenario's /bin/init
 * starts, a process whose start message carried the namespace service's endpoint. It checks AF_UNIX as programs use it:
 * socketpair's three types (bytes and records, a transfer larger than a ring against a reading thread, shutdown, the end
 * of a stream, EPIPE, ECONNRESET for bytes left unread, nonblocking calls, poll and FIONREAD); named sockets on the
 * volumes of the namespace service (bind, the name as a socket in stat and a listing, EADDRINUSE, ECONNREFUSED before
 * listen and for a file, listen, a nonblocking connect, accept4's flags, the names getsockname and getpeername give, a
 * relative path, epoll on a listener, unlink, ENOENT afterwards, EROFS in the package); abstract names and autobind; the
 * options; and a connection between processes: the same program started as a child connects to a name, echoes what it
 * reads, and reads ECONNRESET from a second connection the listener drops before accepting it. */

#define BIG (200 * 1024)
#define CROSS_BYTES (96 * 1024)
#define CHUNK 8192
#define CROSS_PATH "/data/local-check-cross.sock"
#define DROPPED_PATH "/tmp/local-check-dropped.sock"

static int passed, failed;

static void check(int condition, const char *what)
{
    if (condition) {
        ++passed;
    } else {
        ++failed;
        printf("[LOCAL-SOCKETS] FAILED: %s (errno %d: %s)\n", what, errno, strerror(errno));
    }
}

static unsigned char pattern(long i)
{
    return (unsigned char)(i * 37 + (i >> 8) * 11 + (i >> 16));
}

static socklen_t address_of(struct sockaddr_un *a, const char *path)
{
    memset(a, 0, sizeof(*a));
    a->sun_family = AF_UNIX;
    strncpy(a->sun_path, path, sizeof(a->sun_path) - 1);
    return (socklen_t)(offsetof(struct sockaddr_un, sun_path) + strlen(path) + 1);
}

static socklen_t abstract_of(struct sockaddr_un *a, const char *name)
{
    memset(a, 0, sizeof(*a));
    a->sun_family = AF_UNIX;
    memcpy(a->sun_path + 1, name, strlen(name));
    return (socklen_t)(offsetof(struct sockaddr_un, sun_path) + 1 + strlen(name));
}

static long read_all(int fd, unsigned char *buffer, long bytes)
{
    long got = 0;
    while (got < bytes) {
        const ssize_t n = read(fd, buffer + got, (size_t)(bytes - got));
        if (n <= 0) {
            break;
        }
        got += n;
    }
    return got;
}

typedef struct Writer {
    int Fd;
    long Bytes;
    long Written;
} Writer;

static void *write_pattern(void *argument)
{
    Writer *w = argument;
    static unsigned char chunk[CHUNK];
    for (long at = 0; at < w->Bytes;) {
        const long piece = w->Bytes - at < CHUNK ? w->Bytes - at : CHUNK;
        for (long i = 0; i < piece; ++i) {
            chunk[i] = pattern(at + i);
        }
        const ssize_t n = write(w->Fd, chunk, (size_t)piece);
        if (n <= 0) {
            break;
        }
        at += n;
        w->Written = at;
    }
    return 0;
}

/* Reads bytes of the pattern from a descriptor, as many as asked, and counts those that differ. */
static long read_pattern(int fd, long bytes, long *wrong)
{
    static unsigned char chunk[CHUNK];
    long got = 0;
    *wrong = 0;
    while (got < bytes) {
        const ssize_t n = read(fd, chunk, sizeof(chunk));
        if (n <= 0) {
            break;
        }
        for (ssize_t i = 0; i < n; ++i) {
            *wrong += chunk[i] != pattern(got + i);
        }
        got += n;
    }
    return got;
}

static void pairs(void)
{
    int sv[2];
    char buffer[64];
    check(socketpair(AF_UNIX, SOCK_STREAM, 0, sv) == 0, "socketpair of a stream");
    check(write(sv[0], "ping", 4) == 4 && read(sv[1], buffer, sizeof(buffer)) == 4 && !memcmp(buffer, "ping", 4),
        "a pair's bytes one way");
    check(send(sv[1], "pong!", 5, 0) == 5 &&
            recv(sv[0], buffer, 2, MSG_PEEK) == 2 &&
            recv(sv[0], buffer, sizeof(buffer), 0) == 5 &&
            !memcmp(buffer, "pong!", 5),
        "the other way, MSG_PEEK first");
    /* More than a ring holds, against a reading thread. */
    Writer writer = {sv[0], BIG, 0};
    pthread_t thread;
    long wrong = 0;
    check(pthread_create(&thread, 0, write_pattern, &writer) == 0, "a writing thread");
    const long got = read_pattern(sv[1], BIG, &wrong);
    pthread_join(thread, 0);
    check(got == BIG && wrong == 0 && writer.Written == BIG, "200 KiB through a pair against a reading thread");
    /* shutdown: the end of the stream one way, the other way still open. */
    check(
        shutdown(sv[0], SHUT_WR) == 0 && read(sv[1], buffer, sizeof(buffer)) == 0, "SHUT_WR is the end of the stream");
    check(write(sv[1], "back", 4) == 4 && read(sv[0], buffer, sizeof(buffer)) == 4, "the other way after SHUT_WR");
    errno = 0;
    check(send(sv[0], "x", 1, MSG_NOSIGNAL) < 0 && errno == EPIPE, "a send after SHUT_WR is EPIPE");
    close(sv[0]);
    check(read(sv[1], buffer, sizeof(buffer)) == 0, "the end of the stream once the peer closed");
    errno = 0;
    check(send(sv[1], "x", 1, MSG_NOSIGNAL) < 0 && errno == EPIPE, "EPIPE once the peer closed");
    errno = 0;
    check(write(sv[1], "x", 1) < 0 && errno == EPIPE, "write raises SIGPIPE, ignored, and answers EPIPE");
    close(sv[1]);

    /* A side that closes with bytes unread resets the connection. */
    check(socketpair(AF_UNIX, SOCK_STREAM, 0, sv) == 0 && write(sv[0], "lost", 4) == 4, "a pair with bytes in flight");
    close(sv[1]);
    errno = 0;
    check(read(sv[0], buffer, sizeof(buffer)) < 0 && errno == ECONNRESET && read(sv[0], buffer, sizeof(buffer)) == 0,
        "ECONNRESET once when the peer closed with bytes unread, then the end");
    close(sv[0]);

    /* Records keep their boundaries; a short buffer cuts one. */
    struct iovec vector = {buffer, 3};
    struct msghdr message = {0};
    message.msg_iov = &vector;
    message.msg_iovlen = 1;
    check(socketpair(AF_UNIX, SOCK_SEQPACKET, 0, sv) == 0, "socketpair of sequenced packets");
    check(send(sv[0], "first", 5, 0) == 5 && send(sv[0], "", 0, 0) == 0 && send(sv[0], "second", 6, 0) == 6,
        "three records");
    check(recv(sv[1], buffer, sizeof(buffer), 0) == 5 &&
            recv(sv[1], buffer, sizeof(buffer), 0) == 0 &&
            recvmsg(sv[1], &message, 0) == 3 &&
            (message.msg_flags & MSG_TRUNC) &&
            !memcmp(buffer, "sec", 3),
        "records whole, an empty one, a cut one with MSG_TRUNC");
    close(sv[0]);
    close(sv[1]);
    check(socketpair(AF_UNIX, SOCK_DGRAM, 0, sv) == 0 &&
            send(sv[1], "one", 3, 0) == 3 &&
            send(sv[1], "two!", 4, 0) == 4 &&
            recv(sv[0], buffer, sizeof(buffer), MSG_TRUNC) == 3 &&
            recv(sv[0], buffer, 2, MSG_TRUNC) == 4,
        "datagrams of a pair, MSG_TRUNC's whole length");
    close(sv[0]);
    close(sv[1]);

    /* Nonblocking calls, poll and FIONREAD. */
    struct pollfd p = {0};
    int queued = -1;
    check(socketpair(AF_UNIX, SOCK_STREAM | SOCK_NONBLOCK | SOCK_CLOEXEC, 0, sv) == 0, "a nonblocking pair");
    errno = 0;
    check(read(sv[1], buffer, sizeof(buffer)) < 0 && errno == EAGAIN, "EAGAIN with nothing to read");
    p.fd = sv[1];
    p.events = POLLIN | POLLOUT;
    check(poll(&p, 1, 0) == 1 && p.revents == POLLOUT, "poll: writable, nothing to read");
    check(
        write(sv[0], "abc", 3) == 3 && poll(&p, 1, 1000) == 1 && (p.revents & POLLIN), "poll: readable after a write");
    check(ioctl(sv[1], FIONREAD, &queued) == 0 && queued == 3, "FIONREAD");
    check(
        (fcntl(sv[0], F_GETFD) & FD_CLOEXEC) && (fcntl(sv[0], F_GETFL) & O_NONBLOCK), "SOCK_CLOEXEC and SOCK_NONBLOCK");
    struct stat st;
    check(fstat(sv[0], &st) == 0 && S_ISSOCK(st.st_mode), "fstat says S_IFSOCK");
    close(sv[0]);
    p.events = POLLIN;
    check(poll(&p, 1, 1000) == 1 && (p.revents & (POLLIN | POLLHUP)) == (POLLIN | POLLHUP),
        "poll: POLLIN and POLLHUP once the peer closed");
    close(sv[1]);
}

static void refusals(void)
{
    errno = 0;
    check(socket(AF_UNIX, SOCK_DGRAM, 0) < 0 && errno == ESOCKTNOSUPPORT,
        "a datagram socket of its own is ESOCKTNOSUPPORT");
    errno = 0;
    check(socket(AF_UNIX, SOCK_STREAM, 7) < 0 && errno == EPROTONOSUPPORT, "an unknown protocol is EPROTONOSUPPORT");
    const int fd = socket(AF_UNIX, SOCK_STREAM, 0);
    struct sockaddr_un a;
    socklen_t length = sizeof(a);
    errno = 0;
    check(getpeername(fd, (struct sockaddr *)&a, &length) < 0 && errno == ENOTCONN,
        "getpeername of an unconnected socket");
    errno = 0;
    check(listen(fd, 1) < 0 && errno == EINVAL, "listen on an unbound socket is EINVAL");
    errno = 0;
    check(bind(fd, (struct sockaddr *)&a, address_of(&a, "/bin/local.sock")) < 0 && errno == EROFS,
        "a bind in the package is EROFS");
    errno = 0;
    check(connect(fd, (struct sockaddr *)&a, address_of(&a, "/bin/init")) < 0 && errno == ECONNREFUSED,
        "a connection to a file is ECONNREFUSED");
    errno = 0;
    check(connect(fd, (struct sockaddr *)&a, address_of(&a, "/data/none.sock")) < 0 && errno == ENOENT,
        "a connection to no name is ENOENT");
    int type = 0, domain = 0;
    socklen_t size = sizeof(type);
    struct ucred credentials;
    socklen_t credentials_size = sizeof(credentials);
    check(getsockopt(fd, SOL_SOCKET, SO_TYPE, &type, &size) == 0 &&
            type == SOCK_STREAM &&
            getsockopt(fd, SOL_SOCKET, SO_DOMAIN, &domain, &size) == 0 &&
            domain == AF_UNIX &&
            getsockopt(fd, SOL_SOCKET, SO_PEERCRED, &credentials, &credentials_size) == 0 &&
            credentials.uid == 0,
        "SO_TYPE, SO_DOMAIN and SO_PEERCRED");
    close(fd);
}

typedef struct Echo {
    int Listener;
    int Accepted;
    long Echoed;
} Echo;

/* The server's side of a named connection: accept, echo until the end of the stream. */
static void *echo(void *argument)
{
    Echo *e = argument;
    static unsigned char chunk[CHUNK];
    struct sockaddr_un peer;
    socklen_t length = sizeof(peer);
    e->Accepted = accept4(e->Listener, (struct sockaddr *)&peer, &length, SOCK_CLOEXEC);
    if (e->Accepted < 0) {
        return 0;
    }
    for (ssize_t n; (n = read(e->Accepted, chunk, sizeof(chunk))) > 0;) {
        if (write(e->Accepted, chunk, (size_t)n) != n) {
            break;
        }
        e->Echoed += n;
    }
    shutdown(e->Accepted, SHUT_WR);
    return 0;
}

static void named(void)
{
    const char *path = "/tmp/local-check.sock";
    struct sockaddr_un a, b;
    socklen_t length;
    struct stat st;
    unlink(path);
    const int listener = socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC, 0);
    check(bind(listener, (struct sockaddr *)&a, address_of(&a, path)) == 0, "bind of /tmp/local-check.sock");
    check(stat(path, &st) == 0 && S_ISSOCK(st.st_mode) && (st.st_mode & 0777) == 0755, "the name is a socket, 0755");
    const int other = socket(AF_UNIX, SOCK_STREAM, 0);
    errno = 0;
    check(bind(other, (struct sockaddr *)&a, address_of(&a, path)) < 0 && errno == EADDRINUSE,
        "a second bind is EADDRINUSE");
    errno = 0;
    check(connect(other, (struct sockaddr *)&a, address_of(&a, path)) < 0 && errno == ECONNREFUSED,
        "a connection before listen is ECONNREFUSED");
    close(other);
    errno = 0;
    check(open(path, O_RDONLY) < 0 && errno == ENXIO, "open of a socket's name is ENXIO");
    errno = 0;
    check(mkdir(path, 0755) < 0 && errno == EEXIST, "mkdir over a socket's name is EEXIST");
    int found = 0;
    DIR *directory = opendir("/tmp");
    for (struct dirent *entry; directory && (entry = readdir(directory));) {
        found |= !strcmp(entry->d_name, "local-check.sock") && entry->d_type == DT_SOCK;
    }
    if (directory) {
        closedir(directory);
    }
    check(found, "the listing of /tmp shows the socket with DT_SOCK");
    check(listen(listener, 8) == 0, "listen");
    length = sizeof(b);
    check(getsockname(listener, (struct sockaddr *)&b, &length) == 0 &&
            length == offsetof(struct sockaddr_un, sun_path) + strlen(path) + 1 &&
            !strcmp(b.sun_path, path),
        "getsockname gives the path");

    /* A listener in epoll: readable once a client is queued. */
    const int instance = epoll_create1(EPOLL_CLOEXEC);
    struct epoll_event event = {.events = EPOLLIN | EPOLLET, .data.u64 = 7};
    check(epoll_ctl(instance, EPOLL_CTL_ADD, listener, &event) == 0, "a listener registered in epoll");
    struct epoll_event got[2];
    check(epoll_wait(instance, got, 2, 0) == 0, "nothing queued yet");
    const int client = socket(AF_UNIX, SOCK_STREAM | SOCK_NONBLOCK, 0);
    check(connect(client, (struct sockaddr *)&a, address_of(&a, path)) == 0,
        "a nonblocking connect completes once queued");
    check(epoll_wait(instance, got, 2, 1000) == 1 && got[0].data.u64 == 7 && (got[0].events & EPOLLIN),
        "epoll: the listener is readable");
    close(instance);
    length = sizeof(b);
    check(getpeername(client, (struct sockaddr *)&b, &length) == 0 && !strcmp(b.sun_path, path),
        "the client's peer is the path");
    const int flags = fcntl(client, F_GETFL);
    fcntl(client, F_SETFL, flags & ~O_NONBLOCK);

    /* 256 KiB echoed through the named connection by an accepting thread. */
    Echo e = {listener, -1, 0};
    pthread_t thread;
    Writer writer = {client, 256 * 1024, 0};
    pthread_t writing;
    long wrong = 0;
    check(pthread_create(&thread, 0, echo, &e) == 0 && pthread_create(&writing, 0, write_pattern, &writer) == 0,
        "an accepting and a writing thread");
    const long back = read_pattern(client, 256 * 1024, &wrong);
    pthread_join(writing, 0);
    shutdown(client, SHUT_WR);
    char rest[8];
    check(read(client, rest, sizeof(rest)) == 0, "the end of the echo's stream");
    pthread_join(thread, 0);
    check(back == 256 * 1024 && wrong == 0 && e.Echoed == 256 * 1024, "256 KiB echoed by the accepted socket");
    length = sizeof(b);
    check(e.Accepted >= 0 &&
            getsockname(e.Accepted, (struct sockaddr *)&b, &length) == 0 &&
            !strcmp(b.sun_path, path) &&
            (fcntl(e.Accepted, F_GETFD) & FD_CLOEXEC),
        "the accepted socket's name is the listener's, accept4's SOCK_CLOEXEC");
    length = sizeof(b);
    check(getpeername(e.Accepted, (struct sockaddr *)&b, &length) == 0 && length == sizeof(sa_family_t),
        "an unnamed client's address is the family alone");
    close(e.Accepted);
    close(client);

    /* unlink takes the name away; the socket stays. */
    check(unlink(path) == 0 && stat(path, &st) < 0 && errno == ENOENT, "unlink takes the name away");
    const int late = socket(AF_UNIX, SOCK_STREAM, 0);
    errno = 0;
    check(connect(late, (struct sockaddr *)&a, address_of(&a, path)) < 0 && errno == ENOENT,
        "no connection after the unlink");
    close(late);
    close(listener);

    /* A relative path from the current directory, on the ext4 volume. */
    const int relative = socket(AF_UNIX, SOCK_STREAM, 0);
    char cwd[256];
    getcwd(cwd, sizeof(cwd));
    check(chdir("/data") == 0 && bind(relative, (struct sockaddr *)&a, address_of(&a, "rel.sock")) == 0,
        "bind of a relative path");
    length = sizeof(b);
    check(getsockname(relative, (struct sockaddr *)&b, &length) == 0 &&
            !strcmp(b.sun_path, "rel.sock") &&
            stat("/data/rel.sock", &st) == 0 &&
            S_ISSOCK(st.st_mode),
        "the relative name, a socket in /data");
    close(relative);
    check(stat("/data/rel.sock", &st) == 0 && unlink("/data/rel.sock") == 0,
        "the name outlives its socket until unlinked");
    chdir(cwd);
}

static void abstract(void)
{
    struct sockaddr_un a, b;
    socklen_t length;
    char buffer[16];
    const int listener = socket(AF_UNIX, SOCK_SEQPACKET, 0);
    const socklen_t named = abstract_of(&a, "witos-local-check");
    check(bind(listener, (struct sockaddr *)&a, named) == 0 && listen(listener, 1) == 0, "an abstract name, listening");
    const int other = socket(AF_UNIX, SOCK_SEQPACKET, 0);
    errno = 0;
    check(bind(other, (struct sockaddr *)&a, named) < 0 && errno == EADDRINUSE, "an abstract name is taken");
    close(other);
    const int stream = socket(AF_UNIX, SOCK_STREAM, 0);
    errno = 0;
    check(connect(stream, (struct sockaddr *)&a, named) < 0 && errno == EPROTOTYPE,
        "a stream to a sequenced packet is EPROTOTYPE");
    close(stream);
    const int client = socket(AF_UNIX, SOCK_SEQPACKET, 0);
    check(connect(client, (struct sockaddr *)&a, named) == 0, "a connection to the abstract name");
    const int accepted = accept(listener, 0, 0);
    check(accepted >= 0 &&
            send(client, "rec", 3, 0) == 3 &&
            send(client, "ords", 4, 0) == 4 &&
            recv(accepted, buffer, sizeof(buffer), 0) == 3 &&
            recv(accepted, buffer, sizeof(buffer), 0) == 4,
        "records across the accepted connection");
    length = sizeof(b);
    check(getsockname(listener, (struct sockaddr *)&b, &length) == 0 &&
            length == named &&
            b.sun_path[0] == 0 &&
            !memcmp(b.sun_path + 1, "witos-local-check", 17),
        "getsockname gives the abstract name");
    close(accepted);
    close(client);
    close(listener);
    const int late = socket(AF_UNIX, SOCK_SEQPACKET, 0);
    errno = 0;
    check(connect(late, (struct sockaddr *)&a, named) < 0 && errno == ECONNREFUSED,
        "the abstract name went with its socket");
    close(late);
    /* autobind: an address of the family alone. */
    const int automatic = socket(AF_UNIX, SOCK_STREAM, 0);
    struct sockaddr_un family = {.sun_family = AF_UNIX};
    length = sizeof(b);
    check(bind(automatic, (struct sockaddr *)&family, sizeof(sa_family_t)) == 0 &&
            getsockname(automatic, (struct sockaddr *)&b, &length) == 0 &&
            length == sizeof(sa_family_t) + 6 &&
            b.sun_path[0] == 0,
        "autobind gives an abstract name of five digits");
    close(automatic);
}

/* The parent's side of the connection between processes. */
static int cross(const char *self)
{
    struct sockaddr_un a;
    unlink(CROSS_PATH);
    unlink(DROPPED_PATH);
    const int listener = socket(AF_UNIX, SOCK_STREAM, 0);
    const int dropped = socket(AF_UNIX, SOCK_STREAM, 0);
    check(bind(listener, (struct sockaddr *)&a, address_of(&a, CROSS_PATH)) == 0 &&
            listen(listener, 4) == 0 &&
            bind(dropped, (struct sockaddr *)&a, address_of(&a, DROPPED_PATH)) == 0 &&
            listen(dropped, 4) == 0,
        "two listeners for the child");
    char *argv[] = {(char *)self, "child", 0};
    pid_t child = 0;
    extern char **environ;
    check(posix_spawn(&child, self, 0, 0, argv, environ) == 0, "the child started");
    const int accepted = accept(listener, 0, 0);
    check(accepted >= 0, "the child's connection accepted");
    Writer writer = {accepted, CROSS_BYTES, 0};
    pthread_t writing;
    long wrong = 0;
    check(pthread_create(&writing, 0, write_pattern, &writer) == 0, "a writing thread");
    const long back = read_pattern(accepted, CROSS_BYTES, &wrong);
    pthread_join(writing, 0);
    check(back == CROSS_BYTES && wrong == 0, "96 KiB echoed by the child");
    shutdown(accepted, SHUT_WR);
    char rest[8];
    check(read(accepted, rest, sizeof(rest)) == 0, "the child ended the stream");
    close(accepted);
    /* The second connection waits queued; closing its listener drops it, and the child reads ECONNRESET. */
    struct pollfd p = {dropped, POLLIN, 0};
    check(poll(&p, 1, 60000) == 1 && (p.revents & POLLIN), "the child's second connection queued");
    close(dropped);
    close(listener);
    int status = 0;
    check(waitpid(child, &status, 0) == child && WIFEXITED(status) && WEXITSTATUS(status) == 0,
        "the child exited with 0");
    unlink(CROSS_PATH);
    unlink(DROPPED_PATH);
    printf("[LOCAL-SOCKETS] %d bytes echoed by another process through %s, a dropped connection reset there\n",
        CROSS_BYTES, CROSS_PATH);
    return 0;
}

/* The child: echo the parent's bytes until the end of the stream, then read ECONNRESET from a dropped connection. */
static int child(void)
{
    struct sockaddr_un a;
    static unsigned char chunk[CHUNK];
    const int fd = socket(AF_UNIX, SOCK_STREAM, 0);
    if (connect(fd, (struct sockaddr *)&a, address_of(&a, CROSS_PATH)) != 0) {
        printf("[LOCAL-SOCKETS] the child's connection failed (errno %d)\n", errno);
        return 1;
    }
    long echoed = 0;
    for (ssize_t n; (n = read(fd, chunk, sizeof(chunk))) > 0;) {
        if (write(fd, chunk, (size_t)n) != n) {
            return 2;
        }
        echoed += n;
    }
    shutdown(fd, SHUT_WR);
    close(fd);
    const int second = socket(AF_UNIX, SOCK_STREAM, 0);
    if (connect(second, (struct sockaddr *)&a, address_of(&a, DROPPED_PATH)) != 0) {
        printf("[LOCAL-SOCKETS] the child's second connection failed (errno %d)\n", errno);
        return 3;
    }
    errno = 0;
    const ssize_t r = read(second, chunk, 1);
    if (r >= 0 || errno != ECONNRESET) {
        printf("[LOCAL-SOCKETS] the dropped connection read %ld (errno %d), not ECONNRESET\n", (long)r, errno);
        return 4;
    }
    close(second);
    return echoed == CROSS_BYTES ? 0 : 5;
}

int main(int argc, char **argv)
{
    if (argc == 2 && !strcmp(argv[1], "child")) {
        return child();
    }
    signal(SIGPIPE, SIG_IGN);
    refusals();
    pairs();
    named();
    abstract();
    cross(argv[0]);
    printf("[LOCAL-SOCKETS] local sockets: %d checks passed, %d failed\n", passed, failed);
    return failed ? 1 : 0;
}
