using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WitOS.Apps;

/// <summary>
/// A version's native code (plan step R9.2, the author's decision of 2026-10-27): &lt;version&gt;/native-&lt;arch&gt;/, a
/// copy of the version's files in which every assembly crossgen2 compiles is replaced by its ReadyToRun image for the
/// architecture, the IL kept inside the image, as .NET Framework's NGEN kept a machine's native images beside the IL.
/// The directory is derived: written after the installation, its marker last, so that a directory without the marker
/// holds no native code; removed and made anew at will; the launchers name it while its marker is there. Which assemblies
/// are compiled and with what references is decided here, for the host's tool and the guest's installer alike.
/// </summary>
public static class NativeCode
{
    #region Constants

    /// <summary>
    /// The native directory's name before its architecture.
    /// </summary>
    public const string PREFIX = "native-";

    /// <summary>
    /// The file a native directory is complete with, written last.
    /// </summary>
    public const string MARKER = "witos-native.json";

    /// <summary>
    /// The operating system crossgen2 compiles for.
    /// </summary>
    public const string TARGET_OS = "witos";

    private const string PARTIAL = ".partial";

    // What crossgen2's --out-near-input writes beside an input X.dll.
    private const string NEAR_OUTPUT = ".ni.dll";

    private const string RESPONSE_FILE = ".crossgen2.rsp";

    private const int FAILURE_LINES = 8;

    private const string REFERENCE_ASSEMBLY_ATTRIBUTE = "ReferenceAssemblyAttribute";

    private const string COMPILER_SERVICES = "System.Runtime.CompilerServices";

    // The RID-specific assets a witos application loads: the RID graph gives witos-<arch> the parents witos and unix (R1.3).
    private static readonly string[] SERVED_RUNTIMES = ["unix", "witos"];

    private static readonly JsonSerializerOptions MARKER_OPTIONS = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    #endregion

    #region Functions

    /// <summary>
    /// The native directory of an architecture, in a version's directory.
    /// </summary>
    /// <param name="architecture">The architecture's name: x64, arm64 or riscv64.</param>
    /// <returns>native-&lt;arch&gt;.</returns>
    public static string DirectoryName(string architecture) => PREFIX + architecture;

    /// <summary>
    /// Whether a version holds the native code of an architecture: its native directory with the marker.
    /// </summary>
    /// <param name="versionDirectory">The version's directory.</param>
    /// <param name="architecture">The architecture's name.</param>
    /// <returns>Whether the launchers may name the native directory.</returns>
    public static bool IsCompiled(string versionDirectory, string architecture) =>
        File.Exists(Path.Combine(versionDirectory, DirectoryName(architecture), MARKER));

    /// <summary>
    /// What crossgen2 compiles in a version's files, directory by directory: each assembly with IL code that is no
    /// ReadyToRun image, no reference assembly and no satellite of resources, outside the RID-specific directories of
    /// other systems; an assembly's references are its own directory's assemblies, then the frameworks', then the rest
    /// of the version's, the first of a name winning, as crossgen2 takes references. A directory's assemblies are one
    /// compilation, since the names of inputs must differ.
    /// </summary>
    /// <param name="directory">The files' directory.</param>
    /// <param name="frameworks">The directories of the frameworks the application runs on, Microsoft.NETCore.App's first.</param>
    /// <returns>The compilations, in the order of their directories' paths.</returns>
    public static IReadOnlyList<NativeGroup> Plan(string directory, IReadOnlyList<string> frameworks)
    {
        var assemblies = Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(directory, file).Replace('\\', '/'))
            .Where(path => IsServed(path))
            .Select(path => (Path: path, Kind: Classify(Path.Combine(directory, path))))
            .ToList();
        var referenced = assemblies.Where(item => item.Kind != AssemblyKind.NotManaged && item.Kind != AssemblyKind.Reference)
            .Select(item => Folder(item.Path)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return
        [
            .. assemblies.Where(item => item.Kind == AssemblyKind.Compiled).GroupBy(item => Folder(item.Path), StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new NativeGroup(group.Key, [.. group.Select(item => item.Path).Order(StringComparer.Ordinal)],
                [
                    Pattern(Path.Combine(directory, group.Key)),
                    .. frameworks.Select(Pattern),
                    .. referenced.Where(folder => folder != group.Key).Select(folder => Pattern(Path.Combine(directory, folder)))
                ]))
        ];
    }

