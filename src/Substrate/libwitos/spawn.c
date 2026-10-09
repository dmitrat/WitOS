#define _GNU_SOURCE
#include "witos/spawn.h"
#include "witos/channels.h"
#include "witos/libc_context.h"
#include "witos/memory_object.h"
#include "witos/start.h"
#include "witos/syscall.h"
#include "witos/thread_reference.h"
#include "witos/user_abi.h"
#include "witos/user_layout.h"
#include "witos/wait_objects.h"
#include <elf.h>
#include <errno.h>
#include <fcntl.h>
#include <string.h>
#include <unistd.h>

/* The ELF loader of the root task (plan steps S5.2 and S5.3). A program is a position-independent executable in the
 * boot package: a static one (S5.2) or a dynamic one whose PT_INTERP names its dynamic linker in the package, musl's
 * libc.so (S5.3). The headers of the program and of its interpreter are read through the libc's descriptors, every
 * rule below is checked before anything is created, and only then does the process exist (PROCESS_CREATE with one
 * end of a new channel). The program lies at the start of the new process's code arena and its interpreter 64 MiB
 * above it, where the kernel places no reservation of its own: an executable segment is a mapping of the package's
 * pages themselves (at most 64 pages per mapping, published to instruction fetch through a view of the loader's own
 * first), any other is anonymous memory objects the loader fills with the file's bytes and zeroes, mapped into the
 * process READ|WRITE or, for a read-only segment, READ, and left to it. A read-only segment is a copy as the libc's
 * private mapping of a file that does not execute is (R2.1): the process may make its pages writable, as NativeAOT's
 * runtime does with the page of its GS cookie at startup, which a mapping of the package never allows. The first stack is one object of 64 pages at the
 * top of the code arena, with nothing mapped below it, on which the loader builds what a Linux kernel builds: argc,
 * argv, envp and the auxiliary vector with AT_PHDR, AT_ENTRY (the program's), AT_BASE (the interpreter's base, for a
 * dynamic program alone: musl's dlstart.c of a static one finds its base through PT_DYNAMIC), AT_RANDOM and
 * WIT_AT_START. The thread starts at the interpreter's entry, or the program's without one. The start message goes
 * out before the thread starts (witos/start.h): duplicates of the loader's log, WRITE alone, of the package, MAP,
 * EXECUTE and QUERY, and of the process manager's endpoint, SEND alone, when the start names one (S6.1), each with
 * TRANSFER, as a capability moves. The objects are charged to the loader while it lives;
 * the process's own memory to the process. A failed start closes what it made: the threadless process ends with its
 * last handle and takes its mappings along. */

#define PAGE 4096ULL
#define CHUNK (WIT_MEMORY_OBJECT_PAGES * PAGE) /* one object and one mapping cover at most 64 pages */
#define MAX_SEGMENTS 16U
#define IMAGE_SPAN (64ULL << 20) /* an image's segments, from address zero of its file */
#define PROGRAM_BASE WIT_USER_CODE_BASE
#define INTERPRETER_BASE (PROGRAM_BASE + IMAGE_SPAN)
#define INTERPRETER_PATH 256U /* PT_INTERP with its terminating zero */
#define STACK_BYTES CHUNK
#define STACK_TOP WIT_USER_CODE_LIMIT
#define STACK_BASE (STACK_TOP - STACK_BYTES)
#define ARGUMENT_BYTES (STACK_BYTES / 4) /* the strings and pointers of argv and envp */
#define AUXILIARY_PAIRS 15U
#define LOG_RIGHTS (WIT_RIGHT_WRITE | WIT_RIGHT_TRANSFER)
#define MANAGER_RIGHTS (WIT_RIGHT_SEND | WIT_RIGHT_TRANSFER)
#define PACKAGE_RIGHTS (WIT_RIGHT_MAP | WIT_RIGHT_EXECUTE | WIT_RIGHT_QUERY | WIT_RIGHT_TRANSFER)

#if defined(__x86_64__)
#define MACHINE EM_X86_64
#elif defined(__aarch64__)
#define MACHINE EM_AARCH64
#else
#error "libwitos: unsupported architecture"
#endif

