using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using WitOS.Dev.Host;
using WitOS.Dev.Pe;
namespace WitOS.Dev.NativeAot;

/// <summary>
/// Compiles the guest NativeAOT boot driver and links the guest runtime image.
/// </summary>
internal static class RuntimeGuestDriver
{
    #region Functions

    /// <summary>
    /// Extracts defines, include paths and warning switches from a recorded compile command.
    /// </summary>
    /// <param name="command">Recorded command line.</param>
    /// <returns>Compiler arguments.</returns>
    internal static string[] CompileProfile(string command) => WindowsCommandLine.Parse(command).Skip(1)
        .Where(a => a.StartsWith("-D", StringComparison.Ordinal) || a.StartsWith("-I", StringComparison.Ordinal) ||
            a.StartsWith("/D", StringComparison.Ordinal) || a.StartsWith("/I", StringComparison.Ordinal) ||
            Regex.IsMatch(a, @"^/w(?:d|e|[1-4])")).ToArray();

    /// <summary>
    /// Compiles the guest boot driver and links it with the managed object and runtime libraries.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="msvc">Directory of the MSVC x64 host tools.</param>
    /// <param name="output">Output directory.</param>
    /// <param name="managed">NativeAOT managed object.</param>
    /// <param name="tls">Compiler TLS object.</param>
    /// <param name="libraries">Runtime libraries.</param>
    /// <returns>Guest image path.</returns>
    public static async Task<string> BuildAsync(string root, string msvc, string output, string managed, string tls, string[] libraries)
    {
        var directory = Path.Combine(output, "guest-driver");
        Directory.CreateDirectory(directory);
        var vc = Path.GetFullPath(Path.Combine(msvc, "..", "..", ".."));
        var version = Directory.GetParent(Toolchain.FindWindowsSdkLibrary("kernel32.lib"))!.Parent!.Parent!.Name;
        var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10", "Include", version);
        var source = Path.Combine(root, "src/Runtime.NativeAot/boot_driver.witos.cpp");
        var driver = Path.Combine(directory, "boot_driver.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), ["/nologo","/c","/std:c++17","/W4","/WX","/GS-","/Zl","/O1","/GR-","/EHs-c-",
            "/I"+Path.Combine(vc,"include"),"/I"+Path.Combine(sdk,"ucrt"),"/I"+Path.Combine(root,"src/Kernel/include"),
            "/I"+Path.Combine(root,"src/Runtime.Native"),"/I"+Path.Combine(root,"src/Runtime.NativeAot"),"/Fo"+driver,source], root);
        // Compile the test helper against the exact definitions/header profile of
        // the source-built PAL, without adding test callbacks to the runtime archive.
        var fixtureSource = Path.Combine(root, "tests/Runtime.NativeAot/worker_lifecycle.cpp");
        var fixture = Path.Combine(directory, "worker_lifecycle.obj");
        using var commands = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "artifacts/runtime-source/witos-compile-commands.json")));
        var palCommand = commands.RootElement.EnumerateArray().Single(c => c.GetProperty("file").GetString()!.EndsWith("/pal_attach.witos.cpp", StringComparison.Ordinal)).GetProperty("command").GetString()!;
        var profile = CompileProfile(palCommand);
        if (!profile.Contains("-DNO_STRESS_LOG") || !profile.Contains("-DFEATURE_SUSPEND_REDIRECTION"))
            throw new InvalidDataException("Worker acceptance lost actual runtime compile profile.");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), ["/nologo","/c","/std:c++17","/W4","/WX","/GS","/Zl","/O1","/Oi","/MT","/Zp8","/GR-","/EHs-c-",
            "/I"+Path.Combine(vc,"include"),"/I"+Path.Combine(sdk,"ucrt"),"/I"+Path.Combine(sdk,"shared"),"/I"+Path.Combine(sdk,"um"),
            ..profile,"/Fo"+fixture,fixtureSource], root);
        var abruptSource = Path.Combine(root, "tests/Runtime.NativeAot/abrupt_lifecycle.cpp");
        var abrupt = Path.Combine(directory, "abrupt_lifecycle.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "cl.exe"), ["/nologo","/c","/std:c++17","/W4","/WX","/GS","/Zl","/O1","/Oi","/MT","/Zp8","/GR-","/EHs-c-",
            "/I"+Path.Combine(vc,"include"),"/I"+Path.Combine(sdk,"ucrt"),"/I"+Path.Combine(sdk,"shared"),"/I"+Path.Combine(sdk,"um"),..profile,"/Fo"+abrupt,abruptSource], root);
        var entry = Path.Combine(directory, "native_start.obj");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"), ["/nologo", "/c", "/I" + output, "/Fo" + entry, Path.Combine(root, "src/Runtime.Native/X64/native_start.asm")], root);
        var faultFixture = Path.Combine(directory, "runtime_fault_fixture.obj");
        var faultSource = Path.Combine(root, "tests/User.X64/runtime_fault_fixture.asm");
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"), ["/nologo", "/c", "/Fo" + faultFixture, faultSource], root);
        var image = Path.Combine(directory, "WitOS.NativeAotBoot.pe");
        string[] arguments = ["/nologo","/subsystem:native","/entry:wit_native_start","/nodefaultlib","/machine:x64","/fixed:no","/dynamicbase","/incremental:no","/Brepro","/opt:ref","/include:_tls_used","/merge:.CRT=.rdata",
            "/map:"+Path.Combine(directory,"WitOS.NativeAotBoot.map"),"/out:"+image,driver,fixture,abrupt,faultFixture,entry,managed,tls,..libraries];
        await File.WriteAllLinesAsync(Path.Combine(directory, "link-arguments.txt"), arguments);
        var link = await Processes.RunAsync(Path.Combine(msvc, "link.exe"), arguments, root, 120);
        await File.WriteAllTextAsync(Path.Combine(directory, "link.log"), link.Output + link.Error);
        if (link.TimedOut || link.ExitCode != 0 || !File.Exists(image))
            throw new InvalidDataException("Guest runtime driver strict link failed; see guest-driver/link.log.");
        var module = NativeModule.Inspect(image);
        var imports = NativeImports.Inspect(image);
        if (imports.HasClrHeader || imports.DirectImports.Length != 0 || imports.DelayImportDirectorySize != 0 || module.Tls is null || module.UnwindEntries == 0)
            throw new InvalidDataException("Guest runtime driver retained OS imports or lost native runtime metadata.");
        var map = await File.ReadAllTextAsync(Path.Combine(directory, "WitOS.NativeAotBoot.map"));
        ulong Symbol(string name)
        {
            var matches = Regex.Matches(map, @"^\s+[0-9a-fA-F]{4}:[0-9a-fA-F]{8}\s+" + Regex.Escape(name) + @"\s+([0-9a-fA-F]{16})\b", RegexOptions.Multiline);
            if (matches.Count != 1)
                throw new InvalidDataException("Missing/ambiguous driver root: " + name);
            return Convert.ToUInt64(matches[0].Groups[1].Value, 16);
        }
        bool Code(ulong address) => address >= module.PreferredBase && module.Sections.Any(s => s.Protection == "RX" &&
            address - module.PreferredBase >= (uint)s.Rva && address - module.PreferredBase - (uint)s.Rva < (uint)Math.Min(s.VirtualSize, s.RawSize));
        if (Symbol("wit_native_start") != module.PreferredBase + (uint)module.EntryRva || !Code(Symbol("wmain")) || !Code(Symbol("__managed__Main")))
            throw new InvalidDataException("Driver lost actual native/upstream/managed entry roots.");
        var abruptReportRva = checked((uint)(Symbol("wit_runtime_abrupt_report") - module.PreferredBase));
        if (!module.Sections.Any(s => s.Protection == "RW" && abruptReportRva >= (uint)s.Rva && (ulong)abruptReportRva + 32 <= (ulong)(uint)s.Rva + (uint)s.VirtualSize))
            throw new InvalidDataException("Abrupt report is not mapped writable image data.");
        using var stream = File.OpenRead(image);
        using var pe = new PEReader(stream);
        int Initializers(string begin, string end)
        {
            var first = Symbol(begin);
            var last = Symbol(end);
            if (first < module.PreferredBase || last <= first || last - module.PreferredBase >= (uint)module.ImageBytes || (last - first) % 8 != 0 || (last - first) / 8 - 1 > 64)
                throw new InvalidDataException("Invalid driver initializer table bounds.");
            var rva = checked((uint)(first - module.PreferredBase));
            var bytes = checked((int)(last - first + 8));
            if (!module.Sections.Any(s => s.Protection == "R" && rva >= (uint)s.Rva && (ulong)rva + (uint)bytes <= (ulong)(uint)s.Rva + (uint)Math.Min(s.VirtualSize, s.RawSize)))
                throw new InvalidDataException("Driver initializer table is not initialized readonly data.");
            var data = pe.GetSectionData((int)rva).GetContent(0, bytes).ToArray();
            if (BinaryPrimitives.ReadUInt64LittleEndian(data) != 0 || BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(bytes - 8)) != 0)
                throw new InvalidDataException("Driver initializer sentinels changed.");
            var count = 0;
            for (var offset = 8; offset < bytes - 8; offset += 8)
            {
                var target = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset));
                if (target != 0)
                { if (!Code(target)) throw new InvalidDataException("Driver initializer points outside initialized RX code."); ++count; }
            }
            return count;
        }
        var initializers = new { c = Initializers("wit_runtime_c_begin", "wit_runtime_c_end"), cpp = Initializers("wit_runtime_cpp_begin", "wit_runtime_cpp_end") };
        string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
        await File.WriteAllTextAsync(Path.Combine(directory, "image.json"), JsonSerializer.Serialize(new
        {
            abruptReportRva,
            guestExecuted = false,
            managedExecuted = false,
            entry = "wit_native_start",
            upstreamEntry = "wmain",
            image,
            sha256 = Hash(image),
            module,
            imports,
            initializers,
            environment = new { DOTNET_GCHeapHardLimit = "400000" },
            inputs = libraries.Concat(new[] { driver, fixture, abrupt, faultFixture, entry, managed, tls }).Select(file => new { file, sha256 = Hash(file) }),
            source = new { file = source, sha256 = Hash(source) },
            workerFixture = new { file = fixtureSource, sha256 = Hash(fixtureSource) },
            abruptFixture = new { file = abruptSource, sha256 = Hash(abruptSource) },
            faultFixture = new { file = faultSource, sha256 = Hash(faultSource) },
            scope = "Real upstream executable/bootstrap/CoreLib with native image/environment publication, GS seed, TLS and initializer tables. Strict linking only; loader budgets and actual runtime/GC execution still require guest acceptance."
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Console.WriteLine($"[DRIVER-LINK-PASS] {module.ImageBytes} mapped bytes, {module.UnwindEntries} unwind entries, TLS {module.Tls.TemplateBytes} bytes; guest execution pending.");
        return image;
    }

    #endregion
}
