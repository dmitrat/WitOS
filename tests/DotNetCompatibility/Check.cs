namespace DotNetCompatibility;

/// <summary>
/// What a check observed, and whether .NET's standard semantics held.
/// </summary>
/// <param name="Passed">Whether the API behaved as .NET documents it.</param>
/// <param name="Observed">What the check saw, for the report.</param>
internal sealed record CheckResult(bool Passed, string Observed)
{
    /// <summary>
    /// A result from a condition.
    /// </summary>
    /// <param name="condition">Whether the semantics held.</param>
    /// <param name="observed">What the check saw.</param>
    /// <returns>The result.</returns>
    public static CheckResult That(bool condition, string observed) => new(condition, observed);

    /// <summary>
    /// A result from a condition, as a finished task.
    /// </summary>
    /// <param name="condition">Whether the semantics held.</param>
    /// <param name="observed">What the check saw.</param>
    /// <returns>The result.</returns>
    public static Task<CheckResult> Now(bool condition, string observed) => Task.FromResult(That(condition, observed));
}

/// <summary>
/// One check of an area.
/// </summary>
/// <param name="Area">The area: one of Strategy §57's, or Platform.</param>
/// <param name="Name">The check's name in its area.</param>
/// <param name="Run">The check.</param>
internal sealed record Check(string Area, string Name, Func<Task<CheckResult>> Run)
{
    /// <summary>
    /// The check's identity in the report and in expectations.json.
    /// </summary>
    public string Id => $"{Area}.{Name}";
}