typedef struct Program {
    int File;
    WitU64 Source, Length; /* the file's bytes in the package */
    WitU64 Base; /* where the image lies in the process */
    Elf64_Ehdr Header;
    Elf64_Phdr Segments[MAX_SEGMENTS];
    WitU64 Headers; /* the address of the program headers relative to the base */
    char Interpreter[INTERPRETER_PATH]; /* PT_INTERP, empty for a static program */
} Program;

static WitU64 round_up(WitU64 value)
{
    return (value + PAGE - 1) & ~(PAGE - 1);
}

static int failure(WitU64 status)
{
    return (int)-__wit_errno(status);
}

static void close_handle(WitU64 handle)
{
    WitU64 result = 0;
    if (handle) {
        wit_syscall(WIT_CALL_HANDLE_CLOSE, handle, 0, 0, &result);
    }
}

/* MEMORY_OBJECT_MAP of a window of an object into the target at an address (zero: the kernel's choice). */
static WitU64 map(
    WitU64 object, WitU64 offset, WitU64 bytes, WitU64 address, WitU32 protection, WitU64 target, WitU64 *mapped)
{
    WitMemoryMapRequest request;
    request.Version = WIT_MEMORY_MAP_VERSION;
    request.Size = sizeof(request);
    request.Object = object;
    request.Offset = offset;
    request.Bytes = bytes;
    request.Address = address;
    request.Protection = protection;
    request.Flags = 0;
    request.Target = target;
    return wit_syscall(WIT_CALL_MEMORY_OBJECT_MAP, (WitU64)&request, sizeof(request), 0, mapped);
}

static int read_exact(int file, void *buffer, WitU64 bytes, WitU64 offset)
{
    return pread(file, buffer, bytes, (off_t)offset) == (ssize_t)bytes;
}

/* The interpreter's path: inside the file, terminated by its last byte and by no earlier one, absolute. */
static int read_interpreter(Program *p, const Elf64_Phdr *s)
{
    if (s->p_filesz < 2 ||
        s->p_filesz > INTERPRETER_PATH ||
        s->p_offset > p->Length ||
        s->p_filesz > p->Length - s->p_offset ||
        !read_exact(p->File, p->Interpreter, s->p_filesz, s->p_offset) ||
        p->Interpreter[s->p_filesz - 1] != 0 ||
        strlen(p->Interpreter) != s->p_filesz - 1 ||
        p->Interpreter[0] != '/') {
        return ENOEXEC;
    }
    return 0;
}

/* The header and the program headers of a position-independent executable of this ISA: loadable segments readable,
 * never writable and executable at once, ascending on distinct pages within the image span, congruent to their file
 * offsets modulo the page, inside the file, zero-filled only where writable; a dynamic section; an interpreter only
 * where one is allowed, and then PT_PHDR, which musl's dynamic linker finds the program's base by; the entry in an
 * executable segment; the program headers inside a loaded segment (AT_PHDR). */
