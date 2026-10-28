using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WitOS.Apps.Tests;

/// <summary>
/// A version's native code (plan step R9.2): which assemblies crossgen2 compiles and with which references, and the
/// compilation into native-&lt;arch&gt;/ through a stand-in for crossgen2 — each image put in place of its assembly, an
/// input that fails alone kept as IL, the marker last, and nothing left of a failed compilation.
/// </summary>
[TestFixture]
public sealed class NativeCodeTests
{
    #region Constants

    private const string ARCHITECTURE = "x64";

    private const string BAD = "bad.dll";

    #endregion

    #region Fields

    private DirectoryInfo m_scratch = null!;

    #endregion

    #region Functions

    [SetUp]
    public void SetUp() => m_scratch = Directory.CreateTempSubdirectory("witos-native-");

    [TearDown]
    public void TearDown() => m_scratch.Delete(recursive: true);

    [Test]
    public void AssembliesAreClassifiedByTheirMetadataTest()
    {
        var directory = m_scratch.FullName;
        Assert.That(NativeCode.Classify(WriteAssembly(directory, "code.dll", code: true)), Is.EqualTo(AssemblyKind.Compiled));
        Assert.That(NativeCode.Classify(WriteAssembly(directory, "empty.dll", code: false)), Is.EqualTo(AssemblyKind.NoCode), "no method to compile");
        Assert.That(NativeCode.Classify(WriteAssembly(directory, "code.resources.dll", code: true)), Is.EqualTo(AssemblyKind.NoCode), "a satellite");
        Assert.That(NativeCode.Classify(WriteAssembly(directory, "contract.dll", code: true, reference: true)), Is.EqualTo(AssemblyKind.Reference));
        Assert.That(NativeCode.Classify(typeof(object).Assembly.Location), Is.EqualTo(AssemblyKind.Native), "the host's CoreLib is ReadyToRun");
        var text = Path.Combine(directory, "native.dll");
        File.WriteAllText(text, "not a PE file");
        Assert.That(NativeCode.Classify(text), Is.EqualTo(AssemblyKind.NotManaged));
    }

    [Test]
    public void PlanGroupsEachDirectoryWithItsReferencesTest()
    {
        var version = LayVersion();
        var framework = Path.Combine(m_scratch.FullName, "framework");
        var plan = NativeCode.Plan(version, [framework]);
        Assert.That(plan.Select(group => group.Directory), Is.EqualTo(new[] { "", "Modules/M", "runtimes/unix/lib/net10.0" }),
            "a directory a compilation; no reference assemblies, no other system's runtimes");
        Assert.That(plan[0].Inputs, Is.EqualTo(new[] { "a.dll", "b.dll", BAD }), "satellites, facades and native files stay out");
        Assert.That(plan[1].Inputs, Is.EqualTo(new[] { "Modules/M/m.dll" }));
        Assert.That(plan[0].References, Is.EqualTo(new[]
        {
            Pattern(version), Pattern(framework), Pattern(Path.Combine(version, "Modules", "M")),
            Pattern(Path.Combine(version, "runtimes", "unix", "lib", "net10.0"))
        }), "the group's own directory first, then the framework, then the rest; never ref/ or runtimes/win");
        Assert.That(plan[1].References.Take(3), Is.EqualTo(new[] { Pattern(Path.Combine(version, "Modules", "M")), Pattern(framework), Pattern(version) }));
    }

    [Test]
    public async Task CompilationReplacesEachAssemblyAndKeepsWhatFailsTest()
    {
        var version = LayVersion();
        Directory.CreateDirectory(Path.Combine(version, "native-arm64"));
        File.WriteAllText(Path.Combine(version, "native-arm64", "other.txt"), "another architecture's");
        var responses = new List<string[]>();
        Assert.That(NativeCode.IsCompiled(version, ARCHITECTURE), Is.False);

        var steps = new List<string>();
        var report = await NativeCode.CompileAsync(version, ARCHITECTURE, [Path.Combine(m_scratch.FullName, "framework")],
            (response, _) => Task.FromResult(Crossgen2(response, responses)), steps.Add);

        var native = Path.Combine(version, NativeCode.DirectoryName(ARCHITECTURE));
        Assert.That(NativeCode.IsCompiled(version, ARCHITECTURE), Is.True);
        Assert.That(report.Compiled, Is.EqualTo(new[] { "Modules/M/m.dll", "a.dll", "b.dll", "runtimes/unix/lib/net10.0/u.dll" }));
        Assert.That(report.Failed, Is.EqualTo(new[] { BAD }), "the input crossgen2 refused alone keeps its IL");
        Assert.That(NativeCode.ReadMarker(native), Is.EqualTo(report).Using<NativeReport>(Same));
        Assert.That(File.ReadAllText(Path.Combine(native, "a.dll")), Is.EqualTo("image of a.dll"));
        Assert.That(File.ReadAllText(Path.Combine(native, "Modules", "M", "m.dll")), Is.EqualTo("image of m.dll"));
        Assert.That(File.ReadAllBytes(Path.Combine(native, BAD)), Is.EqualTo(File.ReadAllBytes(Path.Combine(version, BAD))));
        Assert.That(File.ReadAllBytes(Path.Combine(native, "ref", "r.dll")), Is.EqualTo(File.ReadAllBytes(Path.Combine(version, "ref", "r.dll"))),
            "every other file is copied unchanged");
        Assert.That(File.ReadAllText(Path.Combine(native, "lib", "data.txt")), Is.EqualTo("data"));
        Assert.That(Directory.Exists(Path.Combine(native, "native-arm64")), Is.False, "no native directory inside another");
        Assert.That(Directory.EnumerateFiles(native, "*", SearchOption.AllDirectories).Select(Path.GetFileName),
            Has.None.EndsWith(".ni.dll").And.None.EndsWith(".rsp"), "no compiler output or response file is left");
        Assert.That(Directory.EnumerateDirectories(version).Select(Path.GetFileName), Has.None.StartsWith("."), "no partial directory is left");

        var first = responses[0];
        Assert.That(first, Does.Contain("--targetos:witos").And.Contain("--targetarch:x64").And.Contain("-O")
            .And.Contain("--single-file-compilation").And.Contain("--out-near-input"));
        Assert.That(first.Where(token => token.StartsWith("-r:", StringComparison.Ordinal)), Has.Exactly(4).Items);
        Assert.That(steps, Has.Some.StartsWith("Compiling .: 3 assemblies").And.Some.EqualTo("Compiled 4 assemblies, 1 kept as IL"));

        // The derived directory is made anew at will.
        File.WriteAllText(Path.Combine(native, "stale.txt"), "stale");
        await NativeCode.CompileAsync(version, ARCHITECTURE, [], (response, _) => Task.FromResult(Crossgen2(response, responses)));
        Assert.That(File.Exists(Path.Combine(native, "stale.txt")), Is.False);
    }

