namespace WitOS.Dev.Kernel;

/// <summary>
/// Expected serial output of one boot suite from tests/Expectations/&lt;name&gt;.json.
/// </summary>
/// <param name="Description">What the markers prove.</param>
/// <param name="Markers">Markers that must appear in this order.</param>
/// <param name="Required">Markers that must appear in any order.</param>
internal sealed record BootExpectation(string Description, string[] Markers, string[] Required);