static int read_program(Program *p, int interpreter_allowed)
{
    const Elf64_Ehdr *h = &p->Header;
    WitU64 end = 0, headers_bytes;
    int dynamic = 0, entry = 0, headers = 0, phdr = 0;
    if (!read_exact(p->File, &p->Header, sizeof(p->Header), 0) ||
        memcmp(h->e_ident, ELFMAG, SELFMAG) != 0 ||
        h->e_ident[EI_CLASS] != ELFCLASS64 ||
        h->e_ident[EI_DATA] != ELFDATA2LSB ||
        h->e_ident[EI_VERSION] != EV_CURRENT ||
        h->e_type != ET_DYN ||
        h->e_machine != MACHINE ||
        h->e_version != EV_CURRENT ||
        h->e_phentsize != sizeof(Elf64_Phdr) ||
        h->e_phnum == 0 ||
        h->e_phnum > MAX_SEGMENTS) {
        return ENOEXEC;
    }
    headers_bytes = (WitU64)h->e_phnum * sizeof(Elf64_Phdr);
    if (h->e_phoff > p->Length ||
        headers_bytes > p->Length - h->e_phoff ||
        !read_exact(p->File, p->Segments, headers_bytes, h->e_phoff)) {
        return ENOEXEC;
    }
    for (unsigned i = 0; i < h->e_phnum; ++i) {
        const Elf64_Phdr *s = &p->Segments[i];
        if (s->p_type == PT_INTERP) {
            if (!interpreter_allowed || p->Interpreter[0] || read_interpreter(p, s) != 0) {
                return ENOEXEC;
            }
        }
        if (s->p_type == PT_DYNAMIC) {
            dynamic = 1;
        }
        if (s->p_type == PT_PHDR) {
            phdr = 1;
        }
        if (s->p_type == PT_GNU_STACK && (s->p_flags & PF_X)) {
            return ENOEXEC;
        }
        if (s->p_type != PT_LOAD) {
            continue;
        }
        if (!(s->p_flags & PF_R) ||
            ((s->p_flags & PF_W) && (s->p_flags & PF_X)) ||
            s->p_filesz > s->p_memsz ||
            s->p_memsz == 0 ||
            (s->p_vaddr % PAGE) != (s->p_offset % PAGE) ||
            s->p_offset > p->Length ||
            s->p_filesz > p->Length - s->p_offset ||
            s->p_vaddr >= IMAGE_SPAN ||
            s->p_memsz > IMAGE_SPAN - s->p_vaddr ||
            (s->p_vaddr & ~(PAGE - 1)) < end ||
            (!(s->p_flags & PF_W) && s->p_memsz != s->p_filesz)) {
            return ENOEXEC;
        }
        end = round_up(s->p_vaddr + s->p_memsz);
        if ((s->p_flags & PF_X) && h->e_entry >= s->p_vaddr && h->e_entry < s->p_vaddr + s->p_memsz) {
            entry = 1;
        }
        if (h->e_phoff >= s->p_offset && h->e_phoff + headers_bytes <= s->p_offset + s->p_filesz) {
            p->Headers = s->p_vaddr + (h->e_phoff - s->p_offset);
            headers = 1;
        }
    }
    return dynamic && entry && headers && (phdr || !p->Interpreter[0]) ? 0 : ENOEXEC;
}

/* Opens an image of the package and checks it; the descriptor stays open for the mapping. */
static int open_program(Program *p, const char *path, WitU64 base, int interpreter_allowed)
{
    p->Base = base;
    p->File = open(path, O_RDONLY | O_CLOEXEC);
    if (p->File < 0) {
        return errno;
    }
    return __wit_file_map_source(p->File, &p->Source, &p->Length) == 0 ? read_program(p, interpreter_allowed) : ENOEXEC;
}

/* An executable segment: the package's own pages, mapped READ|EXECUTE in windows of 64 pages. */
static int map_package(const Program *p, const Elf64_Phdr *s, WitU64 process)
{
    const WitU64 first = s->p_offset & ~(PAGE - 1);
    const WitU64 bytes = round_up(s->p_offset + s->p_filesz) - first;
    const WitU64 address = p->Base + (s->p_vaddr & ~(PAGE - 1));
    const WitU32 protection = WIT_MEMORY_READ | WIT_MEMORY_EXECUTE;
    for (WitU64 done = 0; done < bytes; done += CHUNK) {
        const WitU64 part = bytes - done < CHUNK ? bytes - done : CHUNK;
        WitU64 status, mapped = 0, result = 0;
        /* The cache maintenance acts on the pages, so publishing them through the loader's own view publishes them
         * for every mapping of the package (S5.1). */
        status = map(__wit_process.Package, p->Source + first + done, part, 0, protection, WIT_PROCESS_SELF, &mapped);
        if (status != WIT_STATUS_OK) {
            return failure(status);
        }
        status = wit_syscall(WIT_CALL_CODE_PUBLISH, mapped, part, 0, &result);
        wit_syscall(WIT_CALL_MEMORY_RELEASE, mapped, 0, 0, &result);
        if (status != WIT_STATUS_OK) {
            return failure(status);
        }
        status =
            map(__wit_process.Package, p->Source + first + done, part, address + done, protection, process, &mapped);
        if (status != WIT_STATUS_OK) {
            return failure(status);
        }
    }
    return 0;
}

