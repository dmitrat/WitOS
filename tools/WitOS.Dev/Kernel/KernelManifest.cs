using System.Text.Json;

namespace WitOS.Dev.Kernel;

/// <summary>
/// Reads kernel targets and layers from build/; the source lists live there, not in the tool.
/// </summary>
internal static class KernelManifest
{
    #region Constants

    /// <summary>
    /// Status of a target that can be built.
    /// </summary>
    public const string ACTIVE = "active";

    #endregion

    #region Fields

    private static readonly JsonSerializerOptions JSON = new(JsonSerializerDefaults.Web);

    #endregion

    #region Functions

    /// <summary>
    /// Reads build/kernel-&lt;architecture&gt;.json.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="architecture">Architecture name, such as x64.</param>
    /// <returns>The target.</returns>
    /// <exception cref="InvalidDataException">The manifest is missing required fields.</exception>
    public static KernelTarget ReadTarget(string root, string architecture)
    {
        var path = Path.Combine(root, "build", $"kernel-{architecture}.json");
        var target = JsonSerializer.Deserialize<KernelTarget>(File.ReadAllText(path), JSON);
        if (target is null || target.Architecture != architecture || target.Includes is null || target.Layers is null ||
            target.SelfTestLayers is null || target.Status is null)
        {
            throw new InvalidDataException($"Invalid kernel target manifest: {path}");
        }
        return target;
    }

    /// <summary>
    /// Reads build/layers/&lt;name&gt;.json.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="name">Layer name.</param>
    /// <returns>The layer.</returns>
    /// <exception cref="InvalidDataException">The manifest is missing required fields.</exception>
    public static KernelLayer ReadLayer(string root, string name)
    {
        var path = Path.Combine(root, "build", "layers", name + ".json");
        var layer = JsonSerializer.Deserialize<KernelLayer>(File.ReadAllText(path), JSON);
        if (layer is null || layer.Name != name || layer.Includes is null || layer.Sources is null || layer.Assembly is null)
        {
            throw new InvalidDataException($"Invalid kernel layer manifest: {path}");
        }
        return layer;
    }

    /// <summary>
    /// Reads the layers a kernel of this target links, in link order.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="target">Kernel target.</param>
    /// <param name="selfTest">Whether to add the self-test layers.</param>
    /// <returns>The layers.</returns>
    /// <exception cref="InvalidOperationException">The target is only a template.</exception>
    public static IReadOnlyList<KernelLayer> ReadLayers(string root, KernelTarget target, bool selfTest)
    {
        if (target.Status != ACTIVE)
        {
            throw new InvalidOperationException($"Kernel target {target.Architecture} is a {target.Status}, not buildable yet.");
        }
        var names = selfTest ? target.Layers.Concat(target.SelfTestLayers) : target.Layers;
        return names.Select(name => ReadLayer(root, name)).ToList();
    }

    #endregion
}
