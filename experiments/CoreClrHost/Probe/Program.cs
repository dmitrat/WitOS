using System.Diagnostics;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using WitOS.HostBindingDependency;
static void Require(bool value, string label)
{
    if (!value)
        throw new InvalidOperationException("Hosting reference failed: " + label);
    Console.WriteLine("[HOST-BINDING-PASS] " + label);
}
Require(Dependency.Compute() == 42, "DependencyFromDeps");
var generated = new DynamicMethod("Answer", typeof(int), Type.EmptyTypes);
var il = generated.GetILGenerator();
il.Emit(OpCodes.Ldc_I4, 742);
il.Emit(OpCodes.Ret);
Require(RuntimeFeature.IsDynamicCodeSupported && RuntimeFeature.IsDynamicCodeCompiled && generated.CreateDelegate<Func<int>>()() == 742, "ActualJit");
var root = Environment.GetEnvironmentVariable("WITOS_HOST_REFERENCE") ?? throw new InvalidOperationException("Missing reference root.");
var version = Environment.GetEnvironmentVariable("WITOS_HOST_VERSION") ?? throw new InvalidOperationException("Missing reference version.");
var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["dotnet.exe"] = Path.Combine(root, "dotnet.exe"),
    ["hostfxr.dll"] = Path.Combine(root, "host/fxr", version, "hostfxr.dll"),
    ["hostpolicy.dll"] = Path.Combine(root, "shared/Microsoft.NETCore.App", version, "hostpolicy.dll"),
    ["coreclr.dll"] = Path.Combine(root, "shared/Microsoft.NETCore.App", version, "coreclr.dll"),
    ["clrjit.dll"] = Path.Combine(root, "shared/Microsoft.NETCore.App", version, "clrjit.dll")
};
using var process = Process.GetCurrentProcess();
var modules = process.Modules.Cast<ProcessModule>().ToArray();
foreach (var (name, path) in expected)
{
    var actual = modules.Single(module => string.Equals(module.ModuleName, name, StringComparison.OrdinalIgnoreCase));
    Require(string.Equals(Path.GetFullPath(actual.FileName), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase), "SourceModule:" + name);
}
Console.WriteLine("[HOST-BINDING-SUCCESS]");
return 42;
