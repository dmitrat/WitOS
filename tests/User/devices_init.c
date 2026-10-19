#define _GNU_SOURCE
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <mntent.h>
#include <spawn.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <sys/wait.h>
#include <unistd.h>

/* /bin/init of the runtime-devices scenario (plan steps D1.1 and D5.1, ADR 0025): the root task starts it only after
 * the device manager, /system/DeviceManager, answered that its devices are discovered, so its first line follows every
 * discovery line in the log. Then it uses the files of the volumes the namespace service mounts through the libc's
 * POSIX calls, as any program does: on /data (the data disk's ext4 partition) and /fat (its FAT partition) it makes,
 * writes, reads, appends to, cuts, lists, renames and removes files and directories and checks each errno value POSIX
 * gives, and maps a file privately; on /boot (the read-only boot disk) it reads the loader and is refused a write. It
 * leaves /data/posix-kept.txt for the host to find after the boot. Then it starts three programs from the ext4 volume,
 * none of which the boot package holds (D5.3), and waits for each: the System.IO check, an ordinary NativeAOT program
 * that uses the same volumes (D5.2); the dynamic program, whose library and plugin the dynamic linker maps from beside
 * it; and the package's dotnet with a .NET application and its library. It counts the boots of the data disk in
 * /data/boots.txt (D6.1); on its third boot it only writes rounds of files until the host cuts the machine off, on a
 * later boot it checks what the cut left (D6.2), and on its fifth, whose disk fails some reads, it requires EIO for a
 * read of the failing sector and the rest of the volume to read (D6.3). */

/* What /bin/init and the .NET program leave on /data, which the host and a later boot read back. */
#define POSIX_KEPT "Written by /bin/init through the libc and the namespace service."
#define DOTNET_KEPT "Written by a .NET program through System.IO and the namespace service."

/* The boot of the data disk the host cuts off while it writes (D6.2), and what each round of its writes makes. */
#define CUT_BOOT 3
#define ROUND_BYTES 65536

/* The boot whose disk fails every read of /data/big.bin's first block (D6.3). */
#define FAIL_BOOT 5

extern char **environ;

static int passed, failed;

static void check(int condition, const char *what)
{
    if (condition) {
        ++passed;
    } else {
        ++failed;
        printf("[DEVICES-INIT] failed: %s (errno %d)\n", what, errno);
    }
}

static int write_file(const char *path, const char *text, int flags)
{
    const int fd = open(path, O_WRONLY | O_CREAT | flags, 0644);
    if (fd < 0) {
        return -1;
    }
    const ssize_t written = write(fd, text, strlen(text));
    close(fd);
    return written == (ssize_t)strlen(text) ? 0 : -1;
}

static ssize_t read_file(const char *path, char *buffer, size_t size)
{
    const int fd = open(path, O_RDONLY);
    if (fd < 0) {
        return -1;
    }
    const ssize_t read_bytes = read(fd, buffer, size - 1);
    close(fd);
    buffer[read_bytes > 0 ? read_bytes : 0] = 0;
    return read_bytes;
}

/* The same calls on one writable volume; modes says whether its format keeps a mode (ext4, /tmp) or read-only alone
 * (FAT, N4.1). */
