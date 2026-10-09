namespace WitOS.Dev.Kernel;

/// <summary>
/// One kernel layer from build/layers/&lt;name&gt;.json: its sources in link order and extra include directories.
/// </summary>
/// <param name="Name">Layer name; also the prefix of its object files.</param>
/// <param name="Description">What the layer contains.</param>
/// <param name="Includes">Include directories added for this layer's C sources only.</param>
/// <param name="Sources">Repository-relative C sources in link order.</param>
/// <param name="Assembly">Repository-relative assembly sources in link order, GNU syntax (.S) for clang's integrated
/// assembler (plan step T3).</param>
internal sealed record KernelLayer(string Name, string Description, string[] Includes, string[] Sources, string[] Assembly);
