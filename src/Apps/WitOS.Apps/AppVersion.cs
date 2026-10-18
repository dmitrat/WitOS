using System;
using System.Collections.Generic;
using System.Linq;

namespace WitOS.Apps;

/// <summary>
/// Versions of the catalog's form, two to four numbers, ordered by their numbers: 2026.9.19 after 2026.9.1, and 7.6.7
/// after 7.6.
/// </summary>
public sealed class AppVersion : IComparer<string>
{
    #region Constants

    /// <summary>
    /// The order.
    /// </summary>
    public static readonly AppVersion ORDER = new();

    #endregion

    #region Functions

    /// <summary>
    /// Compares two versions by their numbers, a missing number being less than any.
    /// </summary>
    /// <param name="x">One version.</param>
    /// <param name="y">The other.</param>
    /// <returns>Less than zero when x is earlier, zero when equal, more when later.</returns>
    public int Compare(string? x, string? y)
    {
        var left = Numbers(x);
        var right = Numbers(y);
        for (var index = 0; index < Math.Max(left.Length, right.Length); index++)
        {
            if (index >= left.Length)
                return -1;
            if (index >= right.Length)
                return 1;
            var order = left[index].CompareTo(right[index]);
            if (order != 0)
                return order;
        }
        return 0;
    }

    /// <summary>
    /// The latest of some versions, or null.
    /// </summary>
    /// <param name="versions">The versions.</param>
    /// <returns>The latest.</returns>
    public static string? Latest(IEnumerable<string> versions) => versions.OrderBy(version => version, ORDER).LastOrDefault();

    private static long[] Numbers(string? version) =>
        string.IsNullOrEmpty(version) ? [] : [.. version.Split('.').Select(part => long.TryParse(part, out var number) ? number : -1)];

    #endregion
}
