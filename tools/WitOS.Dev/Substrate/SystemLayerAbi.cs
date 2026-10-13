using System.Text.Json;
using WitOS.Dev.Images;
using WitOS.Dev.Kernel;

namespace WitOS.Dev.Substrate;

/// <summary>
/// ABI-2, the system layer as .NET sees it (RFC 0011 sections 9 and 10.2), frozen at 1.0 at plan step N3.3, the first
/// image with two runtimes. A runtime built for witos links libc.so, which is also the dynamic linker, and LLVM's C++
/// runtime by its sonames. build/abi2.json records those libraries and the names libc.so exports on each ISA, less the
/// system layer's private __wit_ ones, which its own libwitos reaches and which no runtime uses. Every sysroot build
/// requires libc.so to export exactly the recorded names: a name that disappears breaks a runtime built against 1.0, and
/// one that appears is an addition, which the record takes with a new minor version.
/// </summary>
internal static class SystemLayerAbi
{
    #region Constants

    /// <summary>
    /// The record of ABI-2.
    /// </summary>
    public const string RECORD = "build/abi2.json";

    /// <summary>
    /// The prefix of the system layer's private names in libc.so: the interface between its libc and libwitos, built
    /// together, outside ABI-2.
    /// </summary>
    public const string PRIVATE_PREFIX = "__wit_";

    #endregion

    #region Functions

    /// <summary>
    /// Reads the record.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The record.</returns>
    public static Abi2Record Read(string root)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, RECORD)));
        var element = document.RootElement;
        return new Abi2Record(element.GetProperty("version").GetString() ?? "",
            [.. element.GetProperty("libraries").EnumerateArray().Select(library => library.GetString() ?? "")],
            element.GetProperty("libc").EnumerateObject().ToDictionary(isa => isa.Name,
                isa => (IReadOnlyList<string>)[.. isa.Value.EnumerateArray().Select(name => name.GetString() ?? "")], StringComparer.Ordinal));
    }

    /// <summary>
    /// Requires a sysroot's libraries to be ABI-2 as recorded.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Target architecture.</param>
    /// <param name="lib">The sysroot's library directory.</param>
    /// <exception cref="InvalidDataException">A library is missing, or libc.so exports otherwise than the record.</exception>
    public static void Check(string root, KernelArchitecture architecture, string lib)
    {
        var record = Read(root);
        var missing = record.Libraries.Where(library => !File.Exists(Path.Combine(lib, library))).ToList();
        if (missing.Count > 0)
            throw new InvalidDataException($"The sysroot for {architecture.Triple} lacks {string.Join(", ", missing)}, which ABI-2 {record.Version} names ({RECORD}).");
        if (!record.Libc.TryGetValue(architecture.Name, out var recorded))
            throw new InvalidDataException($"{RECORD} records no names of libc.so for {architecture.Name}.");
        var exported = ElfExports.Read(Path.Combine(lib, "libc.so")).Where(name => !name.StartsWith(PRIVATE_PREFIX, StringComparison.Ordinal)).ToList();
        var removed = recorded.Except(exported, StringComparer.Ordinal).ToList();
        var added = exported.Except(recorded, StringComparer.Ordinal).ToList();
        if (removed.Count > 0 || added.Count > 0)
            throw new InvalidDataException($"libc.so for {architecture.Triple} is not ABI-2 {record.Version} ({RECORD}): " +
                (removed.Count > 0 ? $"it no longer exports {string.Join(", ", removed.Take(20))}, which breaks a runtime built against it; " : "") +
                (added.Count > 0 ? $"it exports {string.Join(", ", added.Take(20))} anew, an addition the record takes with a new minor version." : ""));
    }

    #endregion
}

/// <summary>
/// The record of ABI-2.
/// </summary>
/// <param name="Version">Its version, major.minor.</param>
/// <param name="Libraries">The libraries of the sysroot a runtime links, by the names it links them.</param>
/// <param name="Libc">The names libc.so exports on each ISA, less the system layer's private ones.</param>
internal sealed record Abi2Record(string Version, IReadOnlyList<string> Libraries, IReadOnlyDictionary<string, IReadOnlyList<string>> Libc);
