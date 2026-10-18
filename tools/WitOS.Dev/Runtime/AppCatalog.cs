using System.Text.Json;
using System.Text.RegularExpressions;

namespace WitOS.Dev.Runtime;

/// <summary>
/// The catalog of base applications (ADR 0027, plan step C4.1): one file a base application under build/apps, named by
/// its identity, which pins what its upstream publishes — a release archive by the SHA-256 of its bytes, a .NET tool
/// package by the SHA-256 of the package, or a repository commit the stock SDK builds as a portable application without
/// a RID — beside the native part built from source, the commands the application provides and the framework it runs on.
/// Never a binary of a Linux RID.
/// </summary>
internal static class AppCatalog
{
    #region Constants

    /// <summary>
    /// The catalog's directory, from the repository root.
    /// </summary>
    public const string DIRECTORY = "build/apps";

    /// <summary>
    /// A release archive: the URL of its bytes, the file's name and their SHA-256.
    /// </summary>
    public const string ARCHIVE = "archive";

    /// <summary>
    /// A .NET tool package of nuget.org: its identity, version and the package's SHA-256.
    /// </summary>
    public const string TOOL = "tool";

    /// <summary>
    /// A repository commit and the project the stock SDK publishes without a RID.
    /// </summary>
    public const string SOURCE = "source";

    private static readonly Regex IDENTITY = new(@"^[A-Z][A-Za-z0-9]*(\.[A-Z][A-Za-z0-9]*)*$");
    private static readonly Regex VERSION = new(@"^[0-9]+(\.[0-9]+){1,3}$");
    private static readonly Regex FRAMEWORK = new(@"^Microsoft\.NETCore\.App [0-9]+\.[0-9]+$");
    private static readonly Regex COMMAND = new(@"^[a-z][a-z0-9-]*$");
    private static readonly Regex SHA256 = new("^[0-9a-f]{64}$");
    private static readonly Regex COMMIT = new("^[0-9a-f]{40}$");
    private static readonly Regex PACKAGE = new(@"^[A-Za-z0-9][A-Za-z0-9._-]*$");
    private static readonly Regex PROPERTY = new(@"^[A-Za-z_][A-Za-z0-9_]*=[^;""]*$");

    #endregion

    #region Functions

