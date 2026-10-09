using WitOS.Dev.Host;
using WitOS.Dev.Kernel;

namespace WitOS.Dev.Substrate;

/// <summary>
/// The system layer's sysroot (plan step R1.2a): layer 2's C library, C++ runtime and WitOS headers in the layout a
/// Linux rootfs has, so that clang's own driver and the upstream builds that cross-compile against a rootfs (ROOTFS_DIR)
/// build WitOS programs and libraries as they build Linux musl ones. Step R1.2b adds ICU's headers and CMake's platform
/// modules for the system WitOS. Everything in it comes from the substrate's own builds and pins; nothing is a system
/// copy.
/// </summary>
internal static class Sysroot
{
    #region Constants

    /// <summary>
    /// The marker a build recognizes the sysroot by, as FreeBSD's rootfs is recognized by bin/freebsd-version.
    /// </summary>
    public const string MARKER = "etc/witos-release";

    // The libraries musl installs empty, since their functions are in libc (musl's Makefile, EMPTY_LIB_NAMES).
    private static readonly string[] EMPTY_LIBRARIES = ["m", "rt", "pthread", "crypt", "util", "xnet", "resolv", "dl"];

    #endregion

    #region Functions

    /// <summary>
    /// The sysroot of an architecture.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>Directory path.</returns>
    public static string Directory(string root, KernelArchitecture architecture) =>
        Path.Combine(root, "artifacts", "sysroot", architecture.Name);

    /// <summary>
    /// The compiler resource directory of the sysroot: clang's own headers and the compiler-rt objects the driver links
    /// for the triple (libclang_rt.builtins.a, clang_rt.crtbegin.o, clang_rt.crtend.o).
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>Directory path.</returns>
    public static string ResourceDirectory(string root, KernelArchitecture architecture) =>
        Path.Combine(Directory(root, architecture), "usr", "lib", "clang", Toolchain.LLVM_VERSION.Split('.')[0]);

    /// <summary>
    /// The clang driver options that build for WitOS against the sysroot: the layer-2 triple, the sysroot and its
    /// resource directory, compiler-rt, libunwind and libc++, lld, and the layout every image another component maps
    /// needs (4 KiB pages, separate loadable segments: S5.3).
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>The options.</returns>
    public static string[] DriverOptions(string root, KernelArchitecture architecture) =>
    [
        $"--target={architecture.Triple}", $"--sysroot={Directory(root, architecture)}", "-stdlib=libc++", "-fuse-ld=lld",
        .. LinkOptions(root, architecture)
    ];

    /// <summary>
    /// The driver options of DriverOptions that a build with its own choice of target, sysroot, linker and C++ library
    /// still needs (R2.3b: the SDK's NativeAOT targets): the sysroot's resource directory, compiler-rt, libunwind, the
    /// layout every image another component maps needs, and the architecture's options.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>The options.</returns>
    public static string[] LinkOptions(string root, KernelArchitecture architecture) =>
    [
        $"-resource-dir={ResourceDirectory(root, architecture)}", "--rtlib=compiler-rt", "--unwindlib=libunwind",
        "-Wl,-z,max-page-size=4096", "-Wl,-z,separate-loadable-segments", .. architecture.ClangOptions
    ];

