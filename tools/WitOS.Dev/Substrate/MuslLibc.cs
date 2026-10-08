using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using WitOS.Dev.Host;
using WitOS.Dev.Images;
using WitOS.Dev.Kernel;
using WitOS.Dev.NativeAot;

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
        "src/thread/__set_thread_area.c", "src/thread/clone.c", "src/thread/__unmapself.c"
    ];

    // The patched upstream files: the per-architecture system call stubs become calls into WitOS's dispatch.
    private static readonly Dictionary<string, string> PATCHED = new(StringComparer.Ordinal)
    {
        ["arch/x86_64/syscall_arch.h"] = "syscall_arch.x86_64.h",
        ["arch/aarch64/syscall_arch.h"] = "syscall_arch.aarch64.h"
    };

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
        var build = new LibcBuild(Path.Combine(output, "libc.a"), Path.Combine(output, "crt1.o"), Path.Combine(output, "static.ld"),
            [overlay, Path.Combine(generated, "include"), Path.Combine(sources, "include"), Path.Combine(sources, "arch", arch),
                Path.Combine(sources, "arch", "generic")]);

        var sysdeps = Directory.GetFiles(Path.Combine(root, SYSDEPS)).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var patchTexts = PATCHED.Values.Select(name => File.ReadAllText(Path.Combine(UpstreamPatches.Directory(root), "musl", name + ".patch")));
        var stamp = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
            [VERSION, TARBALL_SHA256, Toolchain.LLVM_VERSION, architecture.Triple, .. architecture.ClangOptions, .. OMITTED,
                .. patchTexts, .. sysdeps.Select(File.ReadAllText), string.Join(' ', LibcOptions(architecture, build.Includes, sources, generated))])))).ToLowerInvariant();
        var stampPath = Path.Combine(output, "stamp.txt");
        if (File.Exists(build.Library) && File.Exists(build.Crt1) && File.Exists(build.LinkerScript) && File.Exists(stampPath) &&
            await File.ReadAllTextAsync(stampPath) == stamp)
            return build;
        File.Delete(stampPath);

        await GenerateHeadersAsync(root, sources, arch, generated, overlay);
        var objects = Path.Combine(output, "obj");
        if (Directory.Exists(objects))
            Directory.Delete(objects, recursive: true);
        Directory.CreateDirectory(objects);
        var files = SelectSources(sources, arch);
        var clang = Toolchain.Clang(root);
        var options = LibcOptions(architecture, build.Includes, sources, generated);
        var produced = new List<string>();
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            async (file, _) =>
            {
                var relative = Path.GetRelativePath(sources, file).Replace('\\', '/');
                var obj = Path.Combine(objects, relative.Replace('/', '_') + ".o");
                await Processes.RequireSuccessAsync(clang, [.. options, "-c", file, "-o", obj], root);
                lock (produced)
                    produced.Add(obj);
            });
        // WitOS's part of the library: strict warnings, the kernel's ABI headers and the sysroot's transport.
        string[] sysdepOptions = [.. options.Where(option => option != "-w"), "-Wall", "-Wextra", "-Werror",
            "-I", Path.Combine(root, "src", "Sysroot", "include"), "-I", Path.Combine(root, "src", "Kernel", "include")];
        foreach (var file in sysdeps.Where(path => path.EndsWith(".c", StringComparison.Ordinal)))
        {
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
    private static string[] LibcOptions(KernelArchitecture architecture, IReadOnlyList<string> includes, string sources, string generated) =>
    [
        $"--target={architecture.Triple}", "-std=c99", "-nostdinc", "-ffreestanding", "-fexcess-precision=standard", "-frounding-math",
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
        var source = $"arch/{arch}/syscall_arch.h";
        var bytes = await File.ReadAllBytesAsync(Path.Combine(sources, source));
        var pinned = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, LOCK))).RootElement;
        if (pinned.GetProperty("sha256").GetString() != TARBALL_SHA256 || pinned.GetProperty("version").GetString() != VERSION ||
            !pinned.GetProperty("sources").EnumerateArray().Any(entry => entry.GetProperty("path").GetString() == source &&
                entry.GetProperty("sha256").GetString() == Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()))
            throw new InvalidDataException($"{LOCK} does not pin {source} as extracted from the tarball.");
        var text = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n");
        await File.WriteAllTextAsync(Path.Combine(overlay, "syscall_arch.h"), UpstreamPatches.Apply(root, "musl", source, PATCHED[source], text));
    }

    #endregion
}

/// <summary>
/// The products of a libc build.
/// </summary>
/// <param name="Library">libc.a.</param>
/// <param name="Crt1">WitOS's program startup object.</param>
/// <param name="LinkerScript">The linker script of a static program at the image window.</param>
/// <param name="Includes">Include directories in search order: the patched overlay, the generated headers, musl's
/// public headers, the architecture's bits and the generic bits.</param>
internal sealed record LibcBuild(string Library, string Crt1, string LinkerScript, IReadOnlyList<string> Includes);