/* A segment that does not execute: anonymous objects of at most 64 pages, filled through a view of the loader's own
 * with the file's bytes that fall in each and zero elsewhere, then mapped into the process READ|WRITE, or READ for a
 * read-only segment; the process's mapping holds every right of the object, so that it may make the pages writable. */
static int map_copy(const Program *p, const Elf64_Phdr *s, WitU64 process)
{
    const WitU32 protection = (s->p_flags & PF_W) ? WIT_MEMORY_READ | WIT_MEMORY_WRITE : WIT_MEMORY_READ;
    const WitU64 low = s->p_vaddr & ~(PAGE - 1);
    const WitU64 bytes = round_up(s->p_vaddr + s->p_memsz) - low;
    for (WitU64 done = 0; done < bytes; done += CHUNK) {
        const WitU64 part = bytes - done < CHUNK ? bytes - done : CHUNK;
        const WitU64 from = s->p_vaddr > low + done ? s->p_vaddr : low + done;
        const WitU64 file_end = s->p_vaddr + s->p_filesz;
        const WitU64 to = file_end < low + done + part ? file_end : low + done + part;
        WitU64 object = 0, view = 0, mapped = 0, result = 0;
        WitU64 status = wit_syscall(WIT_CALL_MEMORY_OBJECT_CREATE, part, 0, 0, &object);
        if (status != WIT_STATUS_OK) {
            return failure(status);
        }
        status = map(object, 0, part, 0, WIT_MEMORY_READ | WIT_MEMORY_WRITE, WIT_PROCESS_SELF, &view);
        if (status == WIT_STATUS_OK) {
            if (from < to &&
                !read_exact(
                    p->File, (void *)(view + (from - low - done)), to - from, s->p_offset + (from - s->p_vaddr))) {
                status = WIT_STATUS_INVALID_ARGUMENT;
            }
            wit_syscall(WIT_CALL_MEMORY_RELEASE, view, 0, 0, &result);
        }
        if (status == WIT_STATUS_OK) {
            status = map(object, 0, part, p->Base + low + done, protection, process, &mapped);
        }
        close_handle(object); /* the process's mapping keeps the object */
        if (status != WIT_STATUS_OK) {
            return status == WIT_STATUS_INVALID_ARGUMENT ? ENOEXEC : failure(status);
        }
    }
    return 0;
}

static int map_image(const Program *p, WitU64 process)
{
    int error = 0;
    for (unsigned i = 0; i < p->Header.e_phnum && !error; ++i) {
        const Elf64_Phdr *s = &p->Segments[i];
        if (s->p_type == PT_LOAD) {
            error = (s->p_flags & PF_X) ? map_package(p, s, process) : map_copy(p, s, process);
        }
    }
    return error;
}

static int count_strings(char *const list[], WitU64 *count, WitU64 *bytes)
{
    *count = 0;
    for (; list && list[*count]; ++*count) {
        *bytes += strlen(list[*count]) + 1 + sizeof(WitU64);
        if (*bytes > ARGUMENT_BYTES) {
            return E2BIG;
        }
    }
    return 0;
}

/* Copies the strings of a list below the cursor of the view and writes their addresses in the process. */
static WitU64 *put_strings(char *const list[], WitU64 count, WitU64 *word, unsigned char *view, WitU64 *cursor)
{
    for (WitU64 i = 0; i < count; ++i) {
        const WitU64 length = strlen(list[i]) + 1;
        *cursor -= length;
        memcpy(view + (*cursor - STACK_BASE), list[i], length);
        *word++ = *cursor;
    }
    *word++ = 0;
    return word;
}

/* The auxiliary vector: the program's headers and entry, the interpreter's base when there is one, the 16 random
 * bytes, the ids, the start endpoint. */