    /// <summary>
    /// Builds the substrate and lays the sysroot out under artifacts/sysroot/&lt;architecture&gt;, anew on every call.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>The sysroot.</returns>
    public static async Task<string> BuildAsync(string root, KernelArchitecture architecture)
    {
        var libc = await MuslLibc.BuildAsync(root, architecture);
        var shared = await MuslLibc.BuildSharedAsync(root, architecture);
        var cxx = await LlvmRuntimes.BuildAsync(root, architecture);
        var cxxShared = await LlvmRuntimes.BuildSharedAsync(root, architecture);
        var libwitos = await LibWitos.BuildAsync(root, architecture);
        var libcIncludes = libc.Includes.Skip(1).ToArray();
        var builtins = await CompilerRtBuiltins.BuildAsync(root, architecture, libcIncludes);
        var crtBegin = await CompilerRtBuiltins.BuildCrtBeginAsync(root, architecture, libcIncludes);
        var crtEnd = await CompilerRtBuiltins.BuildCrtEndAsync(root, architecture, libcIncludes);

        var sysroot = Directory(root, architecture);
        if (System.IO.Directory.Exists(sysroot))
            System.IO.Directory.Delete(sysroot, recursive: true);
        var include = Path.Combine(sysroot, "usr", "include");
        var lib = Path.Combine(sysroot, "usr", "lib");
        System.IO.Directory.CreateDirectory(lib);

        // musl's headers as its install-headers lays them out: include, the generic bits, the architecture's bits over
        // them and the generated alltypes.h and syscall.h.
        var musl = MuslLibc.SourceDirectory(root);
        var arch = MuslLibc.MuslArchitecture(architecture);
        CopyTree(Path.Combine(musl, "include"), include);
        CopyFiles(Path.Combine(musl, "arch", "generic", "bits"), Path.Combine(include, "bits"), "*.h");
        CopyFiles(Path.Combine(musl, "arch", arch, "bits"), Path.Combine(include, "bits"), "*.h");
        CopyFiles(Path.Combine(libc.Includes[1], "bits"), Path.Combine(include, "bits"), "*.h");
        // The headers WitOS patches (R1.3: sys/membarrier.h's C linkage), from the build's generated headers over musl's
        // own.
        foreach (var header in MuslLibc.PatchedHeaders())
            File.Copy(Path.Combine(libc.Includes[1], header), Path.Combine(include, header), overwrite: true);
        // WitOS's own headers: the system layer's (libwitos, the start and manager protocols) and the kernel's ABI-1
        // headers they include.
        CopyTree(Path.Combine(root, "src", "Sysroot", "include", "witos"), Path.Combine(include, "witos"));
        CopyTree(Path.Combine(root, "src", "Kernel", "include", "witos"), Path.Combine(include, "witos"));
        // The C++ runtime's headers as its install lays them out: libunwind's in include, libc++'s and libc++abi's in
        // include/c++/v1 with the configured __config_site and __assertion_handler.
        var cxxInclude = Path.Combine(include, "c++", "v1");
        CopyFiles(cxx.Includes[3], include, "*.h");
        CopyTree(cxx.Includes[1], cxxInclude);
        CopyFiles(cxx.Includes[2], cxxInclude, "*.h");
        CopyFiles(cxx.Includes[0], cxxInclude, "*");
        // ICU's headers (R1.2b), which System.Globalization.Native compiles against: no ICU library is there, and the
        // shim, which loads ICU at run time, reports its absence.
        var icu = await IcuHeaders.ReadPinAsync(root);
        IcuHeaders.Require(root, icu);
        foreach (var directory in IcuHeaders.HeaderDirectories(root, icu))
            CopyFiles(directory, Path.Combine(include, "unicode"), "*.h");

        // The startup objects of WitOS programs (no crt1.o: a program another component starts is position-independent,
        // S5.2), crti.o and crtn.o, which the driver links around every image, and the libraries.
        File.Copy(shared.Scrt1, Path.Combine(lib, "Scrt1.o"));
        File.Copy(libc.Rcrt1, Path.Combine(lib, "rcrt1.o"));
        foreach (var crt in new[] { "crti", "crtn" })
            await Processes.RequireSuccessAsync(Toolchain.Clang(root),
                [$"--target={architecture.Triple}", .. architecture.ClangOptions, "-c", Path.Combine(musl, "crt", arch, crt + ".s"),
                    "-o", Path.Combine(lib, crt + ".o")], root);
        File.Copy(libc.Library, Path.Combine(lib, "libc.a"));
        File.Copy(shared.Library, Path.Combine(lib, "libc.so"));
        foreach (var name in EMPTY_LIBRARIES)
            await Processes.RequireSuccessAsync(Toolchain.LlvmAr(root), ["rcs", Path.Combine(lib, $"lib{name}.a")], root);
        foreach (var archive in new[] { cxx.Cxx, cxx.CxxAbi, cxx.Unwind, libwitos })
            File.Copy(archive, Path.Combine(lib, Path.GetFileName(archive)));
        // A shared C++ runtime library is its versioned file; the unversioned name the driver links is a linker script
        // that names it, as a symbolic link would. libc++'s also brings libc++abi, as LLVM's own install writes it
        // (LIBCXX_ENABLE_ABI_LINKER_SCRIPT): the driver links -lc++ alone.
        foreach (var library in new[] { cxxShared.Cxx, cxxShared.CxxAbi, cxxShared.Unwind })
        {
            var name = Path.GetFileName(library);
            File.Copy(library, Path.Combine(lib, name));
            var abi = library == cxxShared.Cxx ? " -lc++abi" : "";
            await File.WriteAllTextAsync(Path.Combine(lib, name[..name.IndexOf(".so", StringComparison.Ordinal)] + ".so"),
                $"INPUT({name}{abi})\n");
        }

        // The resource directory: clang's headers and compiler-rt for the triple.
        var resource = ResourceDirectory(root, architecture);
        CopyTree(Path.Combine(Toolchain.ClangDirectory(root), "lib", "clang", Toolchain.LLVM_VERSION.Split('.')[0], "include"),
            Path.Combine(resource, "include"));
        var runtime = Path.Combine(resource, "lib", architecture.Triple);
        System.IO.Directory.CreateDirectory(runtime);
        File.Copy(builtins, Path.Combine(runtime, "libclang_rt.builtins.a"));
        File.Copy(crtBegin, Path.Combine(runtime, "clang_rt.crtbegin.o"));
        File.Copy(crtEnd, Path.Combine(runtime, "clang_rt.crtend.o"));

        // CMake's platform modules for the system WitOS (R1.2b), which a cross build's toolchain file adds to its module
        // path: Linux's rules for ELF images, with CMAKE_SYSTEM_NAME WitOS.
        CopyTree(Path.Combine(root, "src", "Sysroot", "cmake"), Path.Combine(sysroot, "usr", "share", "cmake"));

        var marker = Path.Combine(sysroot, MARKER);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        await File.WriteAllTextAsync(marker,
            $"WitOS system layer: musl {MuslLibc.VERSION}, LLVM {Toolchain.LLVM_VERSION}, {architecture.Triple}\n");
        Console.WriteLine($"Sysroot for {architecture.Triple}: {sysroot}");
        return sysroot;
    }

