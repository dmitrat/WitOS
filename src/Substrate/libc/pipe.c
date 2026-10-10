#define _GNU_SOURCE
#include "witos_libc.h"
#include <errno.h>
#include <poll.h>
#include <signal.h>
#include <string.h>
#include <sys/mman.h>

/* Pipes within the process (plan step R2.3a): a pipe is a ring buffer of anonymous memory with its own lock, and a
 * reader that finds it empty or a writer that finds it full parks on a futex word that every change bumps. NativeAOT's
 * System.Native needs one for its signal handling thread before Console writes a byte; descriptors of other processes
 * and posix_spawn's file actions do not reach a pipe (S6.2, the namespace service is D5).
 *
 * Two locks: the table lock of __wit_syscall guards the descriptors and each pipe's References (the descriptors of
 * its ends and the transfers in flight, so a pipe outlives a close that races a blocked read), and the pipe's own
 * lock guards its bytes and the counts of open ends. The table lock comes first; a transfer holds neither while it
 * parks. Linux's semantics: a read of an empty pipe whose writers are all closed is the end of the file, a write to a
 * pipe without a reader fails with EPIPE after raising SIGPIPE, writes of at most PIPE_BUF bytes are atomic, and an
 * end opened with O_NONBLOCK answers EAGAIN instead of parking. poll (R3.2) reads an end's readiness as Linux reports
 * it and parks on one word that every change of every pipe bumps, since pipes are the only descriptors that wait. */

#define PIPES 16U
#define PIPE_BYTES 16384U /* Linux's PIPE_BUF (4096) fits four times */
#define PIPE_ATOMIC 4096U
#define FUTEX_WAIT 0
#define FUTEX_WAKE 1
#define FUTEX_PRIVATE 128

typedef struct Pipe {
    volatile int Lock;
    volatile int Sequence; /* bumped and woken on every change of the bytes or the open ends */
    WitU32 Readers, Writers; /* open descriptors of each end, under Lock */
    WitU32 References; /* descriptors and transfers in flight, under the table lock */
    WitU32 Head, Count;
    unsigned char *Buffer;
} Pipe;

static Pipe pipes[PIPES];
static volatile int poll_sequence; /* bumped and woken on every change of any pipe */

static void changed(Pipe *p)
{
    __atomic_add_fetch(&p->Sequence, 1, __ATOMIC_RELEASE);
    __wit_futex(&p->Sequence, FUTEX_WAKE | FUTEX_PRIVATE, 0x7FFFFFFF, 0, 0, 0);
    __atomic_add_fetch(&poll_sequence, 1, __ATOMIC_RELEASE);
    __wit_futex(&poll_sequence, FUTEX_WAKE | FUTEX_PRIVATE, 0x7FFFFFFF, 0, 0, 0);
}

/* Parks until the sequence moves on from what the caller saw under the lock: -EINTR when a signal ran a handler. */
static long park(Pipe *p, int seen)
{
    const long r = __wit_futex(&p->Sequence, FUTEX_WAIT | FUTEX_PRIVATE, seen, 0, 0, 0);
    return r == -EINTR ? -EINTR : 0;
}