static WitU64 *put_auxiliary(WitU64 *word, const Program *p, const Program *interpreter, WitU64 random, WitU64 endpoint)
{
    const WitU64 pairs[] = {AT_PHDR, p->Base + p->Headers, AT_PHENT, sizeof(Elf64_Phdr), AT_PHNUM, p->Header.e_phnum,
        AT_PAGESZ, PAGE, AT_ENTRY, p->Base + p->Header.e_entry, AT_RANDOM, random, AT_UID, 0, AT_EUID, 0, AT_GID, 0,
        AT_EGID, 0, AT_SECURE, 0, AT_HWCAP, 0, WIT_AT_START, endpoint};
    memcpy(word, pairs, sizeof(pairs));
    word += sizeof(pairs) / sizeof(pairs[0]);
    if (interpreter) {
        *word++ = AT_BASE;
        *word++ = interpreter->Base;
    }
    *word++ = AT_NULL;
    *word++ = 0;
    return word;
}

/* The first stack: argc, argv, envp and the auxiliary vector at a 16-byte aligned stack pointer, the strings and the
 * 16 random bytes above them, as a Linux kernel lays them out. */
static int build_stack(const Program *p, const Program *interpreter, char *const argv[], char *const envp[],
    WitU64 endpoint, WitU64 process, WitU64 *stack_pointer)
{
    WitU64 argc = 0, envc = 0, string_bytes = 0, object = 0, view = 0, mapped = 0, result = 0;
    int error = count_strings(argv, &argc, &string_bytes);
    if (!error) {
        error = count_strings(envp, &envc, &string_bytes);
    }
    if (error) {
        return error;
    }
    WitU64 status = wit_syscall(WIT_CALL_MEMORY_OBJECT_CREATE, STACK_BYTES, 0, 0, &object);
    if (status != WIT_STATUS_OK) {
        return failure(status);
    }
    status = map(object, 0, STACK_BYTES, 0, WIT_MEMORY_READ | WIT_MEMORY_WRITE, WIT_PROCESS_SELF, &view);
    if (status == WIT_STATUS_OK) {
        unsigned char *bytes = (unsigned char *)view;
        const WitU64 random = STACK_TOP - 16;
        WitU64 cursor = random;
        const WitU64 words = 1 + argc + 1 + envc + 1 + 2 * AUXILIARY_PAIRS;
        status = wit_syscall(WIT_CALL_RANDOM, view + (random - STACK_BASE), 16, 0, &result);
        WitU64 strings = cursor;
        for (WitU64 i = 0; i < argc; ++i) {
            strings -= strlen(argv[i]) + 1;
        }
        for (WitU64 i = 0; i < envc; ++i) {
            strings -= strlen(envp[i]) + 1;
        }
        const WitU64 sp = ((strings & ~15ULL) - words * sizeof(WitU64)) & ~15ULL;
        WitU64 *word = (WitU64 *)(bytes + (sp - STACK_BASE));
        *word++ = argc;
        word = put_strings(argv, argc, word, bytes, &cursor);
        word = put_strings(envp, envc, word, bytes, &cursor);
        put_auxiliary(word, p, interpreter, random, endpoint);
        *stack_pointer = sp;
        wit_syscall(WIT_CALL_MEMORY_RELEASE, view, 0, 0, &result);
    }
    if (status == WIT_STATUS_OK) {
        status = map(object, 0, STACK_BYTES, STACK_BASE, WIT_MEMORY_READ | WIT_MEMORY_WRITE, process, &mapped);
    }
    close_handle(object);
    return status == WIT_STATUS_OK ? 0 : failure(status);
}

/* What the start message says beyond the capabilities (S6.2): the initial directory, an absolute path, and the
 * standard streams the process starts without. */
typedef struct StartState {
    char Directory[WIT_START_DIRECTORY_MAXIMUM + 2];
    WitU32 DirectoryBytes;
    WitU32 ClosedStreams;
} StartState;

/* The start message: the package's size, the closed streams and the initial directory, and duplicates of the log,
 * the package and the manager's endpoint when there is one (S6.1), moved by the send. */
