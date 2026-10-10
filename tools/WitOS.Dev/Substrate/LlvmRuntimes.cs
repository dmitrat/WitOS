using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WitOS.Dev.Host;
using WitOS.Dev.Kernel;

namespace WitOS.Dev.Substrate;

/// <summary>
/// The C++ runtime of the system layer (RFC 0011 §9.1, plan step S4): LLVM's libunwind, libc++abi and libc++ at the
/// toolchain's release, from the release's source tarballs pinned by the SHA-256 of their downloaded bytes, built by
/// the pinned clang for an architecture's triple over the musl build and compiled unchanged. The build replicates the
/// runtimes' CMake rules for a static Linux-musl build: libc++'s sources for threads, the random device, localization
/// and the filesystem, its <c>__config_site</c> configured from the upstream template and the default assertion
/// handler; libc++abi with exceptions, thread-safe statics, <c>__cxa_thread_atexit</c> and the operators new and
/// delete; libunwind for the native target alone. A C++ program links all three ahead of libc.a.
/// </summary>
internal static class LlvmRuntimes
{
    #region Constants

    /// <summary>
    /// The lock file of the pinned tarballs.
    /// </summary>
    public const string LOCK = "src/Substrate/llvm-runtimes.lock.json";

    /// <summary>
    /// The lock file of the sanitized Linux UAPI headers the runtimes' build includes.
    /// </summary>
    public const string HEADERS_LOCK = "src/Substrate/linux-headers.lock.json";

    private const string REPOSITORY = "https://github.com/llvm/llvm-project";

    private static readonly string[] RUNTIMES = ["libunwind", "libcxxabi", "libcxx"];

    // libc++'s sources (libcxx/src/CMakeLists.txt): the base list, then the thread, random device, localization and
    // filesystem lists, and int128_builtins.cpp since the pinned builtins are not all of compiler-rt; new.cpp stays
    // out because libc++abi defines the operators (LIBCXX_ENABLE_NEW_DELETE_DEFINITIONS is OFF by default).
    private static readonly string[] CXX_SOURCES =
    [
        "algorithm.cpp", "any.cpp", "bind.cpp", "call_once.cpp", "charconv.cpp", "chrono.cpp", "error_category.cpp",
        "exception.cpp", "expected.cpp", "filesystem/filesystem_clock.cpp", "filesystem/filesystem_error.cpp",
        "filesystem/path.cpp", "functional.cpp", "hash.cpp", "memory.cpp", "memory_resource.cpp", "new_handler.cpp",
        "new_helpers.cpp", "optional.cpp", "print.cpp", "random_shuffle.cpp", "ryu/d2fixed.cpp", "ryu/d2s.cpp",
        "ryu/f2s.cpp", "stdexcept.cpp", "string.cpp", "system_error.cpp", "typeinfo.cpp", "valarray.cpp", "variant.cpp",
        "vector.cpp", "verbose_abort.cpp",
        "atomic.cpp", "barrier.cpp", "condition_variable_destructor.cpp", "condition_variable.cpp", "future.cpp",
        "mutex_destructor.cpp", "mutex.cpp", "shared_mutex.cpp", "thread.cpp",
        "random.cpp",
        "fstream.cpp", "ios.cpp", "ios.instantiations.cpp", "iostream.cpp", "locale.cpp", "ostream.cpp", "regex.cpp",
        "strstream.cpp",
        "filesystem/directory_entry.cpp", "filesystem/directory_iterator.cpp", "filesystem/operations.cpp",
        "filesystem/int128_builtins.cpp"
    ];

    // libc++abi's sources (libcxxabi/src/CMakeLists.txt) with exceptions, the operators new and delete and
    // cxa_thread_atexit.cpp, which threads on Unix add (musl has no __cxa_thread_atexit_impl, so its fallback runs).
    private static readonly string[] CXXABI_SOURCES =
    [
        "cxa_aux_runtime.cpp", "cxa_default_handlers.cpp", "cxa_demangle.cpp", "cxa_exception_storage.cpp",
        "cxa_guard.cpp", "cxa_handlers.cpp", "cxa_vector.cpp", "cxa_virtual.cpp", "stdlib_exception.cpp",
        "stdlib_stdexcept.cpp", "stdlib_typeinfo.cpp", "abort_message.cpp", "fallback_malloc.cpp",
        "private_typeinfo.cpp", "stdlib_new_delete.cpp", "cxa_exception.cpp", "cxa_personality.cpp",
        "cxa_thread_atexit.cpp"
    ];

