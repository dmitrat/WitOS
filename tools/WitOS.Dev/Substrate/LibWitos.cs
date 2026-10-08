using WitOS.Dev.Host;
using WitOS.Dev.Images;
using WitOS.Dev.Kernel;
using WitOS.Dev.NativeAot;

namespace WitOS.Dev.Substrate;

/// <summary>
/// libwitos (plan step S5.2): WitOS's own library of layer 2 beside the libc, starting with the static ELF loader of
/// the root task (src/Substrate/libwitos, witos/spawn.h), built by the pinned clang against the libc's headers, the
/// sysroot and the kernel's ABI headers into libwitos.a; and the spawn scenario, whose root task starts the
/// programs of its boot package with it.
/// </summary>
internal static class LibWitos
{
    #region Constants

    /// <summary>
    /// The library's sources.
    /// </summary>
    public const string SOURCES = "src/Substrate/libwitos";

    /// <summary>
    /// The package path of the program the spawn scenario starts.
    /// </summary>
    public const string CHILD_PATH = "bin/child";

    #endregion

    #region Functions

    /// <summary>
    /// Builds libwitos.a of an architecture under artifacts/substrate/&lt;architecture&gt;/libwitos.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>Path of the archive.</returns>
    public static async Task<string> BuildAsync(string root, KernelArchitecture architecture)
    {
        var output = Path.Combine(root, "artifacts", "substrate", architecture.Name, "libwitos");
        if (Directory.Exists(output))
            Directory.Delete(output, recursive: true);
        Directory.CreateDirectory(output);
        var objects = new List<string>();
        foreach (var source in Directory.GetFiles(Path.Combine(root, SOURCES), "*.c").Order(StringComparer.Ordinal))
        {
            var obj = Path.Combine(output, Path.GetFileNameWithoutExtension(source) + ".o");
            await MuslLibc.CompileAsync(root, architecture, source, obj, Options(root));
            objects.Add(obj);
        }
        var library = Path.Combine(output, "libwitos.a");
        await Processes.RequireSuccessAsync(Toolchain.LlvmAr(root), ["rcs", library, .. objects], root);
        return library;
    }

    /// <summary>
    /// The options of a C source that sees WitOS's headers: C11, strict warnings, the sysroot and the kernel's ABI
    /// headers.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>Compiler options.</returns>
    public static string[] Options(string root) =>
    [
        "-std=c11", "-Wall", "-Wextra", "-Werror", "-I", Path.Combine(root, "src", "Sysroot", "include"),
        "-I", Path.Combine(root, "src", "Kernel", "include")
    ];

    /// <summary>
    /// The programs the spawn scenario's package carries: tests/User/spawn_child.c as a static started program (S5.2),
    /// and tests/User/dynamic_main.c as a dynamic one with its library, the library it opens and musl's libc.so as its
    /// dynamic linker (S5.3).
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Artifact directory of the scenario.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>Package paths and the files to place there.</returns>
    public static async Task<IReadOnlyList<(string Name, string Source)>> BuildProgramsAsync(string root, string output,
        KernelArchitecture architecture)
    {
        async Task<string> Compile(string name, params string[] extra)
        {
            var obj = Path.Combine(output, name + ".o");
            await MuslLibc.CompileAsync(root, architecture, Path.Combine(root, "tests", "User", name + ".c"), obj, [.. Options(root), .. extra]);
            return obj;
        }
        var child = await MuslLibc.LinkStartedProgramAsync(root, architecture, output, "spawn_child", [await Compile("spawn_child")]);
        var shared = await MuslLibc.BuildSharedAsync(root, architecture);
        var library = await MuslLibc.LinkSharedLibraryAsync(root, architecture, output, "libdynamic.so", [await Compile("dynamic_library", "-fPIC")]);
        var plugin = await MuslLibc.LinkSharedLibraryAsync(root, architecture, output, "libplugin.so", [await Compile("dynamic_plugin", "-fPIC")]);
        var dynamic = await MuslLibc.LinkDynamicProgramAsync(root, architecture, output, "dynamic", [await Compile("dynamic_main")], [library]);

        // The shared C++ runtime and the acceptance of phase S (S5.4): a C++ library and its program, and the C++
        // scenarios of S4 as a dynamic program.
        var cxx = await LlvmRuntimes.BuildSharedAsync(root, architecture);
        string[] runtime = [cxx.Cxx, cxx.CxxAbi, cxx.Unwind];
        async Task<string> CompileCxx(string directory, string name, params string[] options)
        {
            var obj = Path.Combine(output, name + ".o");
            await LlvmRuntimes.CompileAsync(root, architecture, Path.Combine(root, "tests", directory, name + ".cpp"), obj, options);
            return obj;
        }
        string[] strict = ["-std=c++20", "-Wall", "-Wextra", "-Werror", .. Options(root).Skip(4)];
        var acceptanceLibrary = await MuslLibc.LinkSharedLibraryAsync(root, architecture, output, "libacceptance.so",
            [await CompileCxx("User", "acceptance_library", [.. strict, "-fPIC"])], runtime);
        var acceptance = await MuslLibc.LinkDynamicProgramAsync(root, architecture, output, "acceptance",
            [await CompileCxx("User", "acceptance_main", strict)], [acceptanceLibrary, .. runtime]);
        var cxxProgram = await MuslLibc.LinkDynamicProgramAsync(root, architecture, output, "cxx",
            [await CompileCxx("User.X64", "cxx_exceptions", "-std=c++20", "-fdeclspec", "-w"), await CompileCxx("User", "cxx_main", strict)],
            runtime);
        return
        [
            (CHILD_PATH, child), ("bin/dynamic", dynamic), ("lib/libdynamic.so", library), ("lib/libplugin.so", plugin),
            (MuslLibc.InterpreterPath(architecture).TrimStart('/'), shared.Library), ("bin/acceptance", acceptance), ("bin/cxx", cxxProgram),
            ("lib/libacceptance.so", acceptanceLibrary), ("lib/" + Path.GetFileName(cxx.Cxx), cxx.Cxx),
            ("lib/" + Path.GetFileName(cxx.CxxAbi), cxx.CxxAbi), ("lib/" + Path.GetFileName(cxx.Unwind), cxx.Unwind)
        ];
    }

    /// <summary>
    /// Builds the root task of the spawn scenario (tests/User/spawn_main.c over the libc and libwitos) as the flat image.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Artifact directory of the scenario.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <returns>Path of the flat image.</returns>
    public static async Task<string> BuildRootAsync(string root, string output, KernelArchitecture architecture)
    {
        var library = await BuildAsync(root, architecture);
        var obj = Path.Combine(output, "spawn_main.o");
        await MuslLibc.CompileAsync(root, architecture, Path.Combine(root, "tests", "User", "spawn_main.c"), obj, Options(root));
        var image = await MuslLibc.LinkAsync(root, architecture, output, "RootFixture", [obj], [library]);
        return await FlatImage.FromElfAsync(output, architecture.ElfMachine, image, "RootFixture", "wit_user_root_image",
            "user_root_image.h");
    }

    #endregion
}