    [Test]
    public void FailedCompilationLeavesNothingTest()
    {
        var version = LayVersion();
        Assert.ThrowsAsync<InvalidOperationException>(() => NativeCode.CompileAsync(version, ARCHITECTURE, [],
            (_, _) => throw new InvalidOperationException("crossgen2 is missing")));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.CatchAsync<OperationCanceledException>(() => NativeCode.CompileAsync(version, ARCHITECTURE, [],
            (response, _) => Task.FromResult(Crossgen2(response, [])), cancellationToken: cancellation.Token));
        Assert.That(Directory.EnumerateDirectories(version).Select(Path.GetFileName), Has.None.StartsWith(".").And.None.StartsWith(NativeCode.PREFIX));
        Assert.That(NativeCode.IsCompiled(version, ARCHITECTURE), Is.False);
    }

    // A version's files: assemblies at the top, a module's, RID-specific ones of two systems, a reference assembly, a
    // satellite, a facade, a native file and data.
    private string LayVersion()
    {
        var version = Path.Combine(m_scratch.FullName, "version");
        WriteAssembly(version, "a.dll", code: true);
        WriteAssembly(version, "b.dll", code: true);
        WriteAssembly(version, BAD, code: true);
        WriteAssembly(version, "a.resources.dll", code: true);
        WriteAssembly(version, "facade.dll", code: false);
        WriteAssembly(Path.Combine(version, "ref"), "r.dll", code: true, reference: true);
        WriteAssembly(Path.Combine(version, "Modules", "M"), "m.dll", code: true);
        WriteAssembly(Path.Combine(version, "runtimes", "unix", "lib", "net10.0"), "u.dll", code: true);
        WriteAssembly(Path.Combine(version, "runtimes", "win", "lib", "net10.0"), "w.dll", code: true);
        File.WriteAllText(Path.Combine(version, "native.dll"), "a native library");
        Directory.CreateDirectory(Path.Combine(version, "lib"));
        File.WriteAllText(Path.Combine(version, "lib", "data.txt"), "data");
        return version;
    }

    // crossgen2's stand-in: reads its response file one token a line, refuses several inputs among which is bad.dll (an
    // image of the first written already, as a compilation that stops midway leaves) and bad.dll alone, and otherwise
    // writes each input's image beside it.
    private static int Crossgen2(string response, List<string[]> responses)
    {
        var tokens = File.ReadAllLines(response);
        responses.Add(tokens);
        var inputs = tokens.Where(token => !token.StartsWith('-')).ToList();
        if (inputs.Any(input => Path.GetFileName(input) == BAD))
        {
            if (inputs.Count > 1)
                File.WriteAllText(Path.ChangeExtension(inputs[0], ".ni.dll"), "partial");
            return 1;
        }
        foreach (var input in inputs)
            File.WriteAllText(Path.ChangeExtension(input, ".ni.dll"), "image of " + Path.GetFileName(input));
        return 0;
    }

    private static bool Same(NativeReport x, NativeReport y) =>
        x.Architecture == y.Architecture && x.Compiled.SequenceEqual(y.Compiled) && x.Failed.SequenceEqual(y.Failed);

    private static string Pattern(string directory) => Path.GetFullPath(directory).TrimEnd('/', '\\') + "/*.dll";

    // An assembly of IL: a public type with one static method unless it has no code, marked a reference assembly when asked.
    private static string WriteAssembly(string directory, string file, bool code, bool reference = false)
    {
        Directory.CreateDirectory(directory);
        var name = Path.GetFileNameWithoutExtension(file);
        var assembly = new PersistedAssemblyBuilder(new AssemblyName(name), typeof(object).Assembly);
        if (reference)
            assembly.SetCustomAttribute(new CustomAttributeBuilder(typeof(ReferenceAssemblyAttribute).GetConstructor(Type.EmptyTypes)!, []));
        var type = assembly.DefineDynamicModule(name).DefineType("Sample", TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed);
        if (code)
        {
            var il = type.DefineMethod("Answer", MethodAttributes.Public | MethodAttributes.Static, typeof(int), Type.EmptyTypes).GetILGenerator();
            il.Emit(OpCodes.Ldc_I4, 42);
            il.Emit(OpCodes.Ret);
        }
        type.CreateType();
        var path = Path.Combine(directory, file);
        assembly.Save(path);
        return path;
    }

    #endregion
}
