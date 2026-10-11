// An ordinary .NET program (plan step R6), built by the stock SDK without a RID and run unchanged on WitOS through
// dotnet. Each check uses what an application uses every day: LINQ and generics, async code on the thread pool,
// exceptions, reflection and the code it generates at run time (expression trees, compiled regular expressions),
// System.Text.Json, parallel loops and concurrent collections, big integers and SIMD vectors. Nothing here knows WitOS.
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var checks = new (string Name, Func<Task<bool>> Run)[]
{
    ("linq", () => Task.FromResult(Enumerable.Range(1, 100).Where(n => n % 3 == 0).Select(n => n * n).Sum() == 112761)),
    ("generics", () => Task.FromResult(new Dictionary<string, List<int>> { ["a"] = [1, 2], ["b"] = [3] }.Values.Sum(list => list.Count) == 3)),
    ("async", async () =>
    {
        var values = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            await Task.Delay(10);
            return i;
        })));
        return values.Sum() == 28;
    }),
    ("exceptions", () =>
    {
        try
        {
            throw new InvalidOperationException("filtered");
        }
        catch (InvalidOperationException exception) when (exception.Message == "filtered")
        {
            return Task.FromResult(exception.StackTrace?.Contains("Program") == true);
        }
    }),
    ("reflection", () =>
    {
        var type = typeof(Point);
        var point = (Point)Activator.CreateInstance(type, 3, 4)!;
        var length = type.GetMethod(nameof(Point.Length), BindingFlags.Public | BindingFlags.Instance)!;
        return Task.FromResult((double)length.Invoke(point, null)! == 5.0);
    }),
    ("expression trees", () =>
    {
        var x = Expression.Parameter(typeof(int), "x");
        var square = Expression.Lambda<Func<int, int>>(Expression.Multiply(x, x), x).Compile();
        return Task.FromResult(square(12) == 144);
    }),
    ("compiled regex", () =>
    {
        var regex = new Regex(@"^(?<user>[a-z]+)@(?<host>[a-z.]+)$", RegexOptions.Compiled);
        var match = regex.Match("dotnet@witos.example");
        return Task.FromResult(match.Success && match.Groups["host"].Value == "witos.example");
    }),
    ("json", () =>
    {
        var json = JsonSerializer.Serialize(new Point(1, 2));
        var back = JsonSerializer.Deserialize<Point>(json)!;
        return Task.FromResult(json == "{\"X\":1,\"Y\":2}" && back.X == 1 && back.Y == 2);
    }),
    ("parallel", () =>
    {
        var counts = new ConcurrentDictionary<int, int>();
        Parallel.For(0, 1000, i => counts.AddOrUpdate(i % 10, 1, (_, count) => count + 1));
        return Task.FromResult(counts.Count == 10 && counts.Values.All(count => count == 100));
    }),
    ("big integers", () => Task.FromResult(BigInteger.Pow(2, 200).ToString().EndsWith("376", StringComparison.Ordinal))),
    ("vectors", () =>
    {
        var left = new Vector<int>(3);
        var right = new Vector<int>(4);
        return Task.FromResult(Vector.Sum(left * right) == 12 * Vector<int>.Count);
    }),
    ("text", () =>
    {
        var builder = new StringBuilder();
        foreach (var word in "the quick brown fox".Split(' '))
            builder.Append(char.ToUpperInvariant(word[0])).Append(word.AsSpan(1)).Append(' ');
        return Task.FromResult(builder.ToString().TrimEnd() == "The Quick Brown Fox" && $"{1234.5:F1}" == "1234.5");
    }),
};

int passed = 0;
foreach (var (name, run) in checks)
{
    bool ok;
    try
    {
        ok = await run();
    }
    catch (Exception exception)
    {
        Console.WriteLine($"[R6] {name}: threw {exception.GetType().Name}: {exception.Message}");
        continue;
    }
    if (ok)
        ++passed;
    Console.WriteLine($"[R6] {name}: {(ok ? "ok" : "FAILED")}");
}
Console.WriteLine($"[R6] A portable application on {RuntimeInformation.OSDescription} ({RuntimeInformation.RuntimeIdentifier}, " +
    $"{RuntimeInformation.FrameworkDescription}): {passed} of {checks.Length} checks passed");
return passed == checks.Length ? 0 : 1;

internal sealed record Point(int X, int Y)
{
    public double Length() => Math.Sqrt(X * X + Y * Y);
}
