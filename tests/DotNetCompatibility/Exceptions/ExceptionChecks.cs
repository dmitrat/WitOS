using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace DotNetCompatibility;

/// <summary>
/// Exceptions: catch and finally order, filters, faults the hardware raises, aggregates, stack traces and rethrow.
/// </summary>
internal static class ExceptionChecks
{
    /// <summary>
    /// The area's checks.
    /// </summary>
    /// <returns>The checks.</returns>
    public static IEnumerable<Check> All()
    {
        yield return new Check("Exceptions", "CatchFinally", () =>
        {
            var order = new List<string>();
            try
            {
                try
                {
                    order.Add("try");
                    throw new InvalidOperationException();
                }
                finally
                {
                    order.Add("finally");
                }
            }
            catch (InvalidOperationException)
            {
                order.Add("catch");
            }
            return CheckResult.Now(string.Join(",", order) == "try,finally,catch", string.Join(",", order));
        });
        yield return new Check("Exceptions", "Filter", () =>
        {
            var filtered = "";
            try
            {
                throw new ArgumentException("second");
            }
            catch (ArgumentException exception) when (exception.Message == "first")
            {
                filtered = "first";
            }
            catch (ArgumentException exception) when (exception.Message == "second")
            {
                filtered = "second";
            }
            return CheckResult.Now(filtered == "second", filtered);
        });
        yield return new Check("Exceptions", "NullReference", () =>
        {
            try
            {
                return CheckResult.Now(false, $"read {Length(null)}");
            }
            catch (NullReferenceException)
            {
                return CheckResult.Now(true, "NullReferenceException from a fault");
            }
        });
        yield return new Check("Exceptions", "DivideByZero", () =>
        {
            try
            {
                return CheckResult.Now(false, $"divided to {Divide(1, 0)}");
            }
            catch (DivideByZeroException)
            {
                return CheckResult.Now(true, "DivideByZeroException");
            }
        });
        yield return new Check("Exceptions", "Aggregate", async () =>
        {
            var both = Task.WhenAll(Task.Run(() => throw new InvalidOperationException("one")), Task.Run(() => throw new FormatException("two")));
            try
            {
                await both;
                return CheckResult.That(false, "no exception");
            }
            catch (Exception)
            {
                var inner = both.Exception?.InnerExceptions.Count ?? 0;
                return CheckResult.That(inner == 2, $"{inner} inner exceptions");
            }
        });
        yield return new Check("Exceptions", "StackTrace", () =>
        {
            try
            {
                Thrower();
                return CheckResult.Now(false, "no exception");
            }
            catch (InvalidOperationException exception)
            {
                return CheckResult.Now(exception.StackTrace?.Contains(nameof(Thrower), StringComparison.Ordinal) == true, "the trace names the method");
            }
        });
        yield return new Check("Exceptions", "Rethrow", () =>
        {
            ExceptionDispatchInfo? captured = null;
            try
            {
                Thrower();
            }
            catch (InvalidOperationException exception)
            {
                captured = ExceptionDispatchInfo.Capture(exception);
            }
            try
            {
                captured!.Throw();
                return CheckResult.Now(false, "no exception");
            }
            catch (InvalidOperationException exception)
            {
                return CheckResult.Now(exception.StackTrace?.Contains(nameof(Thrower), StringComparison.Ordinal) == true,
                    "the rethrown trace keeps the method");
            }
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Length(string? text) => text!.Length;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Divide(int left, int right) => left / right;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Thrower() => throw new InvalidOperationException("thrown");
}