    // libunwind's sources (libunwind/src/CMakeLists.txt): the C++, C and assembly lists.
    private static readonly string[] UNWIND_CXX_SOURCES = ["libunwind.cpp", "Unwind-EHABI.cpp", "Unwind-seh.cpp"];
    private static readonly string[] UNWIND_C_SOURCES = ["UnwindLevel1.c", "UnwindLevel1-gcc-ext.c", "Unwind-sjlj.c", "Unwind-wasm.c"];
    private static readonly string[] UNWIND_ASM_SOURCES = ["UnwindRegistersRestore.S", "UnwindRegistersSave.S"];

    // libc++'s configuration (libcxx/CMakeLists.txt defaults for a static Linux-musl build with pthreads).
    private static readonly Dictionary<string, string?> CONFIG_SITE = new(StringComparer.Ordinal)
    {
        ["_LIBCPP_ABI_VERSION"] = "1",
        ["_LIBCPP_ABI_NAMESPACE"] = "__1",
        ["_LIBCPP_ABI_FORCE_ITANIUM"] = "0",
        ["_LIBCPP_ABI_FORCE_MICROSOFT"] = "0",
        ["_LIBCPP_HAS_THREADS"] = "1",
        ["_LIBCPP_HAS_MONOTONIC_CLOCK"] = "1",
        ["_LIBCPP_HAS_TERMINAL"] = "1",
        ["_LIBCPP_HAS_MUSL_LIBC"] = "1",
        ["_LIBCPP_HAS_THREAD_API_PTHREAD"] = "1",
        ["_LIBCPP_HAS_THREAD_API_EXTERNAL"] = "0",
        ["_LIBCPP_HAS_THREAD_API_WIN32"] = "0",
        ["_LIBCPP_DISABLE_VISIBILITY_ANNOTATIONS"] = null,
        ["_LIBCPP_HAS_VENDOR_AVAILABILITY_ANNOTATIONS"] = "0",
        ["_LIBCPP_NO_VCRUNTIME"] = null,
        ["_LIBCPP_TYPEINFO_COMPARISON_IMPLEMENTATION"] = null,
        ["_LIBCPP_HAS_FILESYSTEM"] = "1",
        ["_LIBCPP_HAS_RANDOM_DEVICE"] = "1",
        ["_LIBCPP_HAS_LOCALIZATION"] = "1",
        ["_LIBCPP_HAS_UNICODE"] = "1",
        ["_LIBCPP_HAS_WIDE_CHARACTERS"] = "1",
        ["_LIBCPP_HAS_NO_STD_MODULES"] = "",
        ["_LIBCPP_HAS_TIME_ZONE_DATABASE"] = "0",
        ["_LIBCPP_INSTRUMENTED_WITH_ASAN"] = "0",
        ["_LIBCPP_PSTL_BACKEND_SERIAL"] = "",
        ["_LIBCPP_PSTL_BACKEND_STD_THREAD"] = null,
        ["_LIBCPP_PSTL_BACKEND_LIBDISPATCH"] = null,
        ["_LIBCPP_HARDENING_MODE_DEFAULT"] = "_LIBCPP_HARDENING_MODE_NONE",
        ["_LIBCPP_ABI_DEFINES"] = "",
        ["_LIBCPP_EXTRA_SITE_DEFINES"] = ""
    };

    private static readonly Regex CMAKEDEFINE = new(@"^#cmakedefine(01)? (\w+)(?: (.*))?$");
    private static readonly Regex VARIABLE = new(@"@(\w+)@");

    #endregion

    #region Functions

