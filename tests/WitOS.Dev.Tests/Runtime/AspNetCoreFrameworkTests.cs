using System.Text.Json;
using WitOS.Dev.Kernel;
using WitOS.Dev.Runtime;
using WitOS.Dev.Tests.Support;

namespace WitOS.Dev.Tests.Runtime;

/// <summary>
/// The ASP.NET Core shared framework in the install (plan step W3.2): the pin names the dotnet/aspnetcore tag of the
/// release that pairs with the runtime's pin, with the submodule its build needs; a build is laid in the install under
/// dotnet/shared/Microsoft.AspNetCore.App/&lt;version&gt; with its runtimeconfig and a deps.json for the architecture, and
/// a build of another commit or for another runtime is refused.
/// </summary>
[TestFixture]
public sealed class AspNetCoreFrameworkTests
{
    #region Functions

    [Test]
    public void PinPairsWithTheRuntimesTest()
    {
        var pin = AspNetCoreFramework.ReadPin(TestEnvironment.Root);
        var runtime = RuntimeWitos.ReadLock(TestEnvironment.Root);
        Assert.That(pin.Repository, Is.EqualTo("https://github.com/dotnet/aspnetcore"));
        Assert.That(pin.Tag, Is.EqualTo($"v{pin.Version}"));
        Assert.That(pin.Version, Is.EqualTo(runtime.ProductVersion), "the framework's release pairs with the runtime's");
        Assert.That(pin.Branch, Is.EqualTo(runtime.Branch));
        Assert.That(pin.Commit, Does.Match("^[0-9a-f]{40}$"));
        Assert.That(pin.Submodules.Keys, Is.EqualTo(new[] { "src/submodules/MessagePack-CSharp" }));
        Assert.That(pin.License, Is.EqualTo(AspNetCoreFramework.LICENSE));
        Assert.That(pin.Purpose, Does.Contain("riscv64"));
    }

    [Test]
    public async Task PinOfAnotherRepositoryOrTagIsRefusedTest()
    {
        var root = TestEnvironment.Scratch();
        Directory.CreateDirectory(Path.Combine(root, "build", "runtime"));
        var lock_ = Path.Combine(root, AspNetCoreFramework.LOCK);
        var text = await File.ReadAllTextAsync(Path.Combine(TestEnvironment.Root, AspNetCoreFramework.LOCK));
        await File.WriteAllTextAsync(lock_, text.Replace("github.com/dotnet/aspnetcore", "github.com/example/aspnetcore"));
        Assert.That(() => AspNetCoreFramework.ReadPin(root), Throws.TypeOf<InvalidDataException>());
        await File.WriteAllTextAsync(lock_, text.Replace("\"tag\": \"v", "\"tag\": \"release-"));
        Assert.That(() => AspNetCoreFramework.ReadPin(root), Throws.TypeOf<InvalidDataException>());
        await File.WriteAllTextAsync(lock_, text);
        Assert.That(AspNetCoreFramework.ReadPin(root).Version, Is.EqualTo(AspNetCoreFramework.ReadPin(TestEnvironment.Root).Version));
    }