static void volume(const char *root, int modes)
{
    char path[256], other[256], buffer[256];
    struct stat st;
    snprintf(path, sizeof(path), "%s/posix", root);
    check(mkdir(path, 0755) == 0, "mkdir");
    check(mkdir(path, 0755) < 0 && errno == EEXIST, "mkdir of an existing directory is EEXIST");
    snprintf(path, sizeof(path), "%s/posix/file.txt", root);
    check(write_file(path, "hello, ", O_TRUNC) == 0, "open with O_CREAT and write");
    check(write_file(path, "world", O_APPEND) == 0, "write with O_APPEND");
    check(read_file(path, buffer, sizeof(buffer)) == 12 && strcmp(buffer, "hello, world") == 0, "read back");
    check(stat(path, &st) == 0 && S_ISREG(st.st_mode) && st.st_size == 12, "stat");
    /* chmod (N4.1): a volume that records a mode keeps the bits; FAT keeps read-only alone and refuses the rest. */
    if (modes) {
        check(chmod(path, 0640) == 0 && stat(path, &st) == 0 && (st.st_mode & 07777) == 0640, "chmod keeps the mode");
        int modes_fd = open(path, O_RDWR);
        check(modes_fd >= 0 && fchmod(modes_fd, 0600) == 0 && fstat(modes_fd, &st) == 0 && (st.st_mode & 07777) == 0600,
            "fchmod keeps the mode");
        if (modes_fd >= 0) {
            close(modes_fd);
        }
        check(chmod(path, 0644) == 0, "chmod back");
        snprintf(other, sizeof(other), "%s/posix", root);
        check(chmod(other, 0777) == 0 && stat(other, &st) == 0 && (st.st_mode & 07777) == 0777, "chmod of a directory");
        /* utimensat and futimens (N4.1): the times as given, to the nanosecond; UTIME_NOW is the service's clock. */
        const struct timespec stamps[2] = {{1600000000, 0}, {1600000001, 500000000}};
        check(utimensat(AT_FDCWD, path, stamps, 0) == 0 &&
                stat(path, &st) == 0 &&
                st.st_atim.tv_sec == 1600000000 &&
                st.st_mtim.tv_sec == 1600000001 &&
                st.st_mtim.tv_nsec == 500000000,
            "utimensat keeps the times");
        const struct timespec omit[2] = {{0, UTIME_OMIT}, {1700000000, 0}};
        check(utimensat(AT_FDCWD, path, omit, 0) == 0 &&
                stat(path, &st) == 0 &&
                st.st_atim.tv_sec == 1600000000 &&
                st.st_mtim.tv_sec == 1700000000,
            "UTIME_OMIT leaves a time");
        modes_fd = open(path, O_RDWR);
        check(
            modes_fd >= 0 && futimens(modes_fd, 0) == 0 && fstat(modes_fd, &st) == 0 && st.st_mtim.tv_sec > 1700000000,
            "futimens with now");
        if (modes_fd >= 0) {
            close(modes_fd);
        }
    } else {
        check(chmod(path, 0444) == 0 && stat(path, &st) == 0 && (st.st_mode & 07777) == 0444,
            "chmod to read-only on FAT");
        check(chmod(path, 0644) == 0 && stat(path, &st) == 0 && (st.st_mode & 07777) == 0644, "chmod back on FAT");
        check(chmod(path, 0600) < 0 && errno == EPERM, "a mode FAT cannot keep is EPERM, as on Linux");
        const struct timespec stamps[2] = {{1600000000, 0}, {1600000001, 0}};
        check(utimensat(AT_FDCWD, path, stamps, 0) < 0 && errno == EPERM, "times on FAT are EPERM");
    }
    int fd = open(path, O_RDWR);
    check(fd >= 0 && pread(fd, buffer, 5, 7) == 5 && memcmp(buffer, "world", 5) == 0, "pread");
    check(lseek(fd, 0, SEEK_END) == 12 && pwrite(fd, "!", 1, 12) == 1, "lseek and pwrite at the end");
    check(ftruncate(fd, 5) == 0 && fstat(fd, &st) == 0 && st.st_size == 5, "ftruncate and fstat");
    check(fsync(fd) == 0, "fsync");
    close(fd);
    snprintf(other, sizeof(other), "%s/posix/renamed.txt", root);
    check(rename(path, other) == 0 && access(path, F_OK) < 0 && errno == ENOENT, "rename");
    check(read_file(other, buffer, sizeof(buffer)) == 5 && strcmp(buffer, "hello") == 0, "the renamed file");
    /* mmap (D5.2, R4.2): a private mapping is a copy of the file's bytes, zero past its end, whose writes stay its own;
     * a shared mapping shows the namespace service's pages of the file, so that a write through it reaches a read of
     * the file and a write of the file reaches it, and msync or the last close writes it back to the file. */
    fd = open(other, O_RDWR);
    char *view = fd >= 0 ? mmap(0, 8192, PROT_READ | PROT_WRITE, MAP_PRIVATE, fd, 0) : MAP_FAILED;
    check(view != MAP_FAILED && memcmp(view, "hello", 5) == 0 && view[5] == 0 && view[8191] == 0,
        "mmap with MAP_PRIVATE copies the file");
    if (view != MAP_FAILED) {
        view[0] = 'J';
        check(msync(view, 8192, MS_SYNC) == 0 && munmap(view, 8192) == 0, "msync and munmap of the copy");
    }
    view = mmap(0, 4096, PROT_READ, MAP_SHARED, fd, 0);
    check(view != MAP_FAILED && memcmp(view, "hello", 5) == 0 && munmap(view, 4096) == 0, "a read-only shared mapping");
    close(fd); /* FAT opens no file twice while one is open for writing (EBUSY) */
    check(read_file(other, buffer, sizeof(buffer)) == 5 && strcmp(buffer, "hello") == 0,
        "a write to a private mapping leaves the file");
    fd = open(other, O_RDWR);
    view = fd >= 0 ? mmap(0, 4096, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0) : MAP_FAILED;
    if (view != MAP_FAILED) {
        view[1] = 'E';
    }
    check(view != MAP_FAILED && pread(fd, buffer, 5, 0) == 5 && memcmp(buffer, "hEllo", 5) == 0,
        "a write through a shared mapping reaches a read");
    check(view != MAP_FAILED && pwrite(fd, "L", 1, 2) == 1 && view[2] == 'L',
        "a write of the file reaches a shared mapping");
    check(view != MAP_FAILED && msync(view, 4096, MS_SYNC) == 0 && munmap(view, 4096) == 0,
        "msync and munmap of a shared mapping");
    close(fd);
    check(read_file(other, buffer, sizeof(buffer)) == 5 && strcmp(buffer, "hELlo") == 0,
        "a shared mapping's write reached the file");
    snprintf(path, sizeof(path), "%s/posix/sub", root);
    check(mkdir(path, 0755) == 0, "mkdir of a subdirectory");
    snprintf(path, sizeof(path), "%s/posix", root);
    DIR *directory = opendir(path);
    int names = 0, renamed = 0, sub = 0;
    for (struct dirent *entry; directory && (entry = readdir(directory));) {
        names += strcmp(entry->d_name, ".") && strcmp(entry->d_name, "..");
        renamed += strcmp(entry->d_name, "renamed.txt") == 0 && entry->d_type == DT_REG;
        sub += strcmp(entry->d_name, "sub") == 0 && entry->d_type == DT_DIR;
    }
    if (directory) {
        closedir(directory);
    }
    check(names == 2 && renamed == 1 && sub == 1, "readdir lists the entries");
    check(rmdir(path) < 0 && errno == ENOTEMPTY, "rmdir of a full directory is ENOTEMPTY");
    check(unlink(path) < 0 && errno == EISDIR, "unlink of a directory is EISDIR");
    fd = chdir(path) == 0 ? open("renamed.txt", O_RDONLY) : -1;
    check(fd >= 0, "chdir and a relative path");
    if (fd >= 0) {
        close(fd); /* FAT removes no open file (EBUSY), unlike POSIX */
    }
    check(getcwd(buffer, sizeof(buffer)) && strcmp(buffer, path) == 0, "getcwd");
    check(chdir("/") == 0, "chdir back to the package");
    check(
        unlink(other) == 0 && rmdir(strcat(strcpy(buffer, path), "/sub")) == 0 && rmdir(path) == 0, "unlink and rmdir");
    check(open(other, O_RDONLY) < 0 && errno == ENOENT, "a removed file is ENOENT");
}