    /// <summary>
    /// Every base application of the catalog, by identity.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <returns>The entries, ordered by identity.</returns>
    /// <exception cref="InvalidDataException">An entry is not valid.</exception>
    public static async Task<IReadOnlyList<AppEntry>> ReadAsync(string root)
    {
        var entries = new List<AppEntry>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, DIRECTORY), "*.json").Order(StringComparer.Ordinal))
            entries.Add(Parse(await File.ReadAllTextAsync(file), Path.GetFileNameWithoutExtension(file)));
        return entries;
    }

    /// <summary>
    /// One base application of the catalog.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="id">Its identity, the file's name.</param>
    /// <returns>The entry.</returns>
    /// <exception cref="InvalidDataException">The entry is not valid.</exception>
    public static async Task<AppEntry> ReadAsync(string root, string id) =>
        Parse(await File.ReadAllTextAsync(Path.Combine(root, DIRECTORY, id + ".json")), id);

    /// <summary>
    /// Reads and checks an entry: the identity its file names, a version, the framework, a source of one of the three
    /// kinds with what that kind needs, an optional native part at a commit, and at least one command, each an assembly
    /// of the application's directory.
    /// </summary>
    /// <param name="json">The entry's text.</param>
    /// <param name="fileName">The file's name less its extension, which must be the identity.</param>
    /// <returns>The entry.</returns>
    /// <exception cref="InvalidDataException">The entry is not valid.</exception>
    public static AppEntry Parse(string json, string fileName)
    {
        var entry = JsonSerializer.Deserialize<AppEntry>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException($"{fileName}: no entry.");
        Require(entry.Id is not null && IDENTITY.IsMatch(entry.Id) && entry.Id == fileName, fileName, "the identity names the file");
        Require(entry.Version is not null && VERSION.IsMatch(entry.Version), fileName, "a version of two to four numbers");
        Require(entry.Framework is not null && FRAMEWORK.IsMatch(entry.Framework), fileName, "the framework Microsoft.NETCore.App and its version");
        Require(!string.IsNullOrWhiteSpace(entry.Purpose), fileName, "a purpose");
        var source = entry.Source ?? throw new InvalidDataException($"{fileName}: no source.");
        switch (source.Kind)
        {
            case ARCHIVE:
                Require(source.Url is not null && source.Url.StartsWith("https://", StringComparison.Ordinal) && source.File is not null &&
                    source.Url.EndsWith("/" + source.File, StringComparison.Ordinal), fileName, "an archive's https URL ending with its file");
                Require(source.Sha256 is not null && SHA256.IsMatch(source.Sha256), fileName, "the archive's SHA-256");
                break;
            case TOOL:
                Require(source.Package is not null && PACKAGE.IsMatch(source.Package), fileName, "a tool package's identity");
                Require(source.Sha256 is not null && SHA256.IsMatch(source.Sha256), fileName, "the package's SHA-256");
                break;
            case SOURCE:
                Require(source.Repository is not null && source.Repository.StartsWith("https://", StringComparison.Ordinal), fileName, "a repository's https URL");
                Require(source.Commit is not null && COMMIT.IsMatch(source.Commit), fileName, "a commit");
                Require(source.Project is not null && source.Project.EndsWith(".csproj", StringComparison.Ordinal) && Relative(source.Project), fileName,
                    "a project within the repository");
                Require((source.Properties ?? []).All(property => PROPERTY.IsMatch(property)), fileName, "MSBuild properties as Name=Value");
                break;
            default:
                throw new InvalidDataException($"{fileName}: the source's kind is none of {ARCHIVE}, {TOOL} and {SOURCE}.");
        }
        if (entry.Native is { } native)
        {
            Require(native.Repository is not null && native.Repository.StartsWith("https://", StringComparison.Ordinal), fileName, "the native part's repository");
            Require(native.Commit is not null && COMMIT.IsMatch(native.Commit), fileName, "the native part's commit");
            Require(native.Library is not null && native.Library.EndsWith(".so", StringComparison.Ordinal) && Relative(native.Library), fileName,
                "the native library's name");
        }
        Require(entry.Commands is { Count: > 0 }, fileName, "at least one command");
        foreach (var command in entry.Commands!)
        {
            Require(command.Name is not null && COMMAND.IsMatch(command.Name), fileName, "a command's name in lower case");
            Require(command.Assembly is not null && command.Assembly.EndsWith(".dll", StringComparison.Ordinal) && Relative(command.Assembly), fileName,
                $"the assembly of {command.Name} within the application's directory");
        }
        Require(entry.Commands!.Select(command => command.Name).Distinct(StringComparer.Ordinal).Count() == entry.Commands!.Count, fileName,
            "distinct commands");
        return entry;
    }

    private static bool Relative(string path) =>
        !Path.IsPathRooted(path) && !path.Contains('\\') && path.Split('/').All(part => part.Length > 0 && part != "." && part != "..");

    private static void Require(bool condition, string fileName, string what)
    {
        if (!condition)
            throw new InvalidDataException($"{fileName}: the catalog entry needs {what}.");
    }

    #endregion
}

/// <summary>
/// A base application of the catalog (ADR 0027).
/// </summary>
/// <param name="Id">Its identity, the catalog file's name and the layer's directory.</param>
/// <param name="Version">The version the catalog pins.</param>
/// <param name="Framework">The shared framework it runs on.</param>
/// <param name="Source">What its upstream publishes.</param>
/// <param name="Native">The native part built from source, if any.</param>
/// <param name="Commands">The commands it provides.</param>
/// <param name="Purpose">Why it is pinned.</param>
internal sealed record AppEntry(string Id, string Version, string Framework, AppSource Source, AppNative? Native,
    IReadOnlyList<AppCommand> Commands, string Purpose);

/// <summary>
/// An upstream publication: an archive (Url, File, Sha256), a tool package (Package, Sha256) or a commit (Repository,
/// Commit, Project, and the MSBuild Properties its publish takes).
/// </summary>
internal sealed record AppSource(string Kind, string? Url, string? File, string? Sha256, string? Package, string? Repository,
    string? Commit, string? Project, IReadOnlyList<string>? Properties = null);

/// <summary>
/// A native part built from source against the sysroot: its repository, commit, tag and library.
/// </summary>
internal sealed record AppNative(string Repository, string Commit, string? Tag, string Library);

/// <summary>
/// A command of a base application: its name and the assembly its launcher starts.
/// </summary>
internal sealed record AppCommand(string Name, string Assembly);
