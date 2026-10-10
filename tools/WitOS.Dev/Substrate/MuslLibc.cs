using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using WitOS.Dev.Host;
using WitOS.Dev.Images;
using WitOS.Dev.Kernel;
using WitOS.Dev.Upstream;

namespace WitOS.Dev.Substrate;

/// <summary>
/// The C library of the system layer (RFC 0011 §9.1, plan step S1): musl at the pinned release, built from the
/// verified tarball by the pinned clang for an architecture's triple, with WitOS's system-call dispatch
/// (src/Substrate/libc) in place of the Linux instruction. Upstream files change only through patches/musl; the
/// assembly that issues Linux system calls is left out and musl's generic C takes its place. The build replicates
/// musl's Makefile: every src/*/*.c and src/malloc/mallocng/*.c, an architecture's src/*/&lt;arch&gt;/* replacing the
/// generic file of the same name, the generated bits/alltypes.h, bits/syscall.h and version.h, musl's compiler
/// options, and a static libc.a; a static program links WitOS's crt1 ahead of it and compiler-rt's pinned builtins
/// (<see cref="CompilerRtBuiltins"/>) after it.
/// </summary>
internal static class MuslLibc
{
    #region Constants

    /// <summary>
    /// The pinned release.
    /// </summary>
    public const string VERSION = "1.2.5";

    /// <summary>
    /// SHA-256 of the release tarball at https://musl.libc.org/releases/.
    /// </summary>
    public const string TARBALL_SHA256 = "a9a118bbe84d8764da0ea0d28b3ab3fae8477fc7e4085d90102b8596fc7c75e4";

    /// <summary>
    /// WitOS's part of the library and its program startup.
    /// </summary>
    public const string SYSDEPS = "src/Substrate/libc";

    /// <summary>
    /// The lock file: the tarball's hash and the hashes of the files the patches start from.
    /// </summary>
    public const string LOCK = "src/Substrate/musl.lock.json";

    private const string TARBALL = "musl-" + VERSION + ".tar.gz";

    private const string URL = "https://musl.libc.org/releases/" + TARBALL;

    // Assembly that issues Linux system calls or builds on Linux's thread and signal entry; musl's generic C or
    // WitOS's own file takes its place. Threads (S2) and signals (S3) replace the stubs that result.
    private static readonly string[] OMITTED =
    [
        "src/thread/x86_64/__set_thread_area.s", "src/thread/x86_64/__unmapself.s", "src/thread/x86_64/clone.s",
        "src/thread/x86_64/syscall_cp.s", "src/signal/x86_64/restore.s", "src/process/x86_64/vfork.s",
        "src/thread/aarch64/__unmapself.s", "src/thread/aarch64/clone.s", "src/thread/aarch64/syscall_cp.s",
        "src/signal/aarch64/restore.s", "src/process/aarch64/vfork.s",
        // WitOS sets the x64 thread pointer through THREAD_SET_TLS, creates threads through THREAD_CREATE and ends them
        // through THREAD_EXIT (src/Substrate/libc/thread.c), in place of the generic stubs.
        "src/thread/__set_thread_area.c", "src/thread/clone.c", "src/thread/__unmapself.c",
        // posix_spawn asks the process manager (src/Substrate/libc/process.c, S6.1) in place of clone and exec.
        "src/process/posix_spawn.c"
    ];

    // The patched upstream files: the per-architecture system call stubs become calls into WitOS's dispatch.
    private static readonly Dictionary<string, string> PATCHED = new(StringComparer.Ordinal)
    {
        ["arch/x86_64/syscall_arch.h"] = "syscall_arch.x86_64.h",
        ["arch/aarch64/syscall_arch.h"] = "syscall_arch.aarch64.h",
        // The dynamic linker takes the start message before it opens a library (S5.3).
        ["ldso/dynlink.c"] = "dynlink.c",
        // musl's own fixes after 1.2.5 that libc-test's math suite checks (S7.2), backported unchanged.
        ["src/math/fma.c"] = "fma.c",
        ["src/math/fmaf.c"] = "fmaf.c",
        ["src/math/powl.c"] = "powl.c",
        ["src/math/x86_64/expl.s"] = "expl.x86_64.s",
        // The C linkage guards sys/membarrier.h lacks, which C++ code needs (R1.3).
        ["include/sys/membarrier.h"] = "membarrier.h"
    };

    // An image another component maps from the boot package (S5.3): every loadable segment starts a page of the file,
    // so that a file with code is at least a page long and lies at a page boundary of the package, and an executable
    // mapping shows the package's pages without a copy and without data beside the code.
    private static readonly string[] SEPARATE_SEGMENTS = ["-z", "max-page-size=4096", "-z", "separate-loadable-segments"];

    // The startup objects of static programs, which libc.so leaves out.
    private static readonly string[] STATIC_STARTUP = ["crt1.c", "rcrt1.c"];