/* Starts a program with its arguments and waits for it: its exit code, or -1. */
static int run(char *argv[])
{
    pid_t child = 0;
    int status = 0;
    const int error = posix_spawn(&child, argv[0], 0, 0, argv, environ);
    if (error || waitpid(child, &status, 0) != child) {
        printf("[DEVICES-INIT] %s did not start (errno %d)\n", argv[0], error ? error : errno);
        return -1;
    }
    const int code = WIFEXITED(status) ? WEXITSTATUS(status) : -1;
    printf("[DEVICES-INIT] %s exited with %d\n", argv[0], code);
    return code;
}

/* The byte of a round's file at an offset: no round's file and no block repeats another. */
static unsigned char round_byte(int round, int offset)
{
    return (unsigned char)((offset * 7 + round * 13) ^ (offset >> 9));
}

/* Whether the file holds its round's bytes from its start, and its length. */
static int round_file(const char *path, int round, off_t *length)
{
    static unsigned char bytes[ROUND_BYTES];
    struct stat st;
    if (stat(path, &st) != 0 || st.st_size > ROUND_BYTES) {
        return 0;
    }
    const int fd = open(path, O_RDONLY);
    const ssize_t read_bytes = fd >= 0 ? read(fd, bytes, (size_t)st.st_size) : -1;
    if (fd >= 0) {
        close(fd);
    }
    if (read_bytes != st.st_size) {
        return 0;
    }
    for (off_t i = 0; i < st.st_size; ++i) {
        if (bytes[i] != round_byte(round, (int)i)) {
            return 0;
        }
    }
    *length = st.st_size;
    return 1;
}

