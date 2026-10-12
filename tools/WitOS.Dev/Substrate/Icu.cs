using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WitOS.Dev.Host;
using WitOS.Dev.Kernel;

namespace WitOS.Dev.Substrate;

/// <summary>
/// ICU4C in the system layer, from the release src/Substrate/icu.lock.json pins by the SHA-256 of its source tarball,
/// downloaded once into .tools/downloads and extracted under .tools/&lt;name&gt; by <c>setup</c>: its public headers in
/// the sysroot (plan step R1.2b), which .NET's globalization shim compiles against as on Linux, and its libraries
/// (plan step R4.3), the culture data RFC 0015 section 8 brings when a scenario needs it. The libraries are built after
/// ICU's own Makefile rules and options for Linux, never by its build system: each source of common/ and i18n/ that
/// their sources.txt lists, compiled unchanged by the pinned clang against the sysroot, linked as libicuuc.so.&lt;major&gt;
/// and libicui18n.so.&lt;major&gt;; libicudata.so.&lt;major&gt; carries the release's prebuilt little-endian data
/// (data/in/icudt&lt;major&gt;l.dat) under its entry point, as ICU's pkgdata links it from genccode's assembly. The
/// globalization shim loads them with dlopen from /usr/lib.
/// </summary>
internal static class Icu
{
    #region Constants

    /// <summary>
    /// The pin.
    /// </summary>
    public const string LOCK = "src/Substrate/icu.lock.json";

    /// <summary>
    /// Where the libraries are in the system layer: the dynamic linker's default path, as a Linux distribution installs
    /// them.
    /// </summary>
    public const string LIBRARY_DIRECTORY = "usr/lib";

    // The libraries the shim loads, whose headers the sysroot carries and whose sources the libraries are built from.
    private static readonly string[] LIBRARIES = ["common", "i18n"];

    // ICU's options for Linux (configure with runConfigureICU Linux: icudefs.mk and config/mh-linux): the C++ flags, the
    // definitions of every library and of a shared one.
    private static readonly string[] CXX_OPTIONS =
    [
        "-D_REENTRANT", "-DU_HAVE_ELF_H=1", "-DU_ALL_IMPLEMENTATION", "-DU_ATTRIBUTE_DEPRECATED=", "-O3", "-W", "-Wall", "-pedantic",
        "-Wpointer-arith", "-Wwrite-strings", "-Wno-long-long", "-std=c++17", "-DPIC", "-fPIC"
    ];

    #endregion

    #region Functions

