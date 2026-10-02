using WitOS.Dev;

// Repository consistency checks: documentation, manifests and headers must agree.
internal static class ConsistencyTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases(string root)
    {
        yield return ("FormatManifestDescribesTree", () => FormatManifestAsync(root));
    }

    private static async Task FormatManifestAsync(string root)
    {
        var manifest = await SourceFormat.ReadManifestAsync(root);
        var native = SourceFormat.Expand(root, manifest.Native, [".c", ".h", ".cpp"], manifest.Exclude);
        var managed = SourceFormat.Expand(root, manifest.Managed, [".cs"], manifest.Exclude);
        Check(native.All(file => !file.EndsWith(".asm", StringComparison.Ordinal)), "Assembly entered the C formatter");
        Check(managed.All(file => file.EndsWith(".cs", StringComparison.Ordinal)), "Non-C# file entered the C# formatter");
        Check(native.Concat(managed).All(file => !file.Contains("/obj/") && !file.Contains("/bin/")), "Generated output");

        var missing = false;
        try
        {
            SourceFormat.Expand(root, ["src/DoesNotExist"], [".c"], []);
        }
        catch (InvalidDataException)
        {
            missing = true;
        }
        Check(missing, "A missing manifest entry was accepted");
    }

    private static void Check(bool value, string why)
    {
        if (!value)
        {
            throw new InvalidOperationException(why);
        }
    }
}
