using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace RepresentativeLibraries;

/// <summary>
/// Roslyn: C# parsed, analyzed and compiled in memory against the running framework's assemblies, the program loaded
/// into a collectible context and run, and a compilation error reported as a diagnostic.
/// </summary>
public sealed class RoslynTests
{
    private const string PROGRAM = """
        using System.Linq;
        public static class Generated
        {
            public static int SumOfSquares(int count) => Enumerable.Range(1, count).Select(i => i * i).Sum();
        }
        """;

    [Fact]
    public void CompileAndRunTest()
    {
        var compilation = Compile(PROGRAM);
        using var image = new MemoryStream();

        var emitted = compilation.Emit(image, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        image.Position = 0;
        var context = new AssemblyLoadContext("generated", isCollectible: true);
        try
        {
            var method = context.LoadFromStream(image).GetType("Generated")!.GetMethod("SumOfSquares")!;
            Assert.Equal(385, (int)method.Invoke(null, [10])!);
        }
        finally
        {
            context.Unload();
        }
    }

    [Fact]
    public void SemanticModelTest()
    {
        var compilation = Compile(PROGRAM);
        var tree = compilation.SyntaxTrees.Single();
        var model = compilation.GetSemanticModel(tree);
        var invocation = tree.GetRoot(TestContext.Current.CancellationToken).DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>().First();

        var symbol = model.GetSymbolInfo(invocation, TestContext.Current.CancellationToken).Symbol as IMethodSymbol;

        Assert.NotNull(symbol);
        Assert.Equal("Sum", symbol.Name);
    }

    [Fact]
    public void DiagnosticsTest()
    {
        var compilation = Compile("public static class Broken { public static int Value() => \"text\"; }");

        var errors = compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();

        Assert.Contains(errors, error => error.Id == "CS0029");
    }

    // A library compilation of the source against every assembly of the running framework.
    private static CSharpCompilation Compile(string source)
    {
        var platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(path => Path.GetFileName(path).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(path) == "netstandard.dll")
            .Select(path => MetadataReference.CreateFromFile(path));
        return CSharpCompilation.Create("Generated", [CSharpSyntaxTree.ParseText(source)], platform,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