static int send_start(WitU64 endpoint, WitU64 manager, const StartState *state)
{
    WitU64 handles[WIT_START_HANDLES_MAXIMUM] = {0, 0, 0}, result = 0;

    union {
        WitStartMessage Header;
        unsigned char Bytes[WIT_START_SIZE + WIT_START_DIRECTORY_MAXIMUM];
    } message;

    WitChannelMessage request;
    WitU64 status = wit_syscall(
        WIT_CALL_HANDLE_DUPLICATE, __wit_process.Log, (WitU64)&handles[WIT_START_HANDLE_LOG], LOG_RIGHTS, &result);
    if (status == WIT_STATUS_OK) {
        status = wit_syscall(WIT_CALL_HANDLE_DUPLICATE, __wit_process.Package,
            (WitU64)&handles[WIT_START_HANDLE_PACKAGE], PACKAGE_RIGHTS, &result);
    }
    if (status == WIT_STATUS_OK && manager) {
        status = wit_syscall(
            WIT_CALL_HANDLE_DUPLICATE, manager, (WitU64)&handles[WIT_START_HANDLE_MANAGER], MANAGER_RIGHTS, &result);
    }
    if (status == WIT_STATUS_OK) {
        message.Header.Version = WIT_START_VERSION;
        message.Header.Size = sizeof(message.Header);
        message.Header.PackageBytes = __wit_process.PackageBytes;
        message.Header.ClosedStreams = state->ClosedStreams;
        message.Header.DirectoryBytes = state->DirectoryBytes;
        memcpy(message.Bytes + sizeof(message.Header), state->Directory, state->DirectoryBytes);
        request.Version = WIT_CHANNEL_MESSAGE_VERSION;
        request.Size = sizeof(request);
        request.Data = (WitU64)&message;
        request.Handles = (WitU64)handles;
        request.Bytes = (WitU32)sizeof(message.Header) + state->DirectoryBytes;
        request.HandleCount = manager ? WIT_START_HANDLES_MAXIMUM : WIT_START_HANDLES;
        request.Flags = 0;
        request.Reserved = 0;
        status = wit_syscall(WIT_CALL_CHANNEL_SEND, endpoint, (WitU64)&request, sizeof(request), &result);
    }
    if (status != WIT_STATUS_OK) {
        close_handle(handles[WIT_START_HANDLE_LOG]);
        close_handle(handles[WIT_START_HANDLE_PACKAGE]);
        close_handle(handles[WIT_START_HANDLE_MANAGER]);
        return failure(status);
    }
    return 0;
}

static int start_thread(WitU64 entry, WitU64 process, WitU64 stack_pointer)
{
    WitThreadCreateRequest3 request;
    WitU64 thread = 0;
    request.Version = WIT_THREAD_CREATE_VERSION_3;
    request.Size = sizeof(request);
    request.Entry = entry;
    request.Argument = 0;
    request.StackPointer = stack_pointer;
    request.TlsBase = 0;
    request.Process = process;
    request.Flags = 0;
    request.Reserved = 0;
    const WitU64 status = wit_syscall(WIT_CALL_THREAD_CREATE, (WitU64)&request, sizeof(request), 0, &thread);
    if (status != WIT_STATUS_OK) {
        return failure(status);
    }
    close_handle(thread); /* closing detaches; the thread runs on */
    return 0;
}

/* Everything after PROCESS_CREATE: the images, the stack, the start message, the thread. */
static int load(const Program *p, const Program *interpreter, char *const argv[], char *const envp[], WitU64 process,
    WitU64 endpoint, WitU64 child, WitU64 manager, const StartState *state)
{
    WitU64 stack_pointer = 0;
    int error = map_image(p, process);
    if (!error && interpreter) {
        error = map_image(interpreter, process);
    }
    if (!error) {
        error = build_stack(p, interpreter, argv, envp, child, process, &stack_pointer);
    }
    if (!error) {
        error = send_start(endpoint, manager, state);
    }
    const Program *entry = interpreter ? interpreter : p;
    return error ? error : start_thread(entry->Base + entry->Header.e_entry, process, stack_pointer);
}