    /// <summary>
    /// The programs the sysroot scenario's package carries: tests/User/sysroot_init.cpp, which clang's driver compiles and
    /// links against the sysroot, as /bin/init, musl's libc.so as its dynamic linker and the shared C++ runtime in /lib.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Artifact directory of the scenario.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>Package paths and the files to place there.</returns>
    public static async Task<IReadOnlyList<(string Name, string Source)>> BuildScenarioProgramsAsync(string root, string output,
        KernelArchitecture architecture)
    {
        var sysroot = await BuildAsync(root, architecture);
        var program = Path.Combine(output, "sysroot_init.elf");
        await Processes.RequireSuccessAsync(Toolchain.Clang(root),
        [
            "--driver-mode=g++", .. DriverOptions(root, architecture), "-std=c++20", "-O2", "-Wall", "-Wextra", "-Werror",
            Path.Combine(root, "tests", "User", "sysroot_init.cpp"), "-o", program
        ], root);
        var lib = Path.Combine(sysroot, "usr", "lib");
        return
        [
            ("bin/init", program), (MuslLibc.InterpreterPath(architecture).TrimStart('/'), Path.Combine(lib, "libc.so")),
            ("lib/libc++.so.1", Path.Combine(lib, "libc++.so.1")), ("lib/libc++abi.so.1", Path.Combine(lib, "libc++abi.so.1")),
            ("lib/libunwind.so.1", Path.Combine(lib, "libunwind.so.1"))
        ];
    }

    #endregion

    #region Tools

    private static void CopyTree(string source, string destination)
    {
        foreach (var file in System.IO.Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (name == "CMakeLists.txt" || name.EndsWith(".in", StringComparison.Ordinal))
                continue;
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void CopyFiles(string source, string destination, string pattern)
    {
        System.IO.Directory.CreateDirectory(destination);
        foreach (var file in System.IO.Directory.EnumerateFiles(source, pattern, SearchOption.TopDirectoryOnly))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
    }

    #endregion
}