/* The writes a power cut interrupts (D6.2, storage document section 50): each round writes /data/crash/<n>.tmp and
 * makes it durable, cuts the previous round's file to half and renames <n>.tmp to <n>.bin, each a transaction of the
 * ext4 provider's journal, then reports the round; the host cuts the machine off after a report, so that the cut falls
 * in a later round. It never returns. */
static void cut_writer(void)
{
    static unsigned char bytes[ROUND_BYTES];
    char path[64], target[64];
    mkdir("/data/crash", 0755);
    for (int round = 0;; ++round) {
        for (int i = 0; i < ROUND_BYTES; ++i) {
            bytes[i] = round_byte(round, i);
        }
        snprintf(path, sizeof(path), "/data/crash/%04d.tmp", round);
        snprintf(target, sizeof(target), "/data/crash/%04d.bin", round);
        int fd = open(path, O_WRONLY | O_CREAT | O_TRUNC, 0644);
        const int written = fd >= 0 && write(fd, bytes, ROUND_BYTES) == ROUND_BYTES && fsync(fd) == 0;
        if (fd >= 0) {
            close(fd);
        }
        int cut = 1;
        if (round > 0) {
            snprintf(path, sizeof(path), "/data/crash/%04d.bin", round - 1);
            fd = open(path, O_WRONLY);
            cut = fd >= 0 && ftruncate(fd, ROUND_BYTES / 2) == 0;
            if (fd >= 0) {
                close(fd);
            }
        }
        snprintf(path, sizeof(path), "/data/crash/%04d.tmp", round);
        if (!written || rename(path, target) != 0 || !cut) {
            printf("[DEVICES-INIT] cut writer: round %d failed (errno %d)\n", round, errno);
            for (;;) {
                pause();
            }
        }
        printf("[DEVICES-INIT] cut writer: round %d durable\n", round);
    }
}

/* After the cut (D6.2): every round's file from the first to the last, each with its round's bytes, as its last durable
 * change left it: every one but the last half; the last whole, or half when the next round's file is there whole (the
 * cut fell between that round's truncation and its rename); the round in flight's file, when there is one, with its
 * round's bytes for its length, and nothing past it. Prints what it found; whether it holds. */