    /// <summary>
    /// Reads and checks the lock file.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The pin.</returns>
    public static async Task<LlvmRuntimesPin> ReadPinAsync(string root)
    {
        var pin = JsonSerializer.Deserialize<LlvmRuntimesPin>(await File.ReadAllTextAsync(Path.Combine(root, LOCK)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("Missing LLVM runtimes pin.");
        if (pin.Repository != REPOSITORY || pin.Version != Toolchain.LLVM_VERSION || pin.Tag != "llvmorg-" + pin.Version ||
            !pin.Tarballs.Select(tarball => tarball.Name).SequenceEqual(RUNTIMES) ||
            pin.Tarballs.Any(tarball => !Regex.IsMatch(tarball.Sha256, "^[0-9a-f]{64}$")) ||
            !pin.Libc.Any(source => source.Path == "libc/LICENSE.TXT"))
            throw new InvalidDataException("Unsupported LLVM runtimes pin.");
        foreach (var source in pin.Libc)
        {
            if (!Regex.IsMatch(source.Sha256, "^[0-9a-f]{64}$") || !source.Path.StartsWith("libc/", StringComparison.Ordinal) ||
                source.Path.Contains('\\') || source.Path.Split('/').Any(part => part is "" or "." or ".."))
                throw new InvalidDataException("Invalid LLVM libc file entry: " + source.Path);
        }
        return pin;
    }

    /// <summary>
    /// The directory that holds the extracted runtimes.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The directory.</returns>
    public static string Directory(string root) => Path.Combine(root, ".tools", "llvm-runtimes-" + Toolchain.LLVM_VERSION);

    /// <summary>
    /// The source tree of one runtime.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="name">libunwind, libcxxabi or libcxx.</param>
    /// <returns>The tree.</returns>
    public static string SourceDirectory(string root, string name) => Path.Combine(Directory(root), $"{name}-{Toolchain.LLVM_VERSION}.src");

    /// <summary>
    /// The directory that holds the pinned LLVM libc headers under their llvm-project paths (libc/...).
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The directory.</returns>
    public static string LibcDirectory(string root) => Path.Combine(Directory(root), "llvm-project");

    /// <summary>
    /// Reads and checks the pin of the Linux UAPI headers.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The pin.</returns>
    public static async Task<KernelHeadersPin> ReadHeadersPinAsync(string root)
    {
        var pin = JsonSerializer.Deserialize<KernelHeadersPin>(await File.ReadAllTextAsync(Path.Combine(root, HEADERS_LOCK)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("Missing Linux headers pin.");
        if (!Regex.IsMatch(pin.Name, "^linux-headers-[0-9.]+-[0-9]+$") || !pin.Url.StartsWith("https://github.com/sabotage-linux/kernel-headers/", StringComparison.Ordinal) ||
            !pin.Url.EndsWith("/" + pin.Name + ".tar.xz", StringComparison.Ordinal) || !Regex.IsMatch(pin.Sha256, "^[0-9a-f]{64}$"))
            throw new InvalidDataException("Unsupported Linux headers pin.");
        return pin;
    }

    /// <summary>
    /// The include directories of the Linux UAPI headers for an architecture: its asm directory, then the generic
    /// headers (linux/, asm-generic/).
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="pin">The pin.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>The directories.</returns>
    public static string[] KernelHeaderIncludes(string root, KernelHeadersPin pin, KernelArchitecture architecture)
    {
        var tree = Path.Combine(Directory(root), pin.Name);
        var arch = architecture.Triple.StartsWith("x86_64-", StringComparison.Ordinal) ? "x86" : "arm64";
        return [Path.Combine(tree, arch, "include"), Path.Combine(tree, "generic", "include")];
    }

    /// <summary>
    /// Downloads the pinned tarballs that are missing, verifies their SHA-256 and extracts them once each, through the
    /// system's tar (the tarballs are xz-compressed); a tree extracted from a tarball of another hash is replaced.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The directory holding the trees.</returns>
    public static async Task<string> PrepareAsync(string root)
    {
        var pin = await ReadPinAsync(root);
        var downloads = Path.Combine(root, ".tools", "downloads");
        System.IO.Directory.CreateDirectory(downloads);
        System.IO.Directory.CreateDirectory(Directory(root));
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        foreach (var tarball in pin.Tarballs)
        {
            var file = Path.Combine(downloads, $"{tarball.Name}-{pin.Version}.src.tar.xz");
            if (!File.Exists(file))
            {
                Console.WriteLine($"Downloading pinned {tarball.Name} {pin.Version}...");
                var bytes = await client.GetByteArrayAsync($"{REPOSITORY}/releases/download/{pin.Tag}/{tarball.Name}-{pin.Version}.src.tar.xz");
                var partial = file + ".partial";
                await File.WriteAllBytesAsync(partial, bytes);
                File.Move(partial, file, overwrite: true);
            }
            string digest;
            await using (var stream = File.OpenRead(file))
            {
                digest = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
            }
            if (digest != tarball.Sha256)
                throw new InvalidDataException($"{tarball.Name} tarball hash mismatch. Remove the invalid download: {file}");
            var tree = SourceDirectory(root, tarball.Name);
            var stamp = tree + ".verified-" + tarball.Sha256;
            if (File.Exists(stamp) && System.IO.Directory.Exists(tree))
                continue;
            if (System.IO.Directory.Exists(tree))
                System.IO.Directory.Delete(tree, recursive: true);
            await Processes.RequireSuccessAsync(Toolchain.Tar(), ["-xf", file, "-C", Directory(root)], root);
            if (!System.IO.Directory.Exists(Path.Combine(tree, "src")))
                throw new InvalidDataException($"The {tarball.Name} tarball does not hold {Path.GetFileName(tree)}.");
            await File.WriteAllTextAsync(stamp, tarball.Sha256 + "\n");
        }
        var headers = await ReadHeadersPinAsync(root);
        var headersFile = Path.Combine(downloads, headers.Name + ".tar.xz");
        if (!File.Exists(headersFile))
        {
            Console.WriteLine($"Downloading pinned {headers.Name}...");
            var bytes = await client.GetByteArrayAsync(headers.Url);
            await File.WriteAllBytesAsync(headersFile + ".partial", bytes);
            File.Move(headersFile + ".partial", headersFile, overwrite: true);
        }
        await using (var stream = File.OpenRead(headersFile))
        {
            if (Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant() != headers.Sha256)
                throw new InvalidDataException($"Linux headers tarball hash mismatch. Remove the invalid download: {headersFile}");
        }
        var headersTree = Path.Combine(Directory(root), headers.Name);
        if (!File.Exists(headersTree + ".verified-" + headers.Sha256) || !System.IO.Directory.Exists(headersTree))
        {
            if (System.IO.Directory.Exists(headersTree))
                System.IO.Directory.Delete(headersTree, recursive: true);
            // The per-architecture directories link to generic/include; only the real directories are extracted.
            await Processes.RequireSuccessAsync(Toolchain.Tar(), ["-xf", headersFile, "-C", Directory(root), headers.Name + "/generic/include",
                headers.Name + "/x86/include/asm", headers.Name + "/arm64/include/asm"], root);
            if (!File.Exists(Path.Combine(headersTree, "generic", "include", "linux", "futex.h")))
                throw new InvalidDataException($"The {headers.Name} tarball does not hold linux/futex.h.");
            await File.WriteAllTextAsync(headersTree + ".verified-" + headers.Sha256, headers.Sha256 + "\n");
        }
        foreach (var source in pin.Libc)
        {
            var file = Path.Combine(LibcDirectory(root), source.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(file))
            {
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                var bytes = await client.GetByteArrayAsync($"https://raw.githubusercontent.com/llvm/llvm-project/{pin.Tag}/{source.Path}");
                await File.WriteAllBytesAsync(file + ".partial", bytes);
                File.Move(file + ".partial", file, overwrite: true);
            }
            if (Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file))).ToLowerInvariant() != source.Sha256)
                throw new InvalidDataException($"LLVM libc file hash mismatch. Remove the invalid file: {file}");
        }
        return Directory(root);
    }

    /// <summary>
    /// Requires the trees that <c>setup</c> extracts.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="pin">The pin.</param>
    public static void RequireSources(string root, LlvmRuntimesPin pin)
    {
        foreach (var tarball in pin.Tarballs)
        {
            var tree = SourceDirectory(root, tarball.Name);
            if (!File.Exists(tree + ".verified-" + tarball.Sha256) || !System.IO.Directory.Exists(Path.Combine(tree, "src")))
                throw new InvalidOperationException($"Pinned {tarball.Name} {pin.Version} is missing. Run: dotnet run --project tools/WitOS.Dev -- setup");
        }
        foreach (var source in pin.Libc)
        {
            var file = Path.Combine(LibcDirectory(root), source.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(file) || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant() != source.Sha256)
                throw new InvalidOperationException($"Pinned LLVM libc header {source.Path} is missing or changed. Run: dotnet run --project tools/WitOS.Dev -- setup");
        }
    }

    /// <summary>
    /// Configures libc++'s <c>__config_site.in</c> as CMake's configure_file does with @ONLY: a
    /// <c>#cmakedefine01 NAME</c> becomes <c>#define NAME 0|1</c>, a <c>#cmakedefine NAME VALUE</c> becomes the
    /// definition when the variable is set and <c>/* #undef NAME */</c> otherwise, and <c>@NAME@</c> its value.
    /// </summary>
    /// <param name="template">The template.</param>
    /// <returns>The configured header.</returns>
    public static string ConfigureSite(string template)
    {
        var output = new StringBuilder();
        foreach (var line in template.Replace("\r\n", "\n").Split('\n'))
        {
            var match = CMAKEDEFINE.Match(line);
            if (!match.Success)
            {
                output.Append(VARIABLE.Replace(line, variable => CONFIG_SITE.TryGetValue(variable.Groups[1].Value, out var value) && value != null
                    ? value
                    : throw new InvalidDataException("Unknown libc++ configuration variable " + variable.Value))).Append('\n');
                continue;
            }
            var name = match.Groups[2].Value;
            if (!CONFIG_SITE.TryGetValue(name, out var setting))
                throw new InvalidDataException("Unknown libc++ configuration name " + name);
            if (match.Groups[1].Success)
            {
                if (setting is not ("0" or "1"))
                    throw new InvalidDataException($"libc++ configuration {name} must be 0 or 1.");
                output.Append($"#define {name} {setting}\n");
            }
            else if (setting == null)
                output.Append($"/* #undef {name} */\n");
            else
            {
                var value = match.Groups[3].Success ? VARIABLE.Replace(match.Groups[3].Value, variable => CONFIG_SITE[variable.Groups[1].Value] ?? "") : "";
                output.Append(value.Length == 0 ? $"#define {name}\n" : $"#define {name} {value}\n");
            }
        }
        return output.ToString().TrimEnd('\n') + "\n";
    }

    /// <summary>
    /// Builds libunwind.a, libc++abi.a and libc++.a of an architecture under artifacts/substrate/&lt;architecture&gt;,
    /// once per set of inputs (the pin, the options, the libc build and the compiler).
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>The build.</returns>
    public static Task<CxxBuild> BuildAsync(string root, KernelArchitecture architecture) => BuildVariantAsync(root, architecture, false);

    /// <summary>
    /// Builds libunwind.so.1, libc++abi.so.1 and libc++.so.1 of an architecture under
    /// artifacts/substrate/&lt;architecture&gt;/cxx-shared (plan step S5.4): the same sources and options as the static
    /// libraries, position-independent, each linked as a shared library with its soname against libc.so and the ones
    /// below it, as the runtimes' CMake links them for Linux (libc++ on libc++abi and libunwind, whose _Unwind_Resume
    /// its cleanups call, libc++abi on libunwind).
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>The build; its library paths are the shared libraries.</returns>
    public static async Task<CxxBuild> BuildSharedAsync(string root, KernelArchitecture architecture)
    {
        await BuildAsync(root, architecture); // the configured headers the shared build compiles against
        return await BuildVariantAsync(root, architecture, true);
    }

    private static async Task<CxxBuild> BuildVariantAsync(string root, KernelArchitecture architecture, bool shared)
    {
        var pin = await ReadPinAsync(root);
        RequireSources(root, pin);
        var headers = await ReadHeadersPinAsync(root);
        var kernelHeaders = KernelHeaderIncludes(root, headers, architecture);
        if (!File.Exists(Path.Combine(Directory(root), headers.Name + ".verified-" + headers.Sha256)))
            throw new InvalidOperationException($"Pinned {headers.Name} is missing. Run: dotnet run --project tools/WitOS.Dev -- setup");
        var libc = await MuslLibc.BuildAsync(root, architecture);
        var output = Path.Combine(root, "artifacts", "substrate", architecture.Name);
        var generated = Path.Combine(output, "libcxx-generated");
        var unwind = SourceDirectory(root, "libunwind");
        var cxxabi = SourceDirectory(root, "libcxxabi");
        var cxx = SourceDirectory(root, "libcxx");
        var libraries = shared ? Path.Combine(output, "cxx-shared") : output;
        System.IO.Directory.CreateDirectory(libraries);
        var build = shared
            ? new CxxBuild(Path.Combine(libraries, "libunwind.so.1"), Path.Combine(libraries, "libc++abi.so.1"), Path.Combine(libraries, "libc++.so.1"),
                [generated, Path.Combine(cxx, "include"), Path.Combine(cxxabi, "include"), Path.Combine(unwind, "include"), .. libc.Includes.Skip(1)])
            : new CxxBuild(Path.Combine(output, "libunwind.a"), Path.Combine(output, "libc++abi.a"), Path.Combine(output, "libc++.a"),
                [generated, Path.Combine(cxx, "include"), Path.Combine(cxxabi, "include"), Path.Combine(unwind, "include"), .. libc.Includes.Skip(1)]);
        string[] common =
        [
            $"--target={architecture.Triple}", .. architecture.ClangOptions, "-O2", "-nostdlibinc", shared ? "-fPIC" : "-fPIE", "-fno-plt",
            "-ffunction-sections", "-fdata-sections", "-funwind-tables", "-fstrict-aliasing", "-DNDEBUG", "-w",
            .. build.Includes.SelectMany(include => new[] { "-isystem", include }),
            .. kernelHeaders.SelectMany(include => new[] { "-isystem", include })
        ];
        string[] cxxCommon = [.. common, "-nostdinc++"];
        var unwindCxx = (string[])[.. cxxCommon, "-std=c++17", "-fno-exceptions", "-fno-rtti", "-D_LIBUNWIND_IS_NATIVE_ONLY"];
        var unwindC = (string[])[.. common, "-std=c99", "-fexceptions", "-D_LIBUNWIND_IS_NATIVE_ONLY"];
        var unwindAsm = (string[])[.. common, "-D_LIBUNWIND_IS_NATIVE_ONLY"];
        var cxxabiOptions = (string[])[.. cxxCommon, "-std=c++23", "-D_LIBCXXABI_BUILDING_LIBRARY", "-D_LIBCPP_BUILDING_LIBRARY",
            "-I", Path.Combine(cxx, "src")];
        var cxxOptions = (string[])[.. cxxCommon, "-std=c++23", "-D_LIBCPP_BUILDING_LIBRARY", "-D_LIBCPP_REMOVE_TRANSITIVE_INCLUDES",
            "-DLIBCXX_BUILDING_LIBCXXABI", "-fvisibility-inlines-hidden", "-fvisibility=hidden", "-faligned-allocation",
            "-fsized-deallocation", "-I", Path.Combine(cxx, "src"),
            // runtimes/cmake/Modules/FindLibcCommonUtils.cmake: the libc root and its namespace for the shared headers.
            "-I", Path.Combine(LibcDirectory(root), "libc"), "-DLIBC_NAMESPACE=__llvm_libc_common_utils"];

        var stamp = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
            [shared ? "shared" : "static", Toolchain.LLVM_VERSION, headers.Sha256, .. pin.Tarballs.Select(tarball => tarball.Sha256), .. pin.Libc.Select(source => source.Sha256), File.ReadAllText(Path.Combine(output, "stamp.txt")),
                .. unwindCxx, .. unwindC, .. unwindAsm, .. cxxabiOptions, .. cxxOptions, .. CXX_SOURCES, .. CXXABI_SOURCES,
                .. CONFIG_SITE.Select(entry => entry.Key + "=" + entry.Value)])))).ToLowerInvariant();
        var stampPath = Path.Combine(libraries, shared ? "stamp.txt" : "cxx-stamp.txt");
        if (File.Exists(build.Unwind) && File.Exists(build.CxxAbi) && File.Exists(build.Cxx) && File.Exists(stampPath) &&
            await File.ReadAllTextAsync(stampPath) == stamp)
            return build;
        File.Delete(stampPath);

        if (!shared)
        {
            if (System.IO.Directory.Exists(generated))
                System.IO.Directory.Delete(generated, recursive: true);
            System.IO.Directory.CreateDirectory(generated);
            await File.WriteAllTextAsync(Path.Combine(generated, "__config_site"),
                ConfigureSite(await File.ReadAllTextAsync(Path.Combine(cxx, "include", "__config_site.in"))));
            File.Copy(Path.Combine(cxx, "vendor", "llvm", "default_assertion_handler.in"), Path.Combine(generated, "__assertion_handler"));
        }

        var jobs = new List<(string Source, string[] Options, string Archive)>();
        jobs.AddRange(UNWIND_CXX_SOURCES.Select(file => (Path.Combine(unwind, "src", file), unwindCxx, build.Unwind)));
        jobs.AddRange(UNWIND_C_SOURCES.Select(file => (Path.Combine(unwind, "src", file), unwindC, build.Unwind)));
        jobs.AddRange(UNWIND_ASM_SOURCES.Select(file => (Path.Combine(unwind, "src", file), unwindAsm, build.Unwind)));
        jobs.AddRange(CXXABI_SOURCES.Select(file => (Path.Combine(cxxabi, "src", file), cxxabiOptions, build.CxxAbi)));
        jobs.AddRange(CXX_SOURCES.Select(file => (Path.Combine(cxx, "src", file), cxxOptions, build.Cxx)));
        var objects = Path.Combine(output, shared ? "cxx-shared-obj" : "cxx-obj");
        if (System.IO.Directory.Exists(objects))
            System.IO.Directory.Delete(objects, recursive: true);
        System.IO.Directory.CreateDirectory(objects);
        var clang = Toolchain.Clang(root);
        var produced = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            [build.Unwind] = [],
            [build.CxxAbi] = [],
            [build.Cxx] = []
        };
        await Parallel.ForEachAsync(jobs, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            async (job, _) =>
            {
                var name = Path.GetFileNameWithoutExtension(job.Archive) + "_" +
                    Path.GetRelativePath(Path.GetDirectoryName(Path.GetDirectoryName(job.Source)!)!, job.Source).Replace('\\', '_').Replace('/', '_') + ".o";
                var obj = Path.Combine(objects, name);
                await Processes.RequireSuccessAsync(clang, [.. job.Options, "-c", job.Source, "-o", obj], root);
                lock (produced)
                    produced[job.Archive].Add(obj);
            });
        if (shared)
        {
            // Each library on the ones below it, as the runtimes' CMake links them for Linux.
            List<string> Members(string library) => produced[library].OrderBy(path => path, StringComparer.Ordinal).ToList();
            await MuslLibc.LinkSharedLibraryAsync(root, architecture, libraries, Path.GetFileName(build.Unwind), Members(build.Unwind));
            await MuslLibc.LinkSharedLibraryAsync(root, architecture, libraries, Path.GetFileName(build.CxxAbi), Members(build.CxxAbi), [build.Unwind]);
            await MuslLibc.LinkSharedLibraryAsync(root, architecture, libraries, Path.GetFileName(build.Cxx), Members(build.Cxx), [build.CxxAbi, build.Unwind]);
        }
        foreach (var (archive, members) in produced.Where(_ => !shared))
        {
            File.Delete(archive);
            var list = archive + ".rsp";
            await File.WriteAllLinesAsync(list, members.OrderBy(path => path, StringComparer.Ordinal).Select(path => '"' + path.Replace('\\', '/') + '"'));
            await Processes.RequireSuccessAsync(Toolchain.LlvmAr(root), ["rcs", archive, "@" + list], root);
        }
        await File.WriteAllTextAsync(stampPath, stamp);
        Console.WriteLine($"LLVM runtimes {pin.Version} for {architecture.Triple}{(shared ? " (shared)" : "")}: libunwind {produced[build.Unwind].Count}, " +
            $"libc++abi {produced[build.CxxAbi].Count}, libc++ {produced[build.Cxx].Count} objects.");
        return build;
    }

    /// <summary>
    /// Compiles one C++ source of a program against libc++'s and the libc's headers, with exceptions and unwind tables.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <param name="source">The source.</param>
    /// <param name="obj">The object to write.</param>
    /// <param name="options">The caller's dialect and warning options.</param>
    public static async Task CompileAsync(string root, KernelArchitecture architecture, string source, string obj, IEnumerable<string> options)
    {
        var build = await BuildAsync(root, architecture);
        await Processes.RequireSuccessAsync(Toolchain.Clang(root),
        [
            $"--target={architecture.Triple}", "-O2", "-nostdlibinc", "-nostdinc++", "-fPIE", "-fno-plt", "-fno-stack-protector",
            "-funwind-tables", .. options, .. architecture.ClangOptions,
            .. build.Includes.SelectMany(include => new[] { "-isystem", include }), "-c", source, "-o", obj
        ], root);
    }

    /// <summary>
    /// Links a C++ program: crt1, its objects, libc++, libc++abi and libunwind, libc.a and the builtins.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <param name="output">Directory for the executable.</param>
    /// <param name="name">Program name.</param>
    /// <param name="objects">Object files.</param>
    /// <returns>Path of the executable.</returns>
    public static async Task<string> LinkAsync(string root, KernelArchitecture architecture, string output, string name, IEnumerable<string> objects)
    {
        var build = await BuildAsync(root, architecture);
        return await MuslLibc.LinkAsync(root, architecture, output, name, objects, [build.Cxx, build.CxxAbi, build.Unwind]);
    }

    /// <summary>
    /// Builds the root task of the cxx scenario (S4): tests/User/cxx_exceptions.cpp, unchanged in its Itanium form
    /// (its <c>__declspec</c> is accepted with -fdeclspec), and tests/User/cxx_main.cpp, which runs it, compares its
    /// trace with Linux's and checks libc++.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Artifact directory of the scenario.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>Path of the flat image.</returns>
    public static async Task<string> BuildRootAsync(string root, string output, KernelArchitecture architecture)
    {
        var scenario = Path.Combine(output, "cxx_exceptions.o");
        await CompileAsync(root, architecture, Path.Combine(root, "tests", "User", "cxx_exceptions.cpp"), scenario,
            ["-std=c++20", "-fdeclspec", "-w"]);
        var program = Path.Combine(output, "cxx_main.o");
        await CompileAsync(root, architecture, Path.Combine(root, "tests", "User", "cxx_main.cpp"), program,
            ["-std=c++20", "-Wall", "-Wextra", "-Werror"]);
        var image = await LinkAsync(root, architecture, output, "RootFixture", [scenario, program]);
        return await Images.FlatImage.FromElfAsync(output, architecture.ElfMachine, image, "RootFixture", "wit_user_root_image",
            "user_root_image.h");
    }

    #endregion
}