    /// <summary>
    /// Reads and checks the pin.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The pin.</returns>
    public static async Task<IcuPin> ReadPinAsync(string root)
    {
        var pin = JsonSerializer.Deserialize<IcuPin>(await File.ReadAllTextAsync(Path.Combine(root, LOCK)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("Missing ICU pin.");
        var release = Regex.Match(pin.Name, "^icu4c-([0-9]+)\\.([0-9]+)$");
        if (!release.Success || !Regex.IsMatch(pin.Sha256, "^[0-9a-f]{64}$") || pin.Url !=
            $"https://github.com/unicode-org/icu/releases/download/release-{release.Groups[1].Value}.{release.Groups[2].Value}/{pin.Name}-sources.tgz")
            throw new InvalidDataException("Unsupported ICU pin.");
        return pin;
    }

    /// <summary>
    /// The major version of the pinned release, which names the libraries and the data.
    /// </summary>
    /// <param name="pin">The pin.</param>
    /// <returns>The major version.</returns>
    public static string Major(IcuPin pin) => pin.Name["icu4c-".Length..pin.Name.IndexOf('.')];

    /// <summary>
    /// The unicode/ directories of the pinned libraries, which the sysroot's usr/include/unicode joins.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="pin">The pin.</param>
    /// <returns>The directories.</returns>
    public static string[] HeaderDirectories(string root, IcuPin pin) =>
        LIBRARIES.Select(library => Path.Combine(SourceDirectory(root, pin), library, "unicode")).ToArray();

    /// <summary>
    /// Downloads the pinned source tarball when missing, verifies its SHA-256 and extracts the libraries' sources, their
    /// headers, the prebuilt data and the license once; a tree extracted from a tarball of another hash, or before the
    /// libraries were built (R4.3), is replaced.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The directory holding the tree.</returns>
    public static async Task<string> PrepareAsync(string root)
    {
        var pin = await ReadPinAsync(root);
        var tarball = await Toolchain.RequireDownloadAsync(Path.Combine(root, ".tools", "downloads", pin.Name + "-sources.tgz"),
            pin.Url, pin.Sha256);
        var tree = Directory(root, pin);
        if (File.Exists(Stamp(root, pin)) && System.IO.Directory.Exists(tree))
            return tree;
        if (System.IO.Directory.Exists(tree))
            System.IO.Directory.Delete(tree, recursive: true);
        foreach (var stale in System.IO.Directory.GetFiles(Path.GetDirectoryName(tree)!, Path.GetFileName(tree) + ".verified-*"))
            File.Delete(stale);
        System.IO.Directory.CreateDirectory(tree);
        await Processes.RequireSuccessAsync(Toolchain.Tar(),
            ["-xf", tarball, "-C", tree, "icu/LICENSE", .. LIBRARIES.Select(library => $"icu/source/{library}"), $"icu/source/data/in/{DataFile(pin)}"],
            root);
        if (HeaderDirectories(root, pin).Any(directory => !System.IO.Directory.Exists(directory) ||
                !System.IO.Directory.EnumerateFiles(directory, "*.h").Any()) || !File.Exists(DataPath(root, pin)))
            throw new InvalidDataException($"The {pin.Name} tarball does not hold the unicode/ headers of {string.Join(" and ", LIBRARIES)} or {DataFile(pin)}.");
        await File.WriteAllTextAsync(Stamp(root, pin), pin.Sha256 + "\n");
        return tree;
    }

    /// <summary>
    /// Requires the tree <c>setup</c> extracts.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="pin">The pin.</param>
    public static void Require(string root, IcuPin pin)
    {
        if (!File.Exists(Stamp(root, pin)) || HeaderDirectories(root, pin).Any(directory => !System.IO.Directory.Exists(directory)))
            throw new InvalidOperationException($"Pinned {pin.Name} is missing. Run: dotnet run --project tools/WitOS.Dev -- setup");
    }

    /// <summary>
    /// Builds ICU's libraries of an architecture under artifacts/icu/&lt;architecture&gt; (plan step R4.3), once for the
    /// pin, the options and the sysroot's libraries they link against.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>The libraries, each by its soname.</returns>
    public static async Task<IReadOnlyList<(string Name, string Path)>> BuildAsync(string root, KernelArchitecture architecture)
    {
        var pin = await ReadPinAsync(root);
        Require(root, pin);
        var sysroot = await Sysroot.BuildAsync(root, architecture);
        var major = Major(pin);
        var source = SourceDirectory(root, pin);
        var output = Path.Combine(root, "artifacts", "icu", architecture.Name);
        var driver = Sysroot.DriverOptions(root, architecture);
        var libraries = new (string Name, string Path)[]
        {
            ($"libicudata.so.{major}", Path.Combine(output, $"libicudata.so.{major}")),
            ($"libicuuc.so.{major}", Path.Combine(output, $"libicuuc.so.{major}")),
            ($"libicui18n.so.{major}", Path.Combine(output, $"libicui18n.so.{major}"))
        };
        var sources = LIBRARIES.ToDictionary(library => library,
            library => File.ReadAllLines(Path.Combine(source, library, "sources.txt")).Select(line => line.Trim()).Where(line => line.Length > 0).ToArray());
        string[] OptionsOf(string library) => library == "common"
            ? [.. driver, .. CXX_OPTIONS, "-I", Path.Combine(source, "common"), "-DDEFAULT_ICU_PLUGINS=\"/usr/lib/icu\"", "-DU_COMMON_IMPLEMENTATION"]
            : [.. driver, .. CXX_OPTIONS, "-I", Path.Combine(source, "i18n"), "-I", Path.Combine(source, "common"), "-DU_I18N_IMPLEMENTATION"];
        var lib = Path.Combine(sysroot, "usr", "lib");
        var stamp = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
        [
            pin.Sha256, Toolchain.LLVM_VERSION, .. OptionsOf("common"), .. OptionsOf("i18n"), .. sources.SelectMany(entry => entry.Value),
            .. new[] { "libc.so", "libc++.so.1", "libc++abi.so.1", "libunwind.so.1" }.Select(file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(lib, file)))))
        ])))).ToLowerInvariant();
        var stampPath = Path.Combine(output, "stamp.txt");
        if (libraries.All(library => File.Exists(library.Path)) && File.Exists(stampPath) && await File.ReadAllTextAsync(stampPath) == stamp)
            return libraries;
        if (System.IO.Directory.Exists(output))
            System.IO.Directory.Delete(output, recursive: true);
        System.IO.Directory.CreateDirectory(output);
        var clang = Toolchain.Clang(root);

        // libicudata: the release's data under ICU's entry point, as genccode writes its assembly for gcc and pkgdata
        // links it, without the default libraries.
        var data = Path.Combine(output, $"icudt{major}l_dat.S");
        var entry = $"icudt{major}_dat";
        await File.WriteAllTextAsync(data,
            $".globl {entry}\n\t.section .note.GNU-stack,\"\",%progbits\n\t.section .rodata\n\t.balign 16\n\t.type {entry},%object\n{entry}:\n\n" +
            $"\t.incbin \"{DataPath(root, pin).Replace('\\', '/')}\"\n\t.size {entry}, .-{entry}\n");
        var dataObject = Path.Combine(output, $"icudt{major}l_dat.o");
        await Processes.RequireSuccessAsync(clang, [.. driver, "-c", data, "-o", dataObject], root);
        await Processes.RequireSuccessAsync(clang,
            [.. driver, "-shared", "-nodefaultlibs", "-nostdlib", $"-Wl,-soname,{libraries[0].Name}", "-o", libraries[0].Path, dataObject], root);

        // libicuuc on libicudata, libicui18n on both, each with the threads and math libraries, as ICU's Makefiles link them;
        // a symbol no library defines fails the link, since musl's dlopen with RTLD_LAZY would leave its calls unbound.
        var objects = new Dictionary<string, List<string>>(StringComparer.Ordinal) { ["common"] = [], ["i18n"] = [] };
        var jobs = sources.SelectMany(entry => entry.Value.Select(file => (Library: entry.Key, File: file))).ToList();
        await Parallel.ForEachAsync(jobs, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, async (job, _) =>
        {
            var directory = Path.Combine(output, job.Library);
            System.IO.Directory.CreateDirectory(directory);
            var obj = Path.Combine(directory, Path.ChangeExtension(job.File, ".o"));
            await Processes.RequireSuccessAsync(clang, [.. OptionsOf(job.Library), "-c", Path.Combine(source, job.Library, job.File), "-o", obj], root);
            lock (objects)
                objects[job.Library].Add(obj);
        });
        async Task LinkAsync((string Name, string Path) library, string from, IEnumerable<string> needed)
        {
            var list = library.Path + ".rsp";
            await File.WriteAllLinesAsync(list, objects[from].Order(StringComparer.Ordinal).Select(path => '"' + path.Replace('\\', '/') + '"'));
            await Processes.RequireSuccessAsync(clang,
            [
                "--driver-mode=g++", .. driver, "-O3", "-shared", "-Wl,-Bsymbolic", "-Wl,--no-undefined", $"-Wl,-soname,{library.Name}", "-o", library.Path,
                "@" + list, .. needed, "-lpthread", "-lm"
            ], root);
        }
        await LinkAsync(libraries[1], "common", [libraries[0].Path]);
        await LinkAsync(libraries[2], "i18n", [libraries[1].Path, libraries[0].Path]);
        await File.WriteAllTextAsync(stampPath, stamp);
        Console.WriteLine($"ICU {pin.Name["icu4c-".Length..]} for {architecture.Triple}: libicuuc {objects["common"].Count}, libicui18n " +
            $"{objects["i18n"].Count} objects, libicudata {new FileInfo(DataPath(root, pin)).Length} bytes of data.");
        return libraries;
    }

    /// <summary>
    /// The libraries' files in the system layer's package, under /usr/lib.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>Package paths and the files to place there.</returns>
    public static async Task<IReadOnlyList<(string Name, string Source)>> PackageFilesAsync(string root, KernelArchitecture architecture) =>
        [.. (await BuildAsync(root, architecture)).Select(library => ($"{LIBRARY_DIRECTORY}/{library.Name}", library.Path))];

    #endregion

    #region Tools

    private static string Directory(string root, IcuPin pin) => Path.Combine(root, ".tools", pin.Name);

    private static string SourceDirectory(string root, IcuPin pin) => Path.Combine(Directory(root, pin), "icu", "source");

    private static string DataFile(IcuPin pin) => $"icudt{Major(pin)}l.dat";

    private static string DataPath(string root, IcuPin pin) => Path.Combine(SourceDirectory(root, pin), "data", "in", DataFile(pin));

    // The extracted tree's mark: the tarball's hash, and that the libraries' sources and data came with the headers.
    private static string Stamp(string root, IcuPin pin) => Directory(root, pin) + ".sources-" + pin.Sha256;

    #endregion
}

/// <summary>
/// The pinned ICU4C release whose headers the sysroot carries and whose libraries the system layer has.
/// </summary>
/// <param name="Name">The release, icu4c-&lt;major&gt;.&lt;minor&gt;.</param>
/// <param name="Url">The source tarball of the release.</param>
/// <param name="Sha256">The SHA-256 of the tarball.</param>
/// <param name="Purpose">Why the system layer carries it.</param>
internal sealed record IcuPin(string Name, string Url, string Sha256, string Purpose);
