namespace WitOS.Dev.Kernel;

/// <summary>
/// One kernel target from build/kernel-&lt;architecture&gt;.json: the layers of a release kernel and the self-test
/// layers added to test kernels.
/// </summary>
/// <param name="Architecture">Architecture name, such as x64.</param>
/// <param name="Status">active for buildable targets; template for planned ones.</param>
/// <param name="Description">Board and purpose of the target.</param>
/// <param name="Includes">Include directories of every kernel source, in search order.</param>
/// <param name="Layers">Layers of the release kernel, in link order.</param>
/// <param name="SelfTestLayers">Layers linked after them into WITOS_SELFTEST kernels; empty while the target has none.</param>
/// <param name="Defines">Preprocessor symbols of every kernel source, such as the WITOS_BOOT_ONLY profile; may be absent.</param>
internal sealed record KernelTarget(string Architecture, string Status, string Description, string[] Includes,
    string[] Layers, string[] SelfTestLayers, string[]? Defines);