    [Test]
    public async Task FrameworkFilesLayTheBuildWithItsDepsTest()
    {
        var pin = AspNetCoreFramework.ReadPin(TestEnvironment.Root);
        var runtime = RuntimeWitos.ReadLock(TestEnvironment.Root);
        var root = TestEnvironment.Scratch();
        Directory.CreateDirectory(Path.Combine(root, "build", "runtime"));
        File.Copy(Path.Combine(TestEnvironment.Root, AspNetCoreFramework.LOCK), Path.Combine(root, AspNetCoreFramework.LOCK));
        File.Copy(Path.Combine(TestEnvironment.Root, RuntimeWitos.LOCK), Path.Combine(root, RuntimeWitos.LOCK));
        var output = Path.Combine(root, "output");
        // Not built yet.
        Assert.That(async () => await AspNetCoreFramework.FrameworkFilesAsync(root, output, KernelArchitecture.X64),
            Throws.TypeOf<InvalidOperationException>().With.Message.Contains("runtime-witos"));
        // A build: an IL-only assembly of this test's own output stands for the framework's, with the build's files.
        var directory = Path.Combine(root, "artifacts", "aspnetcore", pin.Version);
        var lib = Path.Combine(directory, "lib");
        Directory.CreateDirectory(lib);
        var assembly = Path.Combine(AppContext.BaseDirectory, "WitOS.Dev.dll");
        File.Copy(assembly, Path.Combine(lib, "Microsoft.AspNetCore.Http.dll"));
        File.Copy(assembly, Path.Combine(lib, "Microsoft.AspNetCore.dll"));
        await File.WriteAllTextAsync(Path.Combine(lib, "Microsoft.AspNetCore.App.runtimeconfig.json"),
            $$$$"""{"runtimeOptions": {"tfm": "net10.0", "rollForward": "LatestPatch", "framework": {"name": "Microsoft.NETCore.App", "version": "{{{{runtime.ProductVersion}}}}"}}}""");
        await File.WriteAllTextAsync(Path.Combine(directory, "versions.txt"), $"{pin.Commit}\n{pin.Version}\n");
        Assert.That(AspNetCoreFramework.IsIlOnly(assembly), Is.True, "the test's assembly stands for an IL-only one");
        var files = await AspNetCoreFramework.FrameworkFilesAsync(root, output, KernelArchitecture.Riscv64);
        var prefix = $"dotnet/shared/Microsoft.AspNetCore.App/{pin.Version}/";
        Assert.That(files.Select(file => file.Name), Is.EqualTo(new[]
        {
            prefix + "Microsoft.AspNetCore.Http.dll", prefix + "Microsoft.AspNetCore.dll",
            prefix + "Microsoft.AspNetCore.App.runtimeconfig.json", prefix + "Microsoft.AspNetCore.App.deps.json"
        }));
        using var deps = JsonDocument.Parse(await File.ReadAllTextAsync(files[^1].Source));
        var target = deps.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString();
        Assert.That(target, Is.EqualTo(".NETCoreApp,Version=v10.0/witos-riscv64"));
        var library = deps.RootElement.GetProperty("targets").GetProperty(target!).EnumerateObject().Single();
        Assert.That(library.Name, Is.EqualTo($"Microsoft.AspNetCore.App.Runtime.witos-riscv64/{pin.Version}"));
        Assert.That(library.Value.GetProperty("runtime").EnumerateObject().Select(property => property.Name),
            Is.EqualTo(new[] { "Microsoft.AspNetCore.Http.dll", "Microsoft.AspNetCore.dll" }));
        Assert.That(deps.RootElement.GetProperty("libraries").GetProperty(library.Name).GetProperty("path").GetString(),
            Is.EqualTo($"microsoft.aspnetcore.app.runtime.witos-riscv64/{pin.Version}"));
        // A build of another commit, or one that names another runtime, is refused.
        await File.WriteAllTextAsync(Path.Combine(directory, "versions.txt"), $"{new string('0', 40)}\n{pin.Version}\n");
        Assert.That(async () => await AspNetCoreFramework.FrameworkFilesAsync(root, output, KernelArchitecture.X64), Throws.TypeOf<InvalidDataException>());
        await File.WriteAllTextAsync(Path.Combine(directory, "versions.txt"), $"{pin.Commit}\n{pin.Version}\n");
        await File.WriteAllTextAsync(Path.Combine(lib, "Microsoft.AspNetCore.App.runtimeconfig.json"),
            """{"runtimeOptions": {"tfm": "net10.0", "framework": {"name": "Microsoft.NETCore.App", "version": "9.0.0"}}}""");
        Assert.That(async () => await AspNetCoreFramework.FrameworkFilesAsync(root, output, KernelArchitecture.X64), Throws.TypeOf<InvalidDataException>());
    }

    #endregion
}
