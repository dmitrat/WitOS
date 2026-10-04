using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WitOS.Dev.Host;

namespace WitOS.Dev.Images;

/// <summary>
/// Builds a PAL-profile guest fixture from its manifest: compile, link with the shared objects, check the guest
/// profile, embed the image as a C header and record the evidence.
/// </summary>
internal static class PalFixtureImage
{
    #region Fields

    private static readonly JsonSerializerOptions REPORT_JSON = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    #endregion

    #region Functions

    /// <summary>
    /// Builds one fixture.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="output">Artifact directory already populated by the base fixtures.</param>
    /// <param name="msvc">Compiler directory.</param>
    /// <param name="name">Manifest name in build/fixtures.</param>
    /// <returns>A task completing after image validation and embedding.</returns>
    /// <exception cref="InvalidDataException">The image violates the supported guest profile.</exception>
    public static async Task BuildAsync(string root, string output, string msvc, string name)
    {
        var fixture = PalFixtureManifest.Read(root, name);
        foreach (var source in fixture.Assembly)
        {
            await Processes.RequireSuccessAsync(Path.Combine(msvc, "ml64.exe"),
                ["/nologo", "/c", $"/Fo{Path.Combine(output, Path.GetFileNameWithoutExtension(source) + ".obj")}",
                    Path.Combine(root, source)], root);
        }
        var objects = await PalFixtureCompiler.CompileAsync(root, output, msvc, fixture.ObjectPrefix, fixture.Sources,
            fixture.UpstreamHeaders, fixture.Defines, fixture.FunctionSections);
        var path = Path.Combine(output, fixture.Image);
        await Processes.RequireSuccessAsync(Path.Combine(msvc, "link.exe"),
            ["/nologo", "/subsystem:native", "/entry:wit_native_start", "/nodefaultlib", "/machine:x64", "/fixed:no",
                "/dynamicbase", "/incremental:no", "/Brepro", "/base:0x180000000", "/include:_tls_used", "/merge:.CRT=.rdata",
                $"/out:{path}", .. fixture.Shared.Select(file => Path.Combine(output, file)), .. objects], root);
        var bytes = await File.ReadAllBytesAsync(path);
        var unwindEntries = Validate(fixture, bytes);
        await File.WriteAllTextAsync(Path.Combine(output, fixture.Header), Header(fixture, bytes), Encoding.ASCII);
        await File.WriteAllTextAsync(Path.Combine(output, fixture.Report), JsonSerializer.Serialize(new
        {
            scope = fixture.Scope,
            guestManagedRuntime = false,
            compiler = msvc,
            imageBytes = bytes.Length,
            imageSha256 = Hash(bytes),
            unwindEntries,
            localSources = fixture.Sources.Concat(fixture.Assembly).Concat(fixture.Inputs)
                .Select(file => new { path = file, sha256 = Hash(File.ReadAllBytes(Path.Combine(root, file))) }),
            sharedObjects = fixture.Shared
                .Select(file => new { file, sha256 = Hash(File.ReadAllBytes(Path.Combine(output, file))) })
        }, REPORT_JSON));
        Console.WriteLine($"{Path.GetFileNameWithoutExtension(fixture.Image)}: {bytes.Length} bytes, {fixture.Summary}, no OS/CRT imports.");
    }

    // Guest profile: no imports, delay imports, load config or CLR header; one static TLS directory and relocations.
    private static int Validate(PalFixture fixture, byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var pe = new PEReader(stream);
        var header = pe.PEHeaders.PEHeader ?? throw new InvalidDataException($"{fixture.Image}: PE header missing.");
        if (header.ImportTableDirectory.Size != 0 || header.DelayImportTableDirectory.Size != 0 ||
            header.LoadConfigTableDirectory.Size != 0 || header.ThreadLocalStorageTableDirectory.Size != 40 ||
            header.BaseRelocationTableDirectory.Size == 0 || pe.PEHeaders.CorHeader is not null)
        {
            throw new InvalidDataException($"{fixture.Image} contains unsupported imports/metadata.");
        }
        return header.ExceptionTableDirectory.Size / 12;
    }

    private static string Header(PalFixture fixture, byte[] bytes)
    {
        var header = new StringBuilder($"/* {fixture.Description}; generated. */\nstatic const unsigned char {fixture.Symbol}[] = {{\n");
        for (var i = 0; i < bytes.Length; i += 16)
        {
            header.AppendLine("    " + string.Join(", ", bytes.Skip(i).Take(16).Select(b => $"0x{b:X2}")) + ",");
        }
        header.AppendLine("};");
        return header.ToString();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    #endregion
}
