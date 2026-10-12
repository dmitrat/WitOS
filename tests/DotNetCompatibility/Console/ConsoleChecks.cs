using System.Globalization;

namespace DotNetCompatibility;

/// <summary>
/// The console's output and what an application formats for it.
/// </summary>
internal static class ConsoleChecks
{
    /// <summary>
    /// The area's checks.
    /// </summary>
    /// <returns>The checks.</returns>
    public static IEnumerable<Check> All()
    {
        yield return new Check("Console", "Out", () =>
        {
            Console.Out.WriteLine("[N1] a line through Console.Out");
            Console.Out.Flush();
            return CheckResult.Now(true, "written");
        });
        yield return new Check("Console", "Error", () =>
        {
            Console.Error.WriteLine("[N1] a line through Console.Error");
            return CheckResult.Now(true, "written");
        });
        yield return new Check("Console", "SetOut", () =>
        {
            var original = Console.Out;
            using var captured = new StringWriter();
            Console.SetOut(captured);
            Console.Write("captured");
            Console.SetOut(original);
            return CheckResult.Now(captured.ToString() == "captured", $"'{captured}'");
        });
        yield return new Check("Console", "NewLine", () =>
        {
            var expected = OperatingSystem.IsWindows() ? "\r\n" : "\n";
            return CheckResult.Now(Environment.NewLine == expected, Environment.NewLine == "\n" ? "\\n" : "\\r\\n");
        });
        yield return new Check("Console", "InvariantFormatting", () =>
        {
            var text = string.Format(CultureInfo.InvariantCulture, "{0:N2} {1:yyyy-MM-dd}", 1234.5, new DateTime(2026, 10, 12));
            return CheckResult.Now(text == "1,234.50 2026-10-12", text);
        });
    }
}