/* A new pipe with one reader and one writer, both descriptors referencing it. Under the table lock. */
long __wit_pipe_create(WitU32 *index)
{
    for (WitU32 i = 0; i < PIPES; ++i) {
        Pipe *p = &pipes[i];
        if (p->References) {
            continue;
        }
        const long buffer = __wit_mmap(0, PIPE_BYTES, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
        if (buffer < 0) {
            return buffer;
        }
        p->Buffer = (unsigned char *)buffer;
        p->Head = 0;
        p->Count = 0;
        p->Readers = 1;
        p->Writers = 1;
        p->References = 2;
        *index = i;
        return 0;
    }
    return -ENFILE;
}

/* A reference for a descriptor or a transfer in flight. Under the table lock. */
void __wit_pipe_reference(WitU32 index)
{
    ++pipes[index].References;
}

/* The last reference frees the buffer. Under the table lock, which the unmapping needs. */
void __wit_pipe_release(WitU32 index)
{
    Pipe *p = &pipes[index];
    if (--p->References == 0) {
        __wit_munmap((long)p->Buffer, PIPE_BYTES);
        p->Buffer = 0;
    }
}

/* An end gained a descriptor (dup) or lost one (close); waiters see the change. */
void __wit_pipe_end(WitU32 index, int write_end, int delta)
{
    Pipe *p = &pipes[index];
    __wit_lock(&p->Lock);
    if (write_end) {
        p->Writers += (WitU32)delta;
    } else {
        p->Readers += (WitU32)delta;
    }
    changed(p);
    __wit_unlock(&p->Lock);
}

/* What a reader takes: the bytes there are, at least one, or the end of the file once no writer is left. */
long __wit_pipe_read(WitU32 index, unsigned char *buffer, unsigned long bytes, int nonblocking)
{
    Pipe *p = &pipes[index];
    if (bytes == 0) {
        return 0;
    }
    for (;;) {
        __wit_lock(&p->Lock);
        if (p->Count) {
            unsigned long done = 0;
            while (done < bytes && p->Count) {
                buffer[done++] = p->Buffer[p->Head];
                p->Head = (p->Head + 1) % PIPE_BYTES;
                --p->Count;
            }
            changed(p);
            __wit_unlock(&p->Lock);
            return (long)done;
        }
        if (!p->Writers) {
            __wit_unlock(&p->Lock);
            return 0;
        }
        const int seen = p->Sequence;
        __wit_unlock(&p->Lock);
        if (nonblocking) {
            return -EAGAIN;
        }
        if (park(p, seen) == -EINTR) {
            return -EINTR;
        }
    }
}

/* A writer puts every byte, parking while the pipe is full; at most PIPE_ATOMIC bytes go in one piece. */
long __wit_pipe_write(WitU32 index, const unsigned char *buffer, unsigned long bytes, int nonblocking)
{
    Pipe *p = &pipes[index];
    unsigned long done = 0;
    while (done < bytes) {
        __wit_lock(&p->Lock);
        if (!p->Readers) {
            __wit_unlock(&p->Lock);
            if (done) {
                return (long)done;
            }
            __wit_tkill((int)__wit_gettid(), SIGPIPE);
            return -EPIPE;
        }
        const unsigned long room = PIPE_BYTES - p->Count;
        const unsigned long wanted = bytes - done;
        /* A write of at most PIPE_ATOMIC bytes waits for room for all of them, never interleaving with another. */
        if (room && (wanted > PIPE_ATOMIC || room >= wanted)) {
            const unsigned long chunk = wanted < room ? wanted : room;
            for (unsigned long i = 0; i < chunk; ++i) {
                p->Buffer[(p->Head + p->Count) % PIPE_BYTES] = buffer[done + i];
                ++p->Count;
            }
            done += chunk;
            changed(p);
            __wit_unlock(&p->Lock);
            continue;
        }
        const int seen = p->Sequence;
        __wit_unlock(&p->Lock);
        if (nonblocking) {
            return done ? (long)done : -EAGAIN;
        }
        if (park(p, seen) == -EINTR) {
            return done ? (long)done : -EINTR;
        }
    }
    return (long)done;
}

/* An end's readiness for poll, as Linux reports a pipe's: the read end is readable with bytes and hung up once no
 * writer is left; the write end is writable while PIPE_BUF bytes fit and in error once no reader is left. */
short __wit_pipe_poll(WitU32 index, int write_end, short events)
{
    Pipe *p = &pipes[index];
    short ready = 0;
    __wit_lock(&p->Lock);
    if (write_end) {
        ready = (short)((PIPE_BYTES - p->Count >= PIPE_ATOMIC ? events & (POLLOUT | POLLWRNORM) : 0) |
            (p->Readers ? 0 : POLLERR));
    } else {
        ready = (short)((p->Count ? events & (POLLIN | POLLRDNORM) : 0) | (p->Writers ? 0 : POLLHUP));
    }
    __wit_unlock(&p->Lock);
    return ready;
}

/* The word poll parks on: read before the readiness so that a change between the two is not lost. */
int __wit_pipe_sequence(void)
{
    return __atomic_load_n(&poll_sequence, __ATOMIC_ACQUIRE);
}

/* Parks until a pipe changed after the sequence the caller saw: 0, -ETIMEDOUT at the deadline, -EINTR after a handler. */
long __wit_pipe_wait(int seen, WitU64 deadline)
{
    const long r = __wit_futex_wait_until(&poll_sequence, seen, deadline);
    return r == -EAGAIN ? 0 : r;
}

/* Bytes a read would take now (FIONREAD). */
long __wit_pipe_available(WitU32 index)
{
    Pipe *p = &pipes[index];
    __wit_lock(&p->Lock);
    const long count = (long)p->Count;
    __wit_unlock(&p->Lock);
    return count;
}