    /// <summary>
    /// crossgen2's arguments for one compilation of a directory's assemblies, one token a line as its response file
    /// takes them: the inputs, the target, optimization, each input compiled by itself beside its file.
    /// </summary>
    /// <param name="directory">The files' directory.</param>
    /// <param name="group">The compilation.</param>
    /// <param name="inputs">The inputs compiled, the group's or one of them.</param>
    /// <param name="architecture">The architecture's name.</param>
    /// <returns>The tokens.</returns>
    public static IReadOnlyList<string> Arguments(string directory, NativeGroup group, IEnumerable<string> inputs, string architecture) =>
    [
        .. inputs.Select(input => Path.GetFullPath(Path.Combine(directory, input))),
        $"--targetos:{TARGET_OS}", $"--targetarch:{architecture}", "-O", "--single-file-compilation", "--out-near-input",
        .. group.References.Select(reference => $"-r:{reference}")
    ];

    /// <summary>
    /// Compiles a version: its files copied into a hidden directory beside it, every compilation of the plan run by
    /// crossgen2 through its response file, each image put in place of its assembly, the marker written, and the
    /// directory renamed to native-&lt;arch&gt; over any earlier one. A compilation that fails is run again input by input,
    /// and an input that fails alone keeps its IL. Nothing is left of a failed or cancelled compilation.
    /// </summary>
    /// <param name="versionDirectory">The version's directory.</param>
    /// <param name="architecture">The architecture's name.</param>
    /// <param name="frameworks">The frameworks' directories, Microsoft.NETCore.App's first.</param>
    /// <param name="crossgen2">Runs crossgen2 with a response file's path; returns its exit code.</param>
    /// <param name="report">Told each step, for a screen or a log.</param>
    /// <param name="cancellationToken">Cancels the compilation before the rename.</param>
    /// <returns>What was compiled.</returns>
    public static async Task<NativeReport> CompileAsync(string versionDirectory, string architecture, IReadOnlyList<string> frameworks,
        Func<string, CancellationToken, Task<int>> crossgen2, Action<string>? report = null, CancellationToken cancellationToken = default)
    {
        var name = DirectoryName(architecture);
        var target = Path.Combine(versionDirectory, name);
        var partial = Path.Combine(versionDirectory, "." + name + PARTIAL);
        if (Directory.Exists(partial))
            Directory.Delete(partial, recursive: true);
        try
        {
            Copy(versionDirectory, partial);
            var plan = Plan(partial, frameworks);
            var compiled = new List<string>();
            var failed = new List<string>();
            var at = 0;
            foreach (var group in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                report?.Invoke($"Compiling {(group.Directory.Length == 0 ? "." : group.Directory)}: {group.Inputs.Count} assemblies ({++at} of {plan.Count})");
                if (await RunAsync(partial, group, group.Inputs, architecture, crossgen2, cancellationToken))
                {
                    compiled.AddRange(group.Inputs);
                    continue;
                }
                // One input that crossgen2 cannot compile ends the compilation of the rest: each is compiled alone.
                foreach (var input in group.Inputs)
                {
                    if (await RunAsync(partial, group, [input], architecture, crossgen2, cancellationToken))
                        compiled.Add(input);
                    else
                        failed.Add(input);
                }
            }
            foreach (var input in compiled)
            {
                var file = Path.Combine(partial, input);
                File.Move(Path.ChangeExtension(file, NEAR_OUTPUT), file, overwrite: true);
            }
            var result = new NativeReport(architecture, [.. compiled.Order(StringComparer.Ordinal)], [.. failed.Order(StringComparer.Ordinal)]);
            await File.WriteAllTextAsync(Path.Combine(partial, MARKER), JsonSerializer.Serialize(result, MARKER_OPTIONS) + "\n", cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
            Directory.Move(partial, target);
            report?.Invoke($"Compiled {result.Compiled.Count} assemblies" + (result.Failed.Count == 0 ? "" : $", {result.Failed.Count} kept as IL"));
            return result;
        }
        catch
        {
            if (Directory.Exists(partial))
                Directory.Delete(partial, recursive: true);
            throw;
        }
    }

    /// <summary>
    /// crossgen2 as a process, for CompileAsync: the executable started with the response file, its output kept and, when
    /// it fails, its last lines reported.
    /// </summary>
    /// <param name="executable">crossgen2's launcher: /system/Crossgen2/crossgen2 in the guest.</param>
    /// <param name="report">Told a failure's last lines.</param>
    /// <returns>The runner.</returns>
    public static Func<string, CancellationToken, Task<int>> Crossgen2Process(string executable, Action<string>? report = null) =>
        async (response, cancellationToken) =>
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("@" + response);
            using var process = Process.Start(start) ?? throw new InvalidOperationException($"{executable} did not start.");
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0 && report is not null)
            {
                foreach (var line in ((await output) + "\n" + (await error)).Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(FAILURE_LINES))
                    report("crossgen2: " + line.TrimEnd());
            }
            return process.ExitCode;
        };

    /// <summary>
    /// Reads a native directory's marker.
    /// </summary>
    /// <param name="nativeDirectory">The native directory.</param>
    /// <returns>What was compiled.</returns>
    /// <exception cref="InvalidDataException">The marker is missing or not valid.</exception>
    public static NativeReport ReadMarker(string nativeDirectory)
    {
        var path = Path.Combine(nativeDirectory, MARKER);
        return File.Exists(path) && JsonSerializer.Deserialize<NativeReport>(File.ReadAllText(path), MARKER_OPTIONS) is { Architecture: not null } report
            ? report
            : throw new InvalidDataException($"{nativeDirectory} holds no valid {MARKER}.");
    }

    /// <summary>
    /// What an assembly is to crossgen2.
    /// </summary>
    /// <param name="file">The file.</param>
    /// <returns>Its kind.</returns>
    internal static AssemblyKind Classify(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            using var reader = new PEReader(stream);
            if (reader.PEHeaders.CorHeader is not { } cor || !reader.HasMetadata)
                return AssemblyKind.NotManaged;
            var metadata = reader.GetMetadataReader();
            if (!metadata.IsAssembly)
                return AssemblyKind.NotManaged;
            if (IsReferenceAssembly(metadata))
                return AssemblyKind.Reference;
            if (cor.ManagedNativeHeaderDirectory.Size > 0 || (cor.Flags & CorFlags.ILOnly) == 0)
                return AssemblyKind.Native;
            if (Path.GetFileName(file).EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase) || metadata.MethodDefinitions.Count == 0)
                return AssemblyKind.NoCode;
            return AssemblyKind.Compiled;
        }
        catch (BadImageFormatException)
        {
            return AssemblyKind.NotManaged;
        }
    }

    // Runs crossgen2 on inputs of a compilation; true when it exited with zero and wrote every image.
    private static async Task<bool> RunAsync(string directory, NativeGroup group, IReadOnlyList<string> inputs, string architecture,
        Func<string, CancellationToken, Task<int>> crossgen2, CancellationToken cancellationToken)
    {
        var response = Path.Combine(directory, RESPONSE_FILE);
        await File.WriteAllLinesAsync(response, Arguments(directory, group, inputs, architecture), cancellationToken);
        var code = await crossgen2(response, cancellationToken);
        File.Delete(response);
        var written = inputs.All(input => File.Exists(Path.ChangeExtension(Path.Combine(directory, input), NEAR_OUTPUT)));
        if (code == 0 && written)
            return true;
        foreach (var input in inputs)
            File.Delete(Path.ChangeExtension(Path.Combine(directory, input), NEAR_OUTPUT));
        return false;
    }

    // Copies a version's files, less its native directories and the hidden ones beside them.
    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            if (!Path.GetFileName(file).StartsWith('.'))
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }
        foreach (var directory in Directory.EnumerateDirectories(from))
        {
            var name = Path.GetFileName(directory);
            if (!name.StartsWith('.') && !name.StartsWith(PREFIX, StringComparison.Ordinal))
                CopyTree(directory, Path.Combine(to, name));
        }
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(from))
            CopyTree(directory, Path.Combine(to, Path.GetFileName(directory)));
    }

    // Whether a path lies where a witos application loads assemblies from: anywhere but runtimes/<rid>/ of another system.
    private static bool IsServed(string path)
    {
        if (!path.StartsWith("runtimes/", StringComparison.Ordinal))
            return true;
        var rid = path.Split('/')[1];
        return SERVED_RUNTIMES.Any(served => rid == served || rid.StartsWith(served + "-", StringComparison.Ordinal));
    }

    private static string Folder(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

    private static string Pattern(string directory) => Path.GetFullPath(directory).TrimEnd('/', '\\') + "/*.dll";

    private static bool IsReferenceAssembly(MetadataReader metadata)
    {
        foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
        {
            var constructor = metadata.GetCustomAttribute(handle).Constructor;
            var type = constructor.Kind switch
            {
                HandleKind.MemberReference => metadata.GetMemberReference((MemberReferenceHandle)constructor).Parent,
                HandleKind.MethodDefinition => metadata.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType(),
                _ => default
            };
            var (space, name) = type.Kind switch
            {
                HandleKind.TypeReference => (metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)type).Namespace),
                    metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)type).Name)),
                HandleKind.TypeDefinition => (metadata.GetString(metadata.GetTypeDefinition((TypeDefinitionHandle)type).Namespace),
                    metadata.GetString(metadata.GetTypeDefinition((TypeDefinitionHandle)type).Name)),
                _ => ("", "")
            };
            if (space == COMPILER_SERVICES && name == REFERENCE_ASSEMBLY_ATTRIBUTE)
                return true;
        }
        return false;
    }

    #endregion

    #region Properties

    /// <summary>
    /// The architecture of the running process, as the native directory names it.
    /// </summary>
    public static string ProcessArchitecture => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.RiscV64 => "riscv64",
        var other => other.ToString().ToLowerInvariant()
    };

    #endregion
}

