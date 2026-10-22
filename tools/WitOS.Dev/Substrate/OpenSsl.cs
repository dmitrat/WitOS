using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WitOS.Dev.Host;
using WitOS.Dev.Kernel;

namespace WitOS.Dev.Substrate;

/// <summary>
/// OpenSSL in the system layer (plan step W3.1, RFC 0015 section 8, W0 decision 4), from the release
/// src/Substrate/openssl.lock.json pins by the SHA-256 of its source tarball, downloaded once into .tools/downloads and
/// extracted under .tools/&lt;name&gt; by <c>setup</c>. The unix flavour of System.Security.Cryptography and
/// System.Net.Security that upstream .NET builds for witos reaches libssl.so.3 and libcrypto.so.3 through the shim
/// System.Security.Cryptography.Native.OpenSsl, which the portable build loads with dlopen at run time, so the libraries
/// live in the install's /usr/lib beside ICU's and the shim compiles against the headers of this build. Unlike ICU's
/// sources, OpenSSL's are not a list the tool can compile: its public headers and its DER tables are perl templates its
/// own Configure and make expand at build time, so the tool drives that build with the pinned clang against the sysroot
/// on the Linux host, where the .NET install is built (runtime-witos), with the release's own options and nothing of
/// its sources changed.
/// </summary>
internal static class OpenSsl
{
    #region Constants

    /// <summary>
    /// The pin.
    /// </summary>
    public const string LOCK = "src/Substrate/openssl.lock.json";

    /// <summary>
    /// The license the pin names, which the tarball's LICENSE.txt states.
    /// </summary>
    public const string LICENSE = "Apache-2.0";

    /// <summary>
    /// Where the libraries are in the system layer: the dynamic linker's default path, as a Linux distribution installs
    /// them, where the shim's dlopen looks.
    /// </summary>
    public const string LIBRARY_DIRECTORY = "usr/lib";

    /// <summary>
    /// The shared library version of the 3.x series: libcrypto.so.3 and libssl.so.3, the names the shim opens.
    /// </summary>
    public const string SHARED_LIBRARY_VERSION = "3";

    // The release's own configuration, the same on every ISA: shared libraries; the C implementations of every algorithm,
    // since perlasm's assembly is a second source of each and TCG judges the C; no tests, documentation, programs or
    // loadable modules (the default provider is built in, the legacy and FIPS ones are not wanted); no secure memory and
    // no AF_ALG engine, whose sources include the Linux kernel's own headers (linux/mman.h, linux/version.h), which the
    // sysroot does not carry (the sanitized kernel headers serve the C++ runtimes' build alone); /usr and /etc/ssl as
    // the prefix and the configuration directory, so that OpenSSL's default certificate directory is /etc/ssl/certs,
    // where .NET's root store reads too.
    private static readonly string[] OPTIONS =
    [
        "shared", "no-asm", "no-tests", "no-docs", "no-apps", "no-module", "no-secure-memory", "no-afalgeng",
        "--prefix=/usr", "--openssldir=/etc/ssl", "--libdir=lib"
    ];

    #endregion

    #region Functions