static int check_cut(void)
{
    char path[64];
    off_t length = 0, flight_length = 0;
    struct stat st;
    int last = -1, whole = 1;
    while (snprintf(path, sizeof(path), "/data/crash/%04d.bin", last + 1), stat(path, &st) == 0) {
        ++last;
    }
    snprintf(path, sizeof(path), "/data/crash/%04d.tmp", last + 1);
    const int flight = stat(path, &st) == 0;
    if (flight) {
        whole = round_file(path, last + 1, &flight_length);
    }
    for (int round = 0; round <= last && whole; ++round) {
        snprintf(path, sizeof(path), "/data/crash/%04d.bin", round);
        whole = round_file(path, round, &length) &&
            (round < last ? length == ROUND_BYTES / 2
                          : length == ROUND_BYTES || (length == ROUND_BYTES / 2 && flight_length == ROUND_BYTES));
    }
    snprintf(path, sizeof(path), "/data/crash/%04d.bin", last + 2);
    whole = whole && last >= 0 && stat(path, &st) != 0;
    printf("[DEVICES-INIT] after the cut: /data/crash holds rounds 0 to %d, %s%s\n", last,
        whole ? "each as its last durable change left it" : "not as its durable changes left them",
        flight ? ", and the file of the round in flight with its round's bytes" : "");
    return whole;
}

/* A failing disk (D6.3): the host makes the device fail every read of /data/big.bin's first block. The read gets EIO,
 * the system goes on, and the rest of the volume reads. Prints what it found; whether it holds. */
static int check_failing_read(void)
{
    char head[16], hello[128];
    const int fd = open("/data/big.bin", O_RDONLY);
    errno = 0;
    const ssize_t got = fd >= 0 ? read(fd, head, sizeof(head)) : -2;
    const int error = errno;
    if (fd >= 0) {
        close(fd);
    }
    const int rest = read_file("/data/hello.txt", hello, sizeof(hello)) > 0;
    printf("[DEVICES-INIT] a read of /data/big.bin on the failing sector: %s; /data/hello.txt %s\n",
        got == -1 && error == EIO ? "EIO (Input/output error)" : "no EIO", rest ? "still reads" : "does not read");
    return got == -1 && error == EIO && rest;
}

