using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WitOS.Dev.Host;
using WitOS.Dev.Images;

namespace WitOS.Dev.NativeAot;

/// <summary>
/// Builds the guest image that runs the pinned GC memory adapter checks.
/// </summary>
internal static class RuntimePortImage
{
    #region Constants

    internal const string BACKEND = "windows-x64-codegen-witos-pal";

    #endregion

    #region Fields

    private static readonly string[] UPSTREAM_INPUTS =
    [
        "src/coreclr/gc/env/gcenv.os.h", "src/coreclr/gc/env/gcenv.base.h", "src/coreclr/gc/env/gcenv.windows.inl",
        "src/coreclr/gc/env/gcenv.structs.h", "src/native/minipal/utils.h",
        "src/native/minipal/mutex.h", "src/coreclr/nativeaot/Runtime/Crst.h", "LICENSE.TXT"
    ];

    #endregion

    #region Functions

    /// <summary>
    /// Compiles the GC memory adapter against verified upstream headers and links the port image.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Output directory.</param>
    /// <param name="msvc">Directory of the MSVC x64 host tools.</param>
    public static async Task BuildAsync(string root, string output, string msvc)
    {
        var pin = RuntimeExperiment.ReadLock(root);
        // Compile from a bounded, verified include tree, never from a developer's
        // mutable full checkout or an unpinned installed runtime header.
        var stage = Path.Combine(output, "runtime-source");
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        foreach (var path in UPSTREAM_INPUTS)
        {
            var source = pin.Sources.Single(s => s.Path == path);
            var input = await RuntimeExperiment.FetchAsync(client, Path.Combine(root, ".tools", "runtime-audit"),
                "runtime", pin.RuntimeCommit, source.Path, source.Sha256);
            var destination = Path.Combine(stage, path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(input, destination, overwrite: true);
        }
        var vc = Path.GetFullPath(Path.Combine(msvc, "..", "..", ".."));
        var sdkLib = Toolchain.FindWindowsSdkLibrary("kernel32.lib");
        var sdkVersion = Directory.GetParent(sdkLib)!.Parent!.Parent!.Name;
        var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Windows Kits", "10", "Include", sdkVersion);
        string[] compile =
        [
            "/nologo", "/c", "/TP", "/std:c++17", "/W4", "/WX", "/GS-", "/Zl", "/Oi", "/O1", "/GR-",
            "/DHOST_64BIT", "/DHOST_WINDOWS", "/DTARGET_WINDOWS", "/DHOST_AMD64", "/DTARGET_AMD64", "/DTARGET_64BIT",
            "/DNDEBUG", "/DWIN32_LEAN_AND_MEAN", "/DNOMINMAX",
            $"/I{Path.Combine(vc, "include")}", $"/I{Path.Combine(sdk, "ucrt")}",
            $"/I{Path.Combine(sdk, "um")}", $"/I{Path.Combine(sdk, "shared")}",
            $"/I{Path.Combine(stage, "src", "coreclr", "gc", "env")}", $"/I{Path.Combine(stage, "src", "native")}",
            $"/I{Path.Combine(stage, "src", "coreclr", "nativeaot", "Runtime")}",
            $"/I{Path.Combine(root, "src", "Runtime.NativeAot")}", $"/I{Path.Combine(root, "src", "Runtime.Native")}",
            $"/I{Path.Combine(root, "src", "Kernel", "include")}", $"/I{Path.Combine(root, "tests", "User.X64")}"
        ];
        string[] sources = ["src/Runtime.NativeAot/gcenv.witos.cpp", "tests/User.X64/gc_memory.cpp", "tests/User.X64/gc_missing.cpp", "tests/User.X64/gc_discovery.cpp", "src/Runtime.NativeAot/gc_events.witos.cpp", "tests/User.X64/gc_events.cpp", "src/Runtime.NativeAot/gc_time.witos.cpp", "tests/User.X64/gc_time.cpp", "src/Runtime.NativeAot/mutex.witos.cpp", "src/Runtime.NativeAot/crst.witos.cpp", "tests/User.X64/gc_mutex.cpp", "tests/User.X64/gc_reset.cpp", "src/Runtime.NativeAot/native_new.witos.cpp", "tests/User.X64/native_heap.cpp"];
        var objects = new List<string>();
        foreach (var source in sources)
        {
            var obj = Path.Combine(output, Path.GetFileNameWithoutExtension(source) + ".obj");
            await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), [.. compile, $"/Fo{obj}", Path.Combine(root, source)], root);
            objects.Add(obj);
        }
        // native_start.obj was assembled by UserBootstrapImage; all x64 ABI glue stays there.
        string[] link = ["/nologo", "/subsystem:native", "/entry:wit_native_start", "/nodefaultlib", "/machine:x64",
            "/fixed:no", "/dynamicbase", "/incremental:no", "/Brepro", "/base:0x180000000",
            Path.Combine(output, "native_start.obj"), objects[0], objects[4], objects[6], objects[8], objects[9], objects[12]];
        var missing = await Processes.RunAsync(Path.Combine(msvc, "link.exe"),
            [.. link, $"/out:{Path.Combine(output, "GcMissing.pe")}", objects[2]], root);
        var diagnostic = missing.Output + missing.Error;
        await File.WriteAllTextAsync(Path.Combine(output, "gc-missing-link.log"), diagnostic);
        var errors = Regex.Matches(diagnostic, @"error LNK\d+:[^\r\n]*");
        if (missing.TimedOut || missing.ExitCode == 0 || errors.Count != 2 ||
            errors.Count(e => e.Value.Contains("LNK2019:", StringComparison.Ordinal) &&
                e.Value.Contains("?ResetWriteWatch@GCToOSInterface@@SAXPEAX_K@Z", StringComparison.Ordinal)) != 1 ||
            errors.Count(e => e.Value.Contains("LNK1120: 1 ", StringComparison.Ordinal)) != 1)
            throw new InvalidDataException($"Expected exactly the unimplemented GC write-watch reset link failure.\n{diagnostic}");
        var image = Path.Combine(output, "GcMemoryFixture.pe");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"),
            [.. link, $"/out:{image}", $"/map:{Path.Combine(output, "GcMemoryFixture.map")}", objects[1], objects[3], objects[5], objects[7], objects[10], objects[11], objects[13]], root);
        var bytes = await File.ReadAllBytesAsync(image);
        using var stream = new MemoryStream(bytes, writable: false);
        using var pe = new PEReader(stream);
        var h = pe.PEHeaders.PEHeader ?? throw new InvalidDataException("GC memory fixture PE missing.");
        if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64 || pe.PEHeaders.CorHeader is not null ||
            h.Subsystem != Subsystem.Native || h.ImportTableDirectory.Size != 0 ||
            h.DelayImportTableDirectory.Size != 0 || h.ThreadLocalStorageTableDirectory.Size != 0 ||
            h.ExceptionTableDirectory.Size < 12 || h.LoadConfigTableDirectory.Size != 0 || h.BaseRelocationTableDirectory.Size == 0)
            throw new InvalidDataException("GC memory fixture must be a native PE without OS imports, compiler TLS or load config.");
        var generated = new StringBuilder("/* GCToOSInterface memory adapter fixture; generated. */\nstatic const unsigned char wit_gc_memory_image[] = {\n");
        for (var i = 0; i < bytes.Length; i += 16)
            generated.AppendLine("    " + string.Join(", ", bytes.Skip(i).Take(16).Select(b => $"0x{b:X2}")) + ",");
        generated.AppendLine("};");
        await File.WriteAllTextAsync(Path.Combine(output, "gc_memory_image.h"), generated.ToString(), Encoding.ASCII);
        var report = new
        {
            backend = BACKEND,
            pin.RuntimeVersion,
            pin.RuntimeCommit,
            scope = "Source-level GC memory/discovery/event/time and minipal/Crst mutex slice; no collector or managed code linked. Guest execution is checked separately by the VM runner.",
            guestManagedRuntime = false,
            missingGcWriteWatchResetRejected = true,
            upstreamInputs = pin.Sources.Where(s => UPSTREAM_INPUTS.Contains(s.Path)),
            localInputs = sources.Append("src/Runtime.NativeAot/gcenv.witos.h").Append("src/Runtime.Native/X64/native_start.asm")
                .Concat(["src/Runtime.Native/bootstrap.h", "src/Runtime.Native/native_limits.h", "src/Kernel/include/witos/user_abi.h", "src/Kernel/include/witos/limits.h",
                    "src/Kernel/include/witos/types.h", "src/Kernel/include/witos/thread_info.h", "src/Kernel/include/witos/image_info.h", "src/Kernel/include/witos/memory_info.h", "tests/User/protocol.h"])
                .Select(p => new { path = p, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, p)))).ToLowerInvariant() }),
            compiler = msvc,
            sdkVersion,
            imageBytes = bytes.Length,
            imageSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            imports = 0,
            unwindEntries = h.ExceptionTableDirectory.Size / 12
        };
        await File.WriteAllTextAsync(Path.Combine(output, "gc-memory-build.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Console.WriteLine($"GcMemoryFixture: {bytes.Length} bytes; pinned upstream interface, WitOS syscalls, no OS/CRT imports; missing GC write-watch reset rejected.");
    }

    #endregion
}
