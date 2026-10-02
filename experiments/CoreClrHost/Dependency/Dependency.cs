namespace WitOS.HostBindingDependency;

/// <summary>
/// Ordinary dependency library that the host binding probe resolves through its deps.json.
/// </summary>
public static class Dependency
{
    #region Functions

    /// <summary>
    /// Computes a fixed value from LINQ over a small array.
    /// </summary>
    /// <returns>The sum of the squares of 2, 3 and 5, plus 4.</returns>
    public static int Compute() => new[] { 2, 3, 5 }.Select(value => value * value).Sum() + 4;

    #endregion
}