    /// <summary>
    /// Reads and checks the pin.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The pin.</returns>
    public static async Task<OpenSslPin> ReadPinAsync(string root)
    {
        var pin = JsonSerializer.Deserialize<OpenSslPin>(await File.ReadAllTextAsync(Path.Combine(root, LOCK)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("Missing OpenSSL pin.");
        var release = Regex.Match(pin.Name, "^openssl-(3)\\.([0-9]+)\\.([0-9]+)$");
        if (!release.Success || !Regex.IsMatch(pin.Sha256, "^[0-9a-f]{64}$") || pin.License != LICENSE ||
            pin.Url != $"https://github.com/openssl/openssl/releases/download/{pin.Name}/{pin.Name}.tar.gz")
            throw new InvalidDataException("Unsupported OpenSSL pin.");
        return pin;
    }

    /// <summary>
    /// The sonames of the pinned release's libraries, libcrypto before libssl, which needs it.
    /// </summary>
    public static string[] Sonames => [$"libcrypto.so.{SHARED_LIBRARY_VERSION}", $"libssl.so.{SHARED_LIBRARY_VERSION}"];

    /// <summary>
    /// Downloads the pinned source tarball when missing, verifies its SHA-256 and extracts the release once; a tree
    /// extracted from a tarball of another hash is replaced.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The directory holding the release's sources.</returns>
    public static async Task<string> PrepareAsync(string root)
    {
        var pin = await ReadPinAsync(root);
        var tarball = await Toolchain.RequireDownloadAsync(Path.Combine(root, ".tools", "downloads", pin.Name + ".tar.gz"), pin.Url, pin.Sha256);
        var tree = SourceDirectory(root, pin);
        if (File.Exists(Stamp(root, pin)) && Directory.Exists(tree))
            return tree;
        if (Directory.Exists(tree))
            Directory.Delete(tree, recursive: true);
        foreach (var stale in Directory.GetFiles(Path.GetDirectoryName(tree)!, Path.GetFileName(tree) + ".sources-*"))
            File.Delete(stale);
        Directory.CreateDirectory(tree);
        await Processes.RequireSuccessAsync(Toolchain.Tar(), ["-xzf", tarball, "-C", tree, "--strip-components=1"], root);
        if (!File.Exists(Path.Combine(tree, "Configure")) || !File.Exists(Path.Combine(tree, "LICENSE.txt")) ||
            !Directory.Exists(Path.Combine(tree, "include", "openssl")))
            throw new InvalidDataException($"The {pin.Name} tarball does not hold Configure, LICENSE.txt and include/openssl.");
        if (!(await File.ReadAllTextAsync(Path.Combine(tree, "LICENSE.txt"))).Contains("Apache License", StringComparison.Ordinal))
            throw new InvalidDataException($"LICENSE.txt of {pin.Name} does not state the license the pin names ({LICENSE}).");
        await File.WriteAllTextAsync(Stamp(root, pin), pin.Sha256 + "\n");
        return tree;
    }

    /// <summary>
    /// Requires the tree <c>setup</c> extracts.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="pin">The pin.</param>
    public static void Require(string root, OpenSslPin pin)
    {
        if (!File.Exists(Stamp(root, pin)) || !File.Exists(Path.Combine(SourceDirectory(root, pin), "Configure")))
            throw new InvalidOperationException($"Pinned {pin.Name} is missing. Run: dotnet run --project tools/WitOS.Dev -- setup");
    }

    /// <summary>
    /// The release's configuration target of an architecture: its Linux target, whose compiler and linker are the
    /// pinned clang and lld for the layer-2 triple.
    /// </summary>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>The Configure target.</returns>
    public static string ConfigureTarget(KernelArchitecture architecture) => architecture.Name switch
    {
        "x64" => "linux-x86_64",
        "arm64" => "linux-aarch64",
        "riscv64" => "linux64-riscv64",
        _ => throw new NotSupportedException($"No OpenSSL target for {architecture.Name}.")
    };

    /// <summary>
    /// Builds the libraries of an architecture under artifacts/openssl/&lt;architecture&gt; (plan step W3.1), once for the
    /// pin, the options, the toolchain and the sysroot's libc they link against: the release's Configure and make,
    /// driven with the pinned clang, then its own install of the headers and the libraries into a staging directory.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>The build: the headers' directory, the libraries' directory and the libraries by soname.</returns>
    public static async Task<OpenSslBuild> BuildAsync(string root, KernelArchitecture architecture)
    {
        if (OperatingSystem.IsWindows())
            throw new InvalidOperationException("OpenSSL is built on the Linux host, where the .NET install is built (runtime-witos): its headers are perl templates.");
        var pin = await ReadPinAsync(root);
        Require(root, pin);
        var sysroot = await Sysroot.BuildAsync(root, architecture);
        var source = SourceDirectory(root, pin);
        var output = Path.Combine(root, "artifacts", "openssl", architecture.Name);
        var build = Path.Combine(output, "build");
        var install = Path.Combine(output, "install");
        var include = Path.Combine(install, "usr", "include");
        var lib = Path.Combine(install, "usr", "lib");
        var target = ConfigureTarget(architecture);
        var cflags = string.Join(' ', [$"--target={architecture.Triple}", $"--sysroot={sysroot}", .. architecture.ClangOptions, "-O2"]);
        var ldflags = string.Join(' ', [$"--target={architecture.Triple}", $"--sysroot={sysroot}", "-fuse-ld=lld", .. Sysroot.LinkOptions(root, architecture)]);
        var libraries = Sonames.Select(name => (Name: name, Path: Path.Combine(lib, name))).ToArray();
        var sysrootLib = Path.Combine(sysroot, "usr", "lib");
        var stamp = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
        [
            pin.Sha256, Toolchain.LLVM_VERSION, target, .. OPTIONS, cflags, ldflags,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(sysrootLib, "libc.so"))))
        ])))).ToLowerInvariant();
        var stampPath = Path.Combine(output, "stamp.txt");
        if (libraries.All(library => File.Exists(library.Path)) && Directory.Exists(Path.Combine(include, "openssl")) &&
            File.Exists(stampPath) && await File.ReadAllTextAsync(stampPath) == stamp)
            return new OpenSslBuild(include, lib, libraries);
        if (Directory.Exists(output))
            Directory.Delete(output, recursive: true);
        Directory.CreateDirectory(build);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CC"] = Toolchain.Clang(root),
            ["CFLAGS"] = cflags,
            ["LDFLAGS"] = ldflags
        };
        // An out-of-tree build: Configure run from the build directory leaves the extracted sources untouched.
        await RequireAsync("perl", [Path.Combine(source, "Configure"), target, .. OPTIONS], build, 600, environment, "Configure");
        await RequireAsync("make", [$"-j{Environment.ProcessorCount}"], build, 3600, environment, "make");
        await RequireAsync("make", [$"DESTDIR={install}", "install_dev", "install_runtime_libs"], build, 600, environment, "make install");
        foreach (var library in libraries)
            if (!File.Exists(library.Path))
                throw new InvalidDataException($"OpenSSL's build for {architecture.Triple} left no {library.Name} in {lib}.");
        var headers = Directory.GetFiles(Path.Combine(include, "openssl"), "*.h").Length;
        await File.WriteAllTextAsync(stampPath, stamp);
        Console.WriteLine($"OpenSSL {pin.Name["openssl-".Length..]} for {architecture.Triple}: {target}, {headers} headers, " +
            string.Join(", ", libraries.Select(library => $"{library.Name} {new FileInfo(library.Path).Length} bytes")) + ".");
        return new OpenSslBuild(include, lib, libraries);
    }

    /// <summary>
    /// The libraries' files in the system layer's package, under /usr/lib.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>Package paths and the files to place there.</returns>
    public static async Task<IReadOnlyList<(string Name, string Source)>> PackageFilesAsync(string root, KernelArchitecture architecture) =>
        [.. (await BuildAsync(root, architecture)).Libraries.Select(library => ($"{LIBRARY_DIRECTORY}/{library.Name}", library.Path))];

    /// <summary>
    /// The CMake variables that point upstream's FindOpenSSL at this build (the shim compiles against the headers and
    /// links neither library: the portable build opens them at run time), each as an argument of build.sh.
    /// </summary>
    /// <param name="build">The build.</param>
    /// <returns>The arguments.</returns>
    public static string[] CMakeArguments(OpenSslBuild build) =>
    [
        "-cmakeargs", $"-DOPENSSL_INCLUDE_DIR={build.Include}",
        "-cmakeargs", $"-DOPENSSL_CRYPTO_LIBRARY={build.Libraries[0].Path}",
        "-cmakeargs", $"-DOPENSSL_SSL_LIBRARY={build.Libraries[1].Path}"
    ];

    #endregion

    #region Tools

    private static async Task RequireAsync(string executable, string[] arguments, string directory, int timeout,
        IReadOnlyDictionary<string, string> environment, string what)
    {
        var result = await Processes.RunAsync(executable, arguments, directory, timeout, environment);
        if (result.TimedOut || result.ExitCode != 0)
            throw new InvalidOperationException($"OpenSSL's {what} failed (exit {result.ExitCode}, timeout={result.TimedOut}).\n" +
                $"{Tail(result.Output)}\n{Tail(result.Error)}");
    }

    private static string Tail(string text)
    {
        var lines = text.Split('\n');
        return string.Join('\n', lines.Skip(Math.Max(0, lines.Length - 40)));
    }

    private static string SourceDirectory(string root, OpenSslPin pin) => Path.Combine(root, ".tools", pin.Name);

    // The extracted tree's mark: the tarball's hash.
    private static string Stamp(string root, OpenSslPin pin) => SourceDirectory(root, pin) + ".sources-" + pin.Sha256;

    #endregion
}

/// <summary>
/// The pinned OpenSSL release the system layer builds for the .NET install.
/// </summary>
/// <param name="Name">The release, openssl-&lt;major&gt;.&lt;minor&gt;.&lt;patch&gt;.</param>
/// <param name="Url">The source tarball of the release.</param>
/// <param name="Sha256">The SHA-256 of the tarball.</param>
/// <param name="License">The license, which the tarball's LICENSE.txt states.</param>
/// <param name="Purpose">Why the system layer carries it.</param>
internal sealed record OpenSslPin(string Name, string Url, string Sha256, string License, string Purpose);

/// <summary>
/// A build of the pinned OpenSSL for one architecture.
/// </summary>
/// <param name="Include">The headers' directory, holding openssl/.</param>
/// <param name="Lib">The libraries' directory.</param>
/// <param name="Libraries">The libraries by soname, libcrypto before libssl.</param>
internal sealed record OpenSslBuild(string Include, string Lib, IReadOnlyList<(string Name, string Path)> Libraries);