int witos_spawn(WitU64 *process, const char *path, char *const argv[], char *const envp[])
{
    return witos_spawn_ex(process, path, argv, envp, 0);
}

int witos_spawn_ex(
    WitU64 *process, const char *path, char *const argv[], char *const envp[], const witos_spawn_options *options)
{
    Program program, interpreter;
    StartState state;
    WitU64 ends[2] = {0, 0}, handle = 0, child = 0, result = 0;
    memset(&program, 0, sizeof(program));
    memset(&interpreter, 0, sizeof(interpreter));
    memset(&state, 0, sizeof(state));
    program.File = interpreter.File = -1;
    *process = 0;
    /* The initial directory, canonical and existing, and the closed streams, checked before anything opens (S6.2). */
    if (options && (options->closed_streams & ~7U)) {
        return EINVAL;
    }
    if (options && options->directory) {
        const long length = __wit_directory_resolve(0, options->directory, state.Directory, sizeof(state.Directory));
        if (length < 0) {
            return (int)-length;
        }
        state.DirectoryBytes = length > 1 ? (WitU32)length : 0; /* an absolute path; the root is no bytes */
    }
    state.ClosedStreams = options ? options->closed_streams : 0;
    int error = open_program(&program, path, PROGRAM_BASE, 1);
    if (!error && program.Interpreter[0]) {
        /* The dynamic linker: an image of the package itself, with no interpreter of its own. */
        error = open_program(&interpreter, program.Interpreter, INTERPRETER_BASE, 0);
        error = error == ENOENT ? ENOEXEC : error;
    }
    if (!error) {
        const WitU64 status = wit_syscall(WIT_CALL_CHANNEL_CREATE, (WitU64)ends, 0, 0, &result);
        error = status == WIT_STATUS_OK ? 0 : failure(status);
    }
    if (!error) {
        WitProcessCreateRequest request;
        request.Version = WIT_PROCESS_CREATE_VERSION;
        request.Size = sizeof(request);
        request.Endpoint = ends[1];
        request.Pages = 0;
        request.Flags = 0;
        request.Reserved = 0;
        const WitU64 status =
            wit_syscall(WIT_CALL_PROCESS_CREATE, (WitU64)&request, sizeof(request), (WitU64)&child, &handle);
        if (status == WIT_STATUS_OK) {
            ends[1] = 0; /* moved into the process */
            error = load(&program, program.Interpreter[0] ? &interpreter : 0, argv, envp, handle, ends[0], child,
                options ? options->manager : 0, &state);
        } else {
            error = failure(status);
        }
    }
    close_handle(ends[0]);
    close_handle(ends[1]);
    if (program.File >= 0) {
        close(program.File);
    }
    if (interpreter.File >= 0) {
        close(interpreter.File);
    }
    if (error) {
        close_handle(handle); /* a process without a thread ends with its last handle */
        return error;
    }
    *process = handle;
    return 0;
}

int witos_wait(WitU64 process, WitProcessInfo *info)
{
    WitUserWaitRequest wait;
    WitU64 status, result = 0;
    wait.Version = WIT_WAIT_OBJECTS_VERSION;
    wait.Size = sizeof(wait);
    wait.Handles = (WitU64)&process;
    wait.Count = 1;
    wait.Flags = 0;
    wait.Deadline = WIT_WAIT_INFINITE;
    do {
        status = wit_syscall(WIT_CALL_OBJECT_WAIT, (WitU64)&wait, sizeof(wait), 0, &result);
    } while (status == WIT_STATUS_INTERRUPTED); /* a signal's activation ran; the wait goes on */
    if (status != WIT_STATUS_OK) {
        return failure(status);
    }
    memset(info, 0, sizeof(*info));
    info->Version = WIT_PROCESS_INFO_VERSION;
    info->Size = sizeof(*info);
    status = wit_syscall(WIT_CALL_PROCESS_QUERY, process, (WitU64)info, sizeof(*info), &result);
    return status == WIT_STATUS_OK ? 0 : failure(status);
}