    private static readonly Regex TYPEDEF = new(@"^TYPEDEF (.*) ([^ ]*);$");
    private static readonly Regex STRUCT = new(@"^STRUCT * ([^ ]*) (.*);$");
    private static readonly Regex UNION = new(@"^UNION * ([^ ]*) (.*);$");

    #endregion

    #region Functions

    /// <summary>
    /// Path of the pinned tarball under .tools/downloads.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>File path.</returns>
    public static string Tarball(string root) => Path.Combine(root, ".tools", "downloads", TARBALL);

    /// <summary>
    /// The extracted source tree (its top directory, holding arch, src and include).
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>Directory path.</returns>
    public static string SourceDirectory(string root) => Path.Combine(root, ".tools", "musl-" + VERSION, "musl-" + VERSION);

    /// <summary>
    /// Downloads the pinned tarball when missing, verifies its SHA-256 and extracts it once; a tree extracted from a
    /// tarball of another hash is replaced.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The source tree.</returns>
    public static async Task<string> PrepareAsync(string root)
    {
        var tarball = Tarball(root);
        Directory.CreateDirectory(Path.GetDirectoryName(tarball)!);
        if (!File.Exists(tarball))
        {
            Console.WriteLine($"Downloading pinned musl {VERSION} (about 1 MiB)...");
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            var bytes = await client.GetByteArrayAsync(URL);
            var partial = tarball + ".partial";
            await File.WriteAllBytesAsync(partial, bytes);
            File.Move(partial, tarball, overwrite: true);
        }
        string digest;
        await using (var stream = File.OpenRead(tarball))
        {
            digest = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
        }
        if (digest != TARBALL_SHA256)
            throw new InvalidDataException($"musl tarball hash mismatch. Remove the invalid download: {tarball}");
        var directory = Path.GetDirectoryName(SourceDirectory(root))!;
        var stamp = Path.Combine(directory, "verified-" + TARBALL_SHA256);
        if (!File.Exists(stamp) || !Directory.Exists(SourceDirectory(root)))
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
            Directory.CreateDirectory(directory);
            await using var tarballStream = File.OpenRead(tarball);
            await using var gzip = new GZipStream(tarballStream, CompressionMode.Decompress);
            await TarFile.ExtractToDirectoryAsync(gzip, directory, overwriteFiles: false);
            if (!File.Exists(Path.Combine(SourceDirectory(root), "VERSION")) ||
                (await File.ReadAllTextAsync(Path.Combine(SourceDirectory(root), "VERSION"))).Trim() != VERSION)
                throw new InvalidDataException("The musl tarball does not hold the pinned release.");
            await File.WriteAllTextAsync(stamp, URL + "\n");
        }
        return SourceDirectory(root);
    }

    /// <summary>
    /// Requires the prepared source tree that <c>setup</c> extracts.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The source tree.</returns>
    public static string RequireSources(string root)
    {
        var directory = SourceDirectory(root);
        if (!File.Exists(Path.Combine(Path.GetDirectoryName(directory)!, "verified-" + TARBALL_SHA256)) ||
            !Directory.Exists(Path.Combine(directory, "src")))
            throw new InvalidOperationException($"Pinned musl {VERSION} is missing. Run: dotnet run --project tools/WitOS.Dev -- setup");
        return directory;
    }

    /// <summary>
    /// Builds libc.a and crt1.o of an architecture under artifacts/substrate/&lt;architecture&gt;, once per set of
    /// inputs (the pin, the patches, the sysdeps, the options and the compiler).
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>The build.</returns>
    public static async Task<LibcBuild> BuildAsync(string root, KernelArchitecture architecture)
    {
        Toolchain.RequireClang(root);
        var sources = RequireSources(root);
        var arch = MuslArchitecture(architecture);
        var output = Path.Combine(root, "artifacts", "substrate", architecture.Name);
        Directory.CreateDirectory(output);
        var generated = Path.Combine(output, "generated");
        var overlay = Path.Combine(output, "overlay");
        var build = new LibcBuild(Path.Combine(output, "libc.a"), Path.Combine(output, "crt1.o"), Path.Combine(output, "rcrt1.o"),
            Path.Combine(output, "static.ld"),
            [overlay, Path.Combine(generated, "include"), Path.Combine(sources, "include"), Path.Combine(sources, "arch", arch),
                Path.Combine(sources, "arch", "generic")]);

        var sysdeps = Directory.GetFiles(Path.Combine(root, SYSDEPS)).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var patchTexts = PATCHED.Values.Select(name => File.ReadAllText(Path.Combine(UpstreamPatches.Directory(root), "musl", name + ".patch")));
        // The ABI-1 headers WitOS's part compiles against, the kernel's and the sysroot's, count too: their structures and
        // constants are compiled into the library (K8.4b found a stale libc.a after THREAD_INFO changed).
        var abiHeaders = new[] { Path.Combine(root, "src", "Kernel", "include"), Path.Combine(root, "src", "Sysroot", "include") }
            .SelectMany(directory => Directory.GetFiles(directory, "*.h", SearchOption.AllDirectories))
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/') + "\n" + File.ReadAllText(path));
        var stamp = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
            [VERSION, TARBALL_SHA256, Toolchain.LLVM_VERSION, architecture.Triple, .. architecture.ClangOptions, .. OMITTED,
                .. patchTexts, .. sysdeps.Select(File.ReadAllText), .. abiHeaders,
                string.Join(' ', LibcOptions(architecture, build.Includes, sources, generated))])))).ToLowerInvariant();
        // The generated and patched headers are written on every call, whatever the stamp says, so that the headers the
        // build hands its users always follow the tool.
        await GenerateHeadersAsync(root, sources, arch, generated, overlay);
        var stampPath = Path.Combine(output, "stamp.txt");
        if (File.Exists(build.Library) && File.Exists(build.Crt1) && File.Exists(build.Rcrt1) && File.Exists(build.LinkerScript) &&
            File.Exists(stampPath) &&
            await File.ReadAllTextAsync(stampPath) == stamp)
            return build;
        File.Delete(stampPath);

        var objects = Path.Combine(output, "obj");
        if (Directory.Exists(objects))
            Directory.Delete(objects, recursive: true);
        Directory.CreateDirectory(objects);
        var files = await LibrarySourcesAsync(root, sources, arch, Path.Combine(output, "patched"));
        var clang = Toolchain.Clang(root);
        var options = LibcOptions(architecture, build.Includes, sources, generated);
        var produced = new List<string>();
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            async (source, _) =>
            {
                var obj = Path.Combine(objects, source.Relative.Replace('/', '_') + ".o");
                await Processes.RequireSuccessAsync(clang, [.. options, "-c", source.File, "-o", obj], root);
                lock (produced)
                    produced.Add(obj);
            });
        // WitOS's part of the library: strict warnings, the kernel's ABI headers and the sysroot's transport.
        string[] sysdepOptions = [.. options.Where(option => option != "-w"), "-Wall", "-Wextra", "-Werror",
            "-I", Path.Combine(root, "src", "Sysroot", "include"), "-I", Path.Combine(root, "src", "Kernel", "include")];
        foreach (var file in sysdeps.Where(path => path.EndsWith(".c", StringComparison.Ordinal)))
        {
            if (Path.GetFileName(file) == "rcrt1.c")
            {
                // The startup of a started program (S5.2) is musl's dlstart.c, compiled as musl compiles its startup
                // files: the library's options with -DCRT and -fPIC.
                await Processes.RequireSuccessAsync(clang,
                    [.. options, "-DCRT", "-fPIC", "-I", Path.Combine(sources, "ldso"), "-c", file, "-o", build.Rcrt1], root);
                continue;
            }
            var obj = Path.Combine(objects, "witos_" + Path.GetFileNameWithoutExtension(file) + ".o");
            await Processes.RequireSuccessAsync(clang, [.. sysdepOptions, "-c", file, "-o", obj], root);
            if (Path.GetFileName(file) == "crt1.c")
                File.Copy(obj, build.Crt1, overwrite: true);
            else
                produced.Add(obj);
        }
        File.Delete(build.Library);
        var list = Path.Combine(output, "objects.rsp");
        await File.WriteAllLinesAsync(list, produced.OrderBy(path => path, StringComparer.Ordinal).Select(path => '"' + path.Replace('\\', '/') + '"'));
        await Processes.RequireSuccessAsync(Toolchain.LlvmAr(root), ["rcs", build.Library, "@" + list], root);
        File.Copy(Path.Combine(root, SYSDEPS, "static.ld"), build.LinkerScript, overwrite: true);
        await File.WriteAllTextAsync(stampPath, stamp);
        Console.WriteLine($"musl {VERSION} for {architecture.Triple}: {produced.Count} objects in libc.a.");
        return build;
    }

    /// <summary>
    /// Builds musl as libc.so, which is also the dynamic linker, and Scrt1.o, the startup of a dynamic program (plan
    /// step S5.3), under artifacts/substrate/&lt;architecture&gt;/shared, once per set of inputs. As musl's Makefile
    /// does, every source of libc.a is compiled again with -fPIC, ldso/dlstart.c and ldso/dynlink.c join them (the
    /// latter with WitOS's start hook, patches/musl/dynlink.c.patch), and the objects are linked with -shared,
    /// -e _dlstart and musl's linker options; WitOS's part of the library joins with -fPIC, less the startup objects of
    /// static programs.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>The build.</returns>
    public static async Task<SharedLibcBuild> BuildSharedAsync(string root, KernelArchitecture architecture)
    {
        var build = await BuildAsync(root, architecture);
        var sources = RequireSources(root);
        var arch = MuslArchitecture(architecture);
        var substrate = Path.Combine(root, "artifacts", "substrate", architecture.Name);
        var output = Path.Combine(substrate, "shared");
        Directory.CreateDirectory(output);
        var shared = new SharedLibcBuild(output, Path.Combine(output, "libc.so"), Path.Combine(output, "Scrt1.o"));
        var options = LibcOptions(architecture, build.Includes, sources, Path.Combine(substrate, "generated"))
            .Select(option => option == "-fPIE" ? "-fPIC" : option).ToArray();
        var stamp = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
            [await File.ReadAllTextAsync(Path.Combine(substrate, "stamp.txt")), .. options, .. SharedLinkOptions(sources)])))).ToLowerInvariant();
        var stampPath = Path.Combine(output, "stamp.txt");
        if (File.Exists(shared.Library) && File.Exists(shared.Scrt1) && File.Exists(stampPath) && await File.ReadAllTextAsync(stampPath) == stamp)
            return shared;
        File.Delete(stampPath);

        var objects = Path.Combine(output, "obj");
        if (Directory.Exists(objects))
            Directory.Delete(objects, recursive: true);
        Directory.CreateDirectory(objects);
        var patched = Path.Combine(output, "patched");
        Directory.CreateDirectory(patched);
        var dynlink = Path.Combine(patched, "dynlink.c");
        await File.WriteAllTextAsync(dynlink, await PatchedAsync(root, sources, "ldso/dynlink.c"));
        var clang = Toolchain.Clang(root);
        var produced = new List<string>();
        var files = (await LibrarySourcesAsync(root, sources, arch, Path.Combine(substrate, "patched")))
            .Append((File: Path.Combine(sources, "ldso", "dlstart.c"), Relative: "ldso/dlstart.c"))
            .Append((File: dynlink, Relative: "ldso/dynlink.c")).ToList();
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            async (source, _) =>
            {
                var obj = Path.Combine(objects, source.Relative.Replace('/', '_') + ".lo");
                await Processes.RequireSuccessAsync(clang, [.. options, "-c", source.File, "-o", obj], root);
                lock (produced)
                    produced.Add(obj);
            });
        string[] sysdepOptions = [.. options.Where(option => option != "-w"), "-Wall", "-Wextra", "-Werror",
            "-I", Path.Combine(root, "src", "Sysroot", "include"), "-I", Path.Combine(root, "src", "Kernel", "include")];
        foreach (var file in Directory.GetFiles(Path.Combine(root, SYSDEPS), "*.c").Order(StringComparer.Ordinal)
            .Where(path => !STATIC_STARTUP.Contains(Path.GetFileName(path))))
        {
            var obj = Path.Combine(objects, "witos_" + Path.GetFileNameWithoutExtension(file) + ".lo");
            await Processes.RequireSuccessAsync(clang, [.. sysdepOptions, "-c", file, "-o", obj], root);
            produced.Add(obj);
        }
        // musl's startup of a dynamic program: crt1.c compiled position-independent (crt/Scrt1.c).
        await Processes.RequireSuccessAsync(clang,
            [.. options, "-DCRT", "-c", Path.Combine(sources, "crt", "Scrt1.c"), "-o", shared.Scrt1], root);
        var builtins = await CompilerRtBuiltins.BuildAsync(root, architecture, build.Includes.Skip(1));
        var list = Path.Combine(output, "objects.rsp");
        await File.WriteAllLinesAsync(list, produced.OrderBy(path => path, StringComparer.Ordinal).Select(path => '"' + path.Replace('\\', '/') + '"'));
        await Processes.RequireSuccessAsync(Toolchain.Lld(root),
            ["-o", shared.Library, .. SharedLinkOptions(sources), "@" + list, builtins], root);
        await File.WriteAllTextAsync(stampPath, stamp);
        Console.WriteLine($"musl {VERSION} for {architecture.Triple}: {produced.Count} objects in libc.so.");
        return shared;
    }

    // musl's link of libc.so (its Makefile and the LDFLAGS its configure finds for lld), and WitOS's page size, unwind
    // index and no build id.
    private static string[] SharedLinkOptions(string sources) =>
    [
        "-shared", "-e", "_dlstart", "--sort-section=alignment", "--sort-common", "--gc-sections", "--hash-style=both",
        "--no-undefined", "--exclude-libs=ALL", "--dynamic-list=" + Path.Combine(sources, "dynamic.list").Replace('\\', '/'),
        "-z", "max-page-size=4096", "--eh-frame-hdr", "--build-id=none"
    ];

    /// <summary>
    /// Compiles C sources against the library's headers and links them with crt1 and libc.a into a static ELF
    /// executable at the component's image window.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <param name="output">Directory for the objects and the executable.</param>
    /// <param name="name">Program name (the executable is name.elf).</param>
    /// <param name="sources">C source files.</param>
    /// <returns>Path of the executable.</returns>
    public static async Task<string> LinkProgramAsync(string root, KernelArchitecture architecture, string output, string name,
        IEnumerable<string> sources)
    {
        var objects = new List<string>();
        foreach (var source in sources)
        {
            var obj = Path.Combine(output, name + "." + Path.GetFileNameWithoutExtension(source) + ".o");
            await CompileAsync(root, architecture, source, obj, ["-std=c11", "-Wall", "-Wextra", "-Werror"]);
            objects.Add(obj);
        }
        return await LinkAsync(root, architecture, output, name, objects);
    }

    /// <summary>
    /// Compiles one C source against the library's headers: optimized, position-independent code for the fixed link,
    /// no stack protector or unwind tables, with the caller's dialect and warning options.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <param name="source">C source file.</param>
    /// <param name="obj">Object file to write.</param>
    /// <param name="options">Dialect, warning and definition options.</param>
    public static async Task CompileAsync(string root, KernelArchitecture architecture, string source, string obj, IEnumerable<string> options)
    {
        var build = await BuildAsync(root, architecture);
        await Processes.RequireSuccessAsync(Toolchain.Clang(root),
        [
            $"--target={architecture.Triple}", "-O2", "-nostdlibinc", "-fPIE", "-fno-plt", "-fno-stack-protector",
            "-fno-asynchronous-unwind-tables", "-fno-unwind-tables", .. options, .. architecture.ClangOptions,
            .. build.Includes.Skip(1).SelectMany(include => new[] { "-isystem", include }),
            "-c", source, "-o", obj
        ], root);
    }

    /// <summary>
    /// Links objects with crt1, libc.a and the compiler's builtins into a static ELF executable at the image window.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <param name="output">Directory for the executable.</param>
    /// <param name="name">Program name (the executable is name.elf).</param>
    /// <param name="objects">Object files.</param>
    /// <param name="libraries">Archives linked between the objects and libc.a (the C++ runtime, S4).</param>
    /// <returns>Path of the executable.</returns>
    public static async Task<string> LinkAsync(string root, KernelArchitecture architecture, string output, string name, IEnumerable<string> objects,
        IEnumerable<string>? libraries = null)
    {
        var build = await BuildAsync(root, architecture);
        // clang emits calls to compiler-rt's soft-float helpers for long double on aarch64; the pinned subset supplies them.
        var builtins = await CompilerRtBuiltins.BuildAsync(root, architecture, build.Includes.Skip(1));
        var image = Path.Combine(output, name + ".elf");
        await Processes.RequireSuccessAsync(Toolchain.Lld(root),
        [
            "-o", image, "-static", "--no-dynamic-linker", "--build-id=none", "-z", "max-page-size=4096", "-z", "norelro",
            "--gc-sections", "--eh-frame-hdr", "-e", "_start", "-T", build.LinkerScript, build.Crt1, .. objects, .. libraries ?? [],
            build.Library, builtins
        ], root);
        return image;
    }

    /// <summary>
    /// Links objects with rcrt1, libc.a and the compiler's builtins into a static position-independent executable, the
    /// program another process starts (plan step S5.2, witos/start.h): ELF type ET_DYN with a dynamic section and no
    /// interpreter, whose relocations are all relative ones, which musl's dlstart.c applies at the program's start.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <param name="output">Directory for the executable.</param>
    /// <param name="name">Program name (the executable is name.elf).</param>
    /// <param name="objects">Object files.</param>
    /// <param name="libraries">Archives linked between the objects and libc.a.</param>
    /// <returns>Path of the executable.</returns>
    public static async Task<string> LinkStartedProgramAsync(string root, KernelArchitecture architecture, string output, string name,
        IEnumerable<string> objects, IEnumerable<string>? libraries = null)
    {
        var build = await BuildAsync(root, architecture);
        var builtins = await CompilerRtBuiltins.BuildAsync(root, architecture, build.Includes.Skip(1));
        var image = Path.Combine(output, name + ".elf");
        await Processes.RequireSuccessAsync(Toolchain.Lld(root),
        [
            "-o", image, "-static", "-pie", "--no-dynamic-linker", "-z", "text", "--build-id=none", "-z", "max-page-size=4096",
            "-z", "norelro", "--gc-sections", "--eh-frame-hdr", "-e", "_start", build.Rcrt1, .. objects, .. libraries ?? [],
            build.Library, builtins
        ], root);
        StartedProgram.Validate(await File.ReadAllBytesAsync(image), architecture.ElfMachine);
        return image;
    }

    /// <summary>
    /// The dynamic linker's path in a dynamic program's PT_INTERP and in the boot package: musl's LDSO_PATHNAME.
    /// </summary>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>The absolute path.</returns>
    public static string InterpreterPath(KernelArchitecture architecture) => $"/lib/ld-musl-{MuslArchitecture(architecture)}.so.1";

    /// <summary>
    /// Links objects into a dynamic position-independent executable (plan step S5.3): musl's Scrt1.o, compiler-rt's
    /// crtbeginS.o (S5.4), the shared libraries the program needs and libc.so, recorded as DT_NEEDED "libc.so", which musl's dynamic linker resolves
    /// to itself; PT_INTERP names the dynamic linker.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <param name="output">Directory for the executable.</param>
    /// <param name="name">Program name (the executable is name.elf).</param>
    /// <param name="objects">Object files.</param>
    /// <param name="libraries">Shared libraries the program needs, by path; DT_NEEDED records their sonames.</param>
    /// <param name="options">Further linker options (libc-test's -rdynamic, S7.1).</param>
    /// <returns>Path of the executable.</returns>
    public static async Task<string> LinkDynamicProgramAsync(string root, KernelArchitecture architecture, string output, string name,
        IEnumerable<string> objects, IEnumerable<string>? libraries = null, IEnumerable<string>? options = null)
    {
        var build = await BuildAsync(root, architecture);
        var shared = await BuildSharedAsync(root, architecture);
        var builtins = await CompilerRtBuiltins.BuildAsync(root, architecture, build.Includes.Skip(1));
        var crtBegin = await CompilerRtBuiltins.BuildCrtBeginAsync(root, architecture, build.Includes.Skip(1));
        var image = Path.Combine(output, name + ".elf");
        await Processes.RequireSuccessAsync(Toolchain.Lld(root),
        [
            "-o", image, "-pie", "--dynamic-linker=" + InterpreterPath(architecture), .. SEPARATE_SEGMENTS, "--eh-frame-hdr",
            "--build-id=none", "--gc-sections", "-e", "_start", .. options ?? [], shared.Scrt1, crtBegin, .. objects, .. libraries ?? [],
            "-L", shared.Directory, "-lc", builtins
        ], root);
        return image;
    }

    /// <summary>
    /// Links position-independent objects into a shared library with its soname, against libc.so (plan step S5.3),
    /// after compiler-rt's crtbeginS.o, which gives the library its own __dso_handle (S5.4).
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <param name="output">Directory for the library.</param>
    /// <param name="soname">The library's soname and file name.</param>
    /// <param name="objects">Object files compiled with -fPIC.</param>
    /// <param name="libraries">Shared libraries the library needs, by path.</param>
    /// <returns>Path of the library.</returns>
    public static async Task<string> LinkSharedLibraryAsync(string root, KernelArchitecture architecture, string output, string soname,
        IEnumerable<string> objects, IEnumerable<string>? libraries = null)
    {
        var build = await BuildAsync(root, architecture);
        var shared = await BuildSharedAsync(root, architecture);
        var builtins = await CompilerRtBuiltins.BuildAsync(root, architecture, build.Includes.Skip(1));
        var crtBegin = await CompilerRtBuiltins.BuildCrtBeginAsync(root, architecture, build.Includes.Skip(1));
        var library = Path.Combine(output, soname);
        var list = library + ".rsp";
        await File.WriteAllLinesAsync(list, objects.Select(path => '"' + path.Replace('\\', '/') + '"'));
        await Processes.RequireSuccessAsync(Toolchain.Lld(root),
        [
            "-o", library, "-shared", "-soname", soname, .. SEPARATE_SEGMENTS, "--eh-frame-hdr", "--build-id=none", "--gc-sections",
            "--no-undefined", crtBegin, "@" + list, .. libraries ?? [], "-L", shared.Directory, "-lc", builtins
        ], root);
        return library;
    }

    /// <summary>
    /// Builds a libc program as the root task's flat image (plan step S1.1): the program's first log line names the
    /// library and the ISA.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Artifact directory of the scenario.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <param name="program">C source under tests/User.</param>
    /// <returns>Path of the flat image.</returns>
    public static async Task<string> BuildRootAsync(string root, string output, KernelArchitecture architecture, string program)
    {
        var image = await LinkProgramAsync(root, architecture, output, "RootFixture", [Path.Combine(root, "tests", "User", program)]);
        return await FlatImage.FromElfAsync(output, architecture.ElfMachine, image, "RootFixture", "wit_user_root_image", "user_root_image.h");
    }

    /// <summary>
    /// musl's architecture name of a kernel architecture.
    /// </summary>
    /// <param name="architecture">Kernel architecture.</param>
    /// <returns>x86_64 or aarch64.</returns>
    public static string MuslArchitecture(KernelArchitecture architecture) => architecture.Triple[..architecture.Triple.IndexOf('-')];

    /// <summary>
    /// musl's installed headers WitOS patches, by their path under include: the build's generated headers hold their
    /// patched text, which the sysroot installs over musl's own (R1.3).
    /// </summary>
    /// <returns>The paths.</returns>
    public static IEnumerable<string> PatchedHeaders() =>
        PATCHED.Keys.Where(path => path.StartsWith("include/", StringComparison.Ordinal)).Select(path => path["include/".Length..]);

    /// <summary>
    /// The sources of one architecture as musl's Makefile selects them: every generic C file whose name no
    /// architecture file replaces, and the architecture files, less the omitted ones.
    /// </summary>
    /// <param name="sources">Source tree.</param>
    /// <param name="arch">musl architecture.</param>
    /// <returns>Absolute file paths in a stable order.</returns>
    public static List<string> SelectSources(string sources, string arch)
    {
        var directories = Directory.GetDirectories(Path.Combine(sources, "src")).Append(Path.Combine(sources, "src", "malloc", "mallocng"))
            .OrderBy(path => path, StringComparer.Ordinal);
        var selected = new List<string>();
        foreach (var directory in directories)
        {
            var archDirectory = Path.Combine(directory, arch);
            var replaced = new HashSet<string>(StringComparer.Ordinal);
            if (Directory.Exists(archDirectory))
            {
                foreach (var file in Directory.GetFiles(archDirectory).OrderBy(path => path, StringComparer.Ordinal))
                {
                    var extension = Path.GetExtension(file);
                    if (extension is not (".c" or ".s" or ".S") || IsOmitted(sources, file))
                        continue;
                    replaced.Add(Path.GetFileNameWithoutExtension(file));
                    selected.Add(file);
                }
            }
            foreach (var file in Directory.GetFiles(directory, "*.c").OrderBy(path => path, StringComparer.Ordinal))
            {
                if (!replaced.Contains(Path.GetFileNameWithoutExtension(file)) && !IsOmitted(sources, file))
                    selected.Add(file);
            }
        }
        return selected;
    }

    // The sources of the library, each with its path in musl's tree: a file patches/musl changes is compiled from
    // its patched text, written under the directory given at the same relative path (S7.2), and its includes are
    // musl's include directories. An architecture's file that replaces a patched generic one is compiled from an
    // unchanged copy beside the patched text, since it includes the generic file by its relative path where the
    // architecture lacks the instruction (x86_64's fma.c includes "../fma.c" without FMA).
    private static async Task<List<(string File, string Relative)>> LibrarySourcesAsync(
        string root, string sources, string arch, string patched)
    {
        async Task<string> WriteAsync(string relative, string text)
        {
            var copy = Path.Combine(patched, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            await File.WriteAllTextAsync(copy, text);
            return copy;
        }

        var files = new List<(string File, string Relative)>();
        foreach (var file in SelectSources(sources, arch))
        {
            var relative = Path.GetRelativePath(sources, file).Replace('\\', '/');
            var directory = Path.GetDirectoryName(relative)!.Replace('\\', '/');
            var generic = Path.GetFileName(directory) == arch
                ? Path.GetDirectoryName(directory)!.Replace('\\', '/') + "/" + Path.GetFileName(relative)
                : null;
            if (PATCHED.ContainsKey(relative))
                files.Add((await WriteAsync(relative, await PatchedAsync(root, sources, relative)), relative));
            else if (generic is not null && PATCHED.ContainsKey(generic))
            {
                await WriteAsync(generic, await PatchedAsync(root, sources, generic));
                files.Add((await WriteAsync(relative, await File.ReadAllTextAsync(file)), relative));
            }
            else
                files.Add((file, relative));
        }
        return files;
    }

    /// <summary>
    /// musl's tools/mkalltypes.sed as a function: TYPEDEF, STRUCT and UNION lines become guarded definitions.
    /// </summary>
    /// <param name="input">The concatenated alltypes.h.in files.</param>
    /// <returns>bits/alltypes.h.</returns>
    public static string AllTypes(string input)
    {
        var result = new StringBuilder();
        foreach (var line in input.Replace("\r\n", "\n").TrimEnd('\n').Split('\n'))
        {
            Match match;
            if ((match = TYPEDEF.Match(line)).Success)
            {
                var name = match.Groups[2].Value;
                result.Append($"#if defined(__NEED_{name}) && !defined(__DEFINED_{name})\ntypedef {match.Groups[1].Value} {name};\n#define __DEFINED_{name}\n#endif\n\n");
            }
            else if ((match = STRUCT.Match(line)).Success)
            {
                var name = match.Groups[1].Value;
                result.Append($"#if defined(__NEED_struct_{name}) && !defined(__DEFINED_struct_{name})\nstruct {name} {match.Groups[2].Value};\n#define __DEFINED_struct_{name}\n#endif\n\n");
            }
            else if ((match = UNION.Match(line)).Success)
            {
                var name = match.Groups[1].Value;
                result.Append($"#if defined(__NEED_union_{name}) && !defined(__DEFINED_union_{name})\nunion {name} {match.Groups[2].Value};\n#define __DEFINED_union_{name}\n#endif\n\n");
            }
            else
            {
                result.Append(line).Append('\n');
            }
        }
        return result.ToString();
    }

    /// <summary>
    /// musl's bits/syscall.h: the __NR_ definitions followed by their SYS_ copies.
    /// </summary>
    /// <param name="input">arch/&lt;arch&gt;/bits/syscall.h.in.</param>
    /// <returns>bits/syscall.h.</returns>
    public static string SyscallNumbers(string input)
    {
        var text = input.Replace("\r\n", "\n");
        var result = new StringBuilder(text);
        if (!text.EndsWith('\n'))
            result.Append('\n');
        foreach (var line in text.TrimEnd('\n').Split('\n'))
        {
            var index = line.IndexOf("__NR_", StringComparison.Ordinal);
            if (index >= 0)
                result.Append(line[..index]).Append("SYS_").Append(line[(index + 5)..]).Append('\n');
        }
        return result.ToString();
    }

    #endregion

    #region Tools

    private static bool IsOmitted(string sources, string file)
    {
        var relative = Path.GetRelativePath(sources, file).Replace('\\', '/');
        return OMITTED.Contains(relative, StringComparer.Ordinal);
    }

    // musl's configure and Makefile options in clang's terms, less GCC-only tuning; warnings off for upstream code.
    // -std=c99 keeps GCC, musl's compiler, from contracting a*b+c into a fused multiply-add, while clang contracts in
    // every mode: -ffp-contract=off gives clang GCC's meaning of musl's options, as the configure change proposed on
    // musl's list in 2025 does. Contracted, atanh and yn of aarch64 miss libc-test's bounds (S7.2).
    private static string[] LibcOptions(KernelArchitecture architecture, IReadOnlyList<string> includes, string sources, string generated) =>
    [
        $"--target={architecture.Triple}", "-std=c99", "-nostdinc", "-ffreestanding", "-fexcess-precision=standard", "-frounding-math",
        "-ffp-contract=off",
        "-fno-strict-aliasing", "-Wa,--noexecstack", "-fno-stack-protector", "-D_XOPEN_SOURCE=700", "-Os", "-fomit-frame-pointer",
        "-fno-unwind-tables", "-fno-asynchronous-unwind-tables", "-ffunction-sections", "-fdata-sections", "-fPIE", "-fno-plt",
        "-w", "-Qunused-arguments", .. architecture.ClangOptions,
        "-I", includes[0], "-I", includes[3], "-I", includes[4], "-I", Path.Combine(generated, "src", "internal"),
        "-I", Path.Combine(sources, "src", "include"), "-I", Path.Combine(sources, "src", "internal"), "-I", includes[1], "-I", includes[2]
    ];

    private static async Task GenerateHeadersAsync(string root, string sources, string arch, string generated, string overlay)
    {
        var bits = Path.Combine(generated, "include", "bits");
        Directory.CreateDirectory(bits);
        Directory.CreateDirectory(Path.Combine(generated, "src", "internal"));
        Directory.CreateDirectory(overlay);
        var allTypes = await File.ReadAllTextAsync(Path.Combine(sources, "arch", arch, "bits", "alltypes.h.in")) +
            await File.ReadAllTextAsync(Path.Combine(sources, "include", "alltypes.h.in"));
        await File.WriteAllTextAsync(Path.Combine(bits, "alltypes.h"), AllTypes(allTypes));
        await File.WriteAllTextAsync(Path.Combine(bits, "syscall.h"),
            SyscallNumbers(await File.ReadAllTextAsync(Path.Combine(sources, "arch", arch, "bits", "syscall.h.in"))));
        await File.WriteAllTextAsync(Path.Combine(generated, "src", "internal", "version.h"), $"#define VERSION \"{VERSION}\"\n");
        await File.WriteAllTextAsync(Path.Combine(overlay, "syscall_arch.h"), await PatchedAsync(root, sources, $"arch/{arch}/syscall_arch.h"));
        // A patched installed header joins the generated ones, where musl's build has obj/include: after musl's internal
        // wrappers (src/include), which include the original by its relative path, and before musl's include, so that
        // every program sees the patched text.
        foreach (var header in PatchedHeaders())
        {
            var target = Path.Combine(generated, "include", header);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, await PatchedAsync(root, sources, "include/" + header));
        }
    }

    // The text of a pinned upstream file with its patch applied: the file's bytes must be the ones the lock pins.
    private static async Task<string> PatchedAsync(string root, string sources, string source)
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(sources, source));
        var pinned = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, LOCK))).RootElement;
        if (pinned.GetProperty("sha256").GetString() != TARBALL_SHA256 || pinned.GetProperty("version").GetString() != VERSION ||
            !pinned.GetProperty("sources").EnumerateArray().Any(entry => entry.GetProperty("path").GetString() == source &&
                entry.GetProperty("sha256").GetString() == Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()))
            throw new InvalidDataException($"{LOCK} does not pin {source} as extracted from the tarball.");
        var text = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n");
        return UpstreamPatches.Apply(root, "musl", source, PATCHED[source], text);
    }

    #endregion
}

/// <summary>
/// The products of a libc build.
/// </summary>
/// <param name="Library">libc.a.</param>
/// <param name="Crt1">WitOS's startup object of the root task.</param>
/// <param name="Rcrt1">The startup object of a program another process starts: musl's dlstart.c and WitOS's start (S5.2).</param>
/// <param name="LinkerScript">The linker script of a static program at the image window.</param>
/// <param name="Includes">Include directories in search order: the patched overlay, the generated headers, musl's
/// public headers, the architecture's bits and the generic bits.</param>
internal sealed record LibcBuild(string Library, string Crt1, string Rcrt1, string LinkerScript, IReadOnlyList<string> Includes);

/// <summary>
/// The products of the shared libc build (plan step S5.3).
/// </summary>
/// <param name="Directory">The directory a link searches for libc.so (-L).</param>
/// <param name="Library">libc.so, which is also the dynamic linker.</param>
/// <param name="Scrt1">musl's startup object of a dynamic program.</param>
internal sealed record SharedLibcBuild(string Directory, string Library, string Scrt1);
