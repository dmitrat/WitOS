using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WitOS.Dev.Host;
using WitOS.Dev.Images;
using WitOS.Dev.NativeAot;
namespace WitOS.Dev.Kernel;

/// <summary>
/// Builds the boot package of guest assemblies and native libraries placed beside the kernel on the FAT image.
/// </summary>
internal static class BootPackage
{
    #region Functions

    /// <summary>
    /// Builds the boot package for a scenario.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Output directory.</param>
    /// <param name="assemblies">Whether to include the unchanged guest assemblies.</param>
    /// <param name="nativeLibraries">Whether to include the native library fixtures.</param>
    /// <param name="libcFiles">Whether to include the files the libc programs read.</param>
    /// <param name="programs">Programs a root task starts from the package (S5.2): package paths and files.</param>
    /// <returns>Package bytes.</returns>
    internal static async Task<byte[]> BuildAsync(string root, string output, bool assemblies, bool nativeLibraries = false,
        bool libcFiles = false, IReadOnlyList<(string Name, string Source)>? programs = null)
    {
        var files = new List<(string Name, ReadOnlyMemory<byte> Bytes)>();
        var manifest = new List<object>();
        async Task Add(string name, string source)
        {
            var bytes = await File.ReadAllBytesAsync(source);
            files.Add((name, bytes));
            manifest.Add(new { name, source, bytes = bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() });
        }
        if (assemblies)
        {
            var pin = RuntimeExperiment.ReadLock(root);
            var framework = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet/shared/Microsoft.NETCore.App", pin.RuntimeVersion);
            var corelib = Path.Combine(framework, "System.Private.CoreLib.dll");
            var product = System.Diagnostics.FileVersionInfo.GetVersionInfo(corelib).ProductVersion;
            if (product is null || !product.StartsWith(pin.RuntimeVersion + "-", StringComparison.Ordinal) || !product.EndsWith("+" + pin.PackageCommit, StringComparison.Ordinal))
                throw new InvalidDataException("Boot package CoreLib provenance mismatch.");
            foreach (var source in Directory.EnumerateFiles(framework, "*.dll").Order(StringComparer.Ordinal))
            {
                using var stream = File.OpenRead(source);
                using var pe = new PEReader(stream);
                if (pe.PEHeaders.CorHeader is null)
                    continue; // Never deliver Windows native implementations.
                await Add("dotnet/shared/Microsoft.NETCore.App/" + pin.RuntimeVersion + "/" + Path.GetFileName(source), source);
            }
            foreach (var name in new[] { "Microsoft.NETCore.App.deps.json", "Microsoft.NETCore.App.runtimeconfig.json" })
                await Add("dotnet/shared/Microsoft.NETCore.App/" + pin.RuntimeVersion + "/" + name, Path.Combine(framework, name));
            var app = Path.Combine(output, "portable");
            await Processes.RequireSuccessAsync("dotnet", ["build", Path.Combine(root, "experiments/CoreClrProbe/CoreClrProbe.csproj"), "--configuration", "Release", "--output", app], root);
            foreach (var name in new[] { "CoreClrProbe.dll", "CoreClrProbe.deps.json", "CoreClrProbe.runtimeconfig.json" })
                await Add("app/" + name, Path.Combine(app, name));
            await Add("p", Path.Combine(app, "CoreClrProbe.runtimeconfig.json")); // Short-name control for real constexpr C++ string PAL probe.
            files.Add(("test/empty", Array.Empty<byte>()));
            manifest.Add(new { name = "test/empty", source = (string?)null, bytes = 0, sha256 = Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant() });
        }
        if (assemblies || nativeLibraries)
        {
            var library = await NativeLibraryImage.BuildAsync(root, Path.Combine(output, "native-library"), await Toolchain.FindMsvcAsync(root));
            await Add("native/lib.dll", library);
            await Add("native/WitLibraryFixture.dll", library);
            var tlsLibrary = await NativeTlsLibraryImage.BuildAsync(root, Path.GetDirectoryName(library)!, await Toolchain.FindMsvcAsync(root));
            await Add("native/statictls.dll", tlsLibrary);
            await Add("native/tlssecond.dll", tlsLibrary);
            var tlsOutput = Path.Combine(output, "native-tls-callbacks");
            var (tlsSink, tlsCallbacks, tlsNoEntry) = await NativeTlsCallbackLibraryImage.BuildAsync(root, tlsOutput,
                await Toolchain.FindMsvcAsync(root));
            await Add("native/tlssink.dll", tlsSink);
            await Add("native/tlscallbacks.dll", tlsCallbacks);
            await Add("native/tlsnoentry.dll", tlsNoEntry);
            await Add("native/tlsobjects.dll", await NativeTlsCallbackLibraryImage.BuildObjectsAsync(root, tlsOutput,
                await Toolchain.FindMsvcAsync(root)));
            await Add("native/threadnotify.dll", await NativeThreadLibraryImage.BuildAsync(root, Path.Combine(output, "native-thread-library"), await Toolchain.FindMsvcAsync(root)));
            var dependencies = await NativeLibraryImage.BuildDependenciesAsync(root, Path.GetDirectoryName(library)!, await Toolchain.FindMsvcAsync(root));
            foreach (var entry in dependencies)
                await Add("native/" + entry.Key, entry.Value);
            await Add("test/dependent.dll", dependencies["dependent.dll"]); // Missing sibling dependency must fail transactionally.
            await Add("test/lib.dll", library); // Distinct module identity with an ambiguous basename.
        }
        if (nativeLibraries)
        {
            // The host runtime fixture, which the host PAL's mode runs from the package to see its own path (P6.4.j3b).
            await Add("host/HostRuntimeFixture.pe", Path.Combine(output, "HostRuntimeFixture.pe"));
            // The C++ library mode 27 loads (P6.4.j3c).
            await Add("host/cxxlib.dll", Path.Combine(output, "cxxlib.dll"));
        }
        if (assemblies || nativeLibraries)
        {
            // The .NET host's libraries in the layout of a .NET root at /dotnet (P6.4.j3c3, P6.4.k1), with the
            // framework's deps.json, without which hostfxr ignores a framework version; the delivered framework brings
            // its own. The root is a directory, as an installation is: upstream drops a root's trailing separator.
            var version = RuntimeExperiment.ReadLock(root).RuntimeVersion;
            await Add("dotnet/host/fxr/" + version + "/hostfxr.dll", Path.Combine(output, "host", "hostfxr.dll"));
            await Add("dotnet/shared/Microsoft.NETCore.App/" + version + "/hostpolicy.dll", Path.Combine(output, "host", "hostpolicy.dll"));
            if (!assemblies)
            {
                var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "dotnet/shared/Microsoft.NETCore.App", version);
                await Add("dotnet/shared/Microsoft.NETCore.App/" + version + "/Microsoft.NETCore.App.deps.json",
                    Path.Combine(installed, "Microsoft.NETCore.App.deps.json"));
            }
        }
        if (libcFiles)
        {
            // The files the first libc program reads through the package (S1.2): a text file and a directory of two.
            foreach (var (name, text) in new[] { ("test/hello.txt", "Hello, package!\nsecond line\n"), ("test/dir/a.txt", "a\n"), ("test/dir/b.txt", "bb\n") })
            {
                var bytes = Encoding.ASCII.GetBytes(text);
                files.Add((name, bytes));
                manifest.Add(new { name, source = (string?)null, bytes = bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() });
            }
            // Code the libc program maps executable from the package (S5.1): two pages, a function returning 42 for x64
            // (mov eax, 42; ret) in the first and for ARM64 (mov w0, #42; ret) in the second, page-aligned as a file of
            // at least a page is.
            var code = new byte[8192];
            new byte[] { 0xB8, 0x2A, 0x00, 0x00, 0x00, 0xC3 }.CopyTo(code, 0);
            new byte[] { 0x40, 0x05, 0x80, 0x52, 0xC0, 0x03, 0x5F, 0xD6 }.CopyTo(code, 4096);
            files.Add(("test/code.bin", code));
            manifest.Add(new { name = "test/code.bin", source = (string?)null, bytes = code.Length, sha256 = Convert.ToHexString(SHA256.HashData(code)).ToLowerInvariant() });
        }
        foreach (var (name, source) in programs ?? [])
            await Add(name, source);
        if (assemblies)
        {
            // The muxer in the .NET root, as dotnet is installed (P6.4.k1): the host runtime fixture.
            await Add("dotnet/dotnet", Path.Combine(output, "HostRuntimeFixture.pe"));
        }
        var package = AssemblyPackage.Create(files);
        await File.WriteAllBytesAsync(Path.Combine(output, "boot.pak"), package);
        await File.WriteAllTextAsync(Path.Combine(output, "boot-package.json"), JsonSerializer.Serialize(new
        {
            profile = assemblies ? "readonly managed/native assembly and configuration bytes; not guest CoreCLR execution" : nativeLibraries ? "readonly native library fixtures; not guest CoreCLR execution" : "empty readonly boot package",
            fileCount = files.Count,
            packageBytes = package.Length,
            sha256 = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant(),
            files = manifest
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (assemblies)
        {
            var header = new StringBuilder("static const struct { const char* Name; WitU64 Bytes,Hash; } wit_storage_files[]={\n");
            foreach (var file in files)
            {
                ulong hash = 14695981039346656037UL;
                foreach (var b in file.Bytes.Span)
                    hash = unchecked((hash ^ b) * 1099511628211UL);
                header.AppendLine($"{{\"{file.Name}\",{file.Bytes.Length}ULL,0x{hash:X16}ULL}},");
            }
            header.AppendLine("};");
            header.AppendLine($"#define WIT_STORAGE_FILE_COUNT {files.Count}U");
            header.AppendLine("#define WIT_STORAGE_RUNTIME_VERSION " + JsonSerializer.Serialize(RuntimeExperiment.ReadLock(root).RuntimeVersion));
            await File.WriteAllTextAsync(Path.Combine(output, "storage_manifest.h"), header.ToString(), Encoding.ASCII);
        }
        Console.WriteLine($"Boot package: {files.Count} files, {package.Length} bytes, separate boot-owned readonly/NX allocation.");
        return package;
    }

    #endregion
}