/// <summary>
/// One compilation of a version's native code: a directory's assemblies and their references.
/// </summary>
/// <param name="Directory">The directory, relative to the version's, empty for the version's own.</param>
/// <param name="Inputs">The assemblies, by their paths in the version's directory.</param>
/// <param name="References">crossgen2's reference patterns, absolute: each directory's *.dll.</param>
public sealed record NativeGroup(string Directory, IReadOnlyList<string> Inputs, IReadOnlyList<string> References);

/// <summary>
/// A native directory's marker: what was compiled for an architecture.
/// </summary>
/// <param name="Architecture">The architecture's name.</param>
/// <param name="Compiled">The assemblies replaced by their images, by their paths in the version's directory.</param>
/// <param name="Failed">The assemblies crossgen2 did not compile, kept as IL.</param>
public sealed record NativeReport(string Architecture, IReadOnlyList<string> Compiled, IReadOnlyList<string> Failed);

/// <summary>
/// What an assembly is to crossgen2.
/// </summary>
public enum AssemblyKind
{
    /// <summary>No managed assembly: a native library or no PE file.</summary>
    NotManaged,

    /// <summary>A reference assembly, which carries no code a process runs.</summary>
    Reference,

    /// <summary>A ReadyToRun image already, or an image with native code of its own.</summary>
    Native,

    /// <summary>A satellite of resources or an assembly with no method.</summary>
    NoCode,

    /// <summary>An assembly crossgen2 compiles.</summary>
    Compiled
}
