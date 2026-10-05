using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WitOS.Dev.Images;

/// <summary>
/// The pinned microsoft/STL sources of the guest C++ runtime (P6.4.i). src/Runtime.Cxx/stl.lock.json names the tag, the
/// commit and the SHA-256 of the canonical bytes of every file: all of stl/inc, the separately compiled sources the
/// host needs and the license files. They are downloaded once into .tools/stl/&lt;commit&gt; and verified on every use.
/// </summary>
internal static class StlSources
{
    #region Constants

    /// <summary>
    /// The lock file.
    /// </summary>
    public const string LOCK = "src/Runtime.Cxx/stl.lock.json";

    /// <summary>
    /// The WitOS replacement for the toolset's closed internal_shared.h, which the STL's sources include.
    /// </summary>
    public const string INTERNAL_SHARED = "src/Runtime.Cxx/stl";

    private const string REPOSITORY = "https://github.com/microsoft/STL";

    private const string TAG = "vs-2022-17.14";

    private const string LICENSE = "Apache-2.0 WITH LLVM-exception";

    #endregion

    #region Functions

    /// <summary>
    /// Reads and checks the lock file.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The pin.</returns>
    internal static async Task<StlPin> ReadPinAsync(string root)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var pin = JsonSerializer.Deserialize<StlPin>(await File.ReadAllTextAsync(Path.Combine(root, LOCK)), json)
            ?? throw new InvalidDataException("Missing STL pin.");
        if (pin.Repository != REPOSITORY || pin.Tag != TAG || pin.License != LICENSE ||
            !Regex.IsMatch(pin.Revision, "^[0-9a-f]{40}$") || pin.Files.Length == 0)
            throw new InvalidDataException("Unsupported STL pin.");
        foreach (var file in pin.Files)
        {
            if (!Regex.IsMatch(file.Sha256, "^[0-9a-f]{64}$") || Path.IsPathRooted(file.Path) ||
                file.Path.Contains('\\') || file.Path.Split('/').Any(part => part is "" or "." or ".."))
                throw new InvalidDataException("Invalid STL file entry: " + file.Path);
        }
        return pin;
    }

    /// <summary>
    /// Downloads the pinned files that are missing and verifies every file against the lock.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The directory that holds stl/inc and stl/src.</returns>
    internal static async Task<string> PrepareAsync(string root)
    {
        var pin = await ReadPinAsync(root);
        var directory = Path.GetFullPath(Path.Combine(root, ".tools", "stl", pin.Revision));
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        using var gate = new SemaphoreSlim(8);
        await Task.WhenAll(pin.Files.Select(async file =>
        {
            var destination = Path.GetFullPath(Path.Combine(directory, file.Path));
            if (!destination.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("STL file escaped the cache: " + file.Path);
            if (!File.Exists(destination))
            {
                await gate.WaitAsync();
                try
                {
                    var bytes = await client.GetByteArrayAsync(
                        $"https://raw.githubusercontent.com/microsoft/STL/{pin.Revision}/{file.Path}");
                    Verify(bytes, file);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    // Another build may fetch the same file at the same time: publish only complete files.
                    var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    await File.WriteAllBytesAsync(temporary, bytes);
                    File.Move(temporary, destination, overwrite: true);
                }
                finally
                {
                    gate.Release();
                }
            }
            Verify(await File.ReadAllBytesAsync(destination), file);
        }));
        return directory;
    }

    /// <summary>
    /// The compiler options of the STL's own build for a separately compiled source in a static library.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="stl">The directory <see cref="PrepareAsync"/> returned.</param>
    /// <returns>The cl options.</returns>
    internal static string[] CompileOptions(string root, string stl) => ["/Zl", "/Gy", "/Zp8", "/std:c++latest",
        "/permissive-", "/Zc:preprocessor", "/Zc:threadSafeInit-", "/EHsc", "/O2", "/GS-", "/W4", "/WX", "/D_CRTBLD",
        "/D_VCRT_ALLOW_INTERNALS", "/D_HAS_OLD_IOSTREAMS_MEMBERS=1", "/D_ITERATOR_DEBUG_LEVEL=0",
        "/I" + Path.Combine(stl, "stl", "inc"), "/I" + Path.Combine(root, INTERNAL_SHARED)];

    #endregion

    #region Tools

    private static void Verify(byte[] bytes, StlFile file)
    {
        if (Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != file.Sha256)
            throw new InvalidDataException("STL canonical-byte hash mismatch: " + file.Path);
    }

    #endregion
}