/// <summary>
/// The pin of the LLVM runtimes.
/// </summary>
/// <param name="Repository">llvm-project.</param>
/// <param name="Tag">The release tag.</param>
/// <param name="Version">The release.</param>
/// <param name="Purpose">Why the pin exists.</param>
/// <param name="Tarballs">libunwind, libcxxabi and libcxx in that order.</param>
/// <param name="LibcPurpose">Why the LLVM libc headers are pinned.</param>
/// <param name="Libc">The LLVM libc headers libc++ includes, file by file.</param>
internal sealed record LlvmRuntimesPin(string Repository, string Tag, string Version, string Purpose, LlvmRuntimesTarball[] Tarballs,
    string LibcPurpose, CompilerRtSource[] Libc);

/// <summary>
/// The pin of the sanitized Linux UAPI headers.
/// </summary>
/// <param name="Name">The tarball's top directory, linux-headers-&lt;version&gt;.</param>
/// <param name="Url">The release asset.</param>
/// <param name="Sha256">SHA-256 of the downloaded bytes.</param>
/// <param name="Purpose">Why the pin exists.</param>
internal sealed record KernelHeadersPin(string Name, string Url, string Sha256, string Purpose);

/// <summary>
/// One pinned source tarball.
/// </summary>
/// <param name="Name">The runtime.</param>
/// <param name="Sha256">SHA-256 of the downloaded bytes.</param>
internal sealed record LlvmRuntimesTarball(string Name, string Sha256);

/// <summary>
/// The products of a C++ runtime build.
/// </summary>
/// <param name="Unwind">libunwind.a.</param>
/// <param name="CxxAbi">libc++abi.a.</param>
/// <param name="Cxx">libc++.a.</param>
/// <param name="Includes">Include directories in search order: libc++'s generated and public headers, libc++abi's,
/// libunwind's, then the libc's.</param>
internal sealed record CxxBuild(string Unwind, string CxxAbi, string Cxx, IReadOnlyList<string> Includes);
