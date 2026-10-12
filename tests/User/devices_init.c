#define _GNU_SOURCE
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <stdio.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

/* /bin/init of the runtime-devices scenario (plan steps D1.1 and D5.1, ADR 0025): the root task starts it only after
 * the device manager, /system/DeviceManager, answered that its devices are discovered, so its first line follows every
 * discovery line in the log. Then it uses the files of the volumes the namespace service mounts through the libc's
 * POSIX calls, as any program does: on /data (the data disk's ext4 partition) and /fat (its FAT partition) it makes,
 * writes, reads, appends to, cuts, lists, renames and removes files and directories and checks each errno value POSIX
 * gives; on /boot (the read-only boot disk) it reads the loader and is refused a write. It leaves /data/posix-kept.txt
 * for the host to find after the boot. */

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

/* The same calls on one writable volume. */
static void volume(const char *root)
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
    int fd = open(path, O_RDWR);
    check(fd >= 0 && pread(fd, buffer, 5, 7) == 5 && memcmp(buffer, "world", 5) == 0, "pread");
    check(lseek(fd, 0, SEEK_END) == 12 && pwrite(fd, "!", 1, 12) == 1, "lseek and pwrite at the end");
    check(ftruncate(fd, 5) == 0 && fstat(fd, &st) == 0 && st.st_size == 5, "ftruncate and fstat");
    check(fsync(fd) == 0, "fsync");
    close(fd);
    snprintf(other, sizeof(other), "%s/posix/renamed.txt", root);
    check(rename(path, other) == 0 && access(path, F_OK) < 0 && errno == ENOENT, "rename");
    check(read_file(other, buffer, sizeof(buffer)) == 5 && strcmp(buffer, "hello") == 0, "the renamed file");
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

int main(void)
{
    char buffer[16];
    printf("[DEVICES-INIT] /bin/init started after the device manager answered\n");
    volume("/data");
    volume("/fat");
    check(rename("/data/x", "/fat/x") < 0 && (errno == EXDEV || errno == ENOENT), "rename across volumes");
    check(write_file(
              "/data/posix-kept.txt", "Written by /bin/init through the libc and the namespace service.", O_TRUNC) == 0,
        "the file the host finds");
    const char *loader =
        access("/boot/EFI/BOOT/BOOTX64.EFI", F_OK) == 0 ? "/boot/EFI/BOOT/BOOTX64.EFI" : "/boot/EFI/BOOT/BOOTAA64.EFI";
    check(read_file(loader, buffer, 3) == 2 && buffer[0] == 'M' && buffer[1] == 'Z', "the boot volume's loader");
    check(write_file("/boot/new.txt", "x", 0) < 0 && errno == EROFS, "a write to the read-only boot volume is EROFS");
    printf("[DEVICES-INIT] POSIX files over the namespace service: %d checks passed, %d failed\n", passed, failed);
    return failed ? 1 : 0;
}