int main(void)
{
    char buffer[16];
    printf("[DEVICES-INIT] /bin/init started after the device manager answered\n");
    /* The data disk across reboots (D6.1): the boot this is, which /data/boots.txt counts, and on a later boot the files
     * the previous one left, which it wrote before the services stopped and unmounted the volume. */
    char text[160];
    const ssize_t counted = read_file("/data/boots.txt", text, sizeof(text));
    const int boot = (counted > 0 ? atoi(text) : 0) + 1;
    snprintf(text, sizeof(text), "%d", boot);
    check(write_file("/data/boots.txt", text, O_TRUNC) == 0, "the boot count");
    printf("[DEVICES-INIT] boot %d of the data disk\n", boot);
    if (boot > 1) {
        const int posix = read_file("/data/posix-kept.txt", text, sizeof(text)) > 0 && strcmp(text, POSIX_KEPT) == 0;
        const int dotnet = read_file("/data/dotnet-kept.txt", text, sizeof(text)) > 0 && strcmp(text, DOTNET_KEPT) == 0;
        check(posix && dotnet, "the files a previous boot left");
        printf("[DEVICES-INIT] /data/posix-kept.txt and /data/dotnet-kept.txt hold what the previous boot wrote: %s\n",
            posix && dotnet ? "yes" : "no");
    }
    if (boot == CUT_BOOT) {
        cut_writer();
    }
    if (boot > CUT_BOOT) {
        check(check_cut(), "the files the cut left");
    }
    if (boot == FAIL_BOOT) {
        check(check_failing_read(), "a read of a failing disk");
    }
    volume("/data", 1);
    volume("/fat", 0);
    /* /tmp (N4.1): the namespace service's volume in memory, there whatever disks there are; the same calls as on a
     * disk's volume, a shared mapping among them, and the package's chmod refused with EROFS. */
    struct stat tmp_stat;
    check(stat("/tmp", &tmp_stat) == 0 && S_ISDIR(tmp_stat.st_mode), "/tmp is a directory");
    volume("/tmp", 1);
    check(chmod("/bin/init", 0755) < 0 && errno == EROFS, "chmod in the package is EROFS");
    /* /etc/mtab (C1.2): the mount table the libc serves from the namespace's mount points, which System.Native reads
     * for DriveInfo.GetDrives(): the package at / and the four volumes, read-only; and the home the service made. */
    FILE *mounts = setmntent("/etc/mtab", "r");
    int seen = 0, entries = 0;
    for (struct mntent *entry; mounts && (entry = getmntent(mounts)) != 0; ++entries) {
        seen |= strcmp(entry->mnt_dir, "/") == 0   ? 1
            : strcmp(entry->mnt_dir, "/data") == 0 ? 2
            : strcmp(entry->mnt_dir, "/fat") == 0  ? 4
            : strcmp(entry->mnt_dir, "/boot") == 0 ? 8
            : strcmp(entry->mnt_dir, "/tmp") == 0  ? 16
                                                   : 0;
    }
    if (mounts) {
        endmntent(mounts);
    }
    check(mounts != 0 && entries == 5 && seen == 31, "/etc/mtab lists the package and the four volumes");
    struct stat mtab_stat;
    check(stat("/etc/mtab", &mtab_stat) == 0 && S_ISREG(mtab_stat.st_mode) && mtab_stat.st_size > 0,
        "/etc/mtab is a regular file");
    check(open("/etc/mtab", O_WRONLY) < 0 && errno == EROFS, "/etc/mtab is read-only");
    check(stat("/data/home", &mtab_stat) == 0 && S_ISDIR(mtab_stat.st_mode),
        "/data/home, the home the namespace service makes");
    check(rename("/data/x", "/fat/x") < 0 && (errno == EXDEV || errno == ENOENT), "rename across volumes");
    check(write_file("/data/posix-kept.txt", POSIX_KEPT, O_TRUNC) == 0, "the file the host finds");
#if defined(__x86_64__)
    const char *loader = "/boot/EFI/BOOT/BOOTX64.EFI";
#elif defined(__aarch64__)
    const char *loader = "/boot/EFI/BOOT/BOOTAA64.EFI";
#else
    const char *loader = "/boot/EFI/BOOT/BOOTRISCV64.EFI"; /* a VFAT long name over the alias BOOTRI~1.EFI (V1.1) */
#endif
    check(read_file(loader, buffer, 3) == 2 && buffer[0] == 'M' && buffer[1] == 'Z', "the boot volume's loader");
    check(write_file("/boot/new.txt", "x", 0) < 0 && errno == EROFS, "a write to the read-only boot volume is EROFS");
    printf("[DEVICES-INIT] POSIX files over the namespace service: %d checks passed, %d failed\n", passed, failed);
    /* The libc's sockets over the network service (W2.1a), in a process of its own, as any program has them. */
    char *sockets_argv[] = {"/bin/sockets-check", 0};
    char *files_argv[] = {"/data/programs/files/Files", 0};
    char *dynamic_argv[] = {"/data/programs/dynamic/dynamic", "0", 0};
    char *app_argv[] = {"/dotnet/dotnet", "/data/programs/app/App.dll", "witos", 0};
    const int sockets = run(sockets_argv);
    const int files = run(files_argv);
    const int dynamic = run(dynamic_argv);
    const int app = run(app_argv);
    return failed || sockets || files || dynamic || app ? 1 : 0;
}
