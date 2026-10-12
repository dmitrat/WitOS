using System.Globalization;
using System.Text;

namespace DotNetCompatibility;

/// <summary>
/// Culture data: what an application formats, compares and converts by a culture, which .NET takes from ICU on
/// Windows, Linux and WitOS (plan step R4.3). Each check holds where ICU's releases agree.
/// </summary>
internal static class GlobalizationChecks
{
    /// <summary>
    /// The area's checks.
    /// </summary>
    /// <returns>The checks.</returns>
    public static IEnumerable<Check> All()
    {
        yield return new Check("Globalization", "Cultures", () =>
        {
            var cultures = CultureInfo.GetCultures(CultureTypes.SpecificCultures);
            return CheckResult.Now(cultures.Length > 100 && cultures.Any(culture => culture.Name == "fr-FR"), $"{cultures.Length} specific cultures");
        });
        yield return new Check("Globalization", "NumberFormat", () =>
        {
            var text = 1234.5.ToString("N2", new CultureInfo("de-DE"));
            return CheckResult.Now(text == "1.234,50", text);
        });
        yield return new Check("Globalization", "DateNames", () =>
        {
            var date = new DateTime(2026, 10, 12);
            var text = $"{date.ToString("MMMM", new CultureInfo("de-DE"))} {date.ToString("dddd", new CultureInfo("fr-FR"))} " +
                new CultureInfo("ja-JP").DateTimeFormat.GetMonthName(1);
            return CheckResult.Now(text == "Oktober lundi 1月", text);
        });
        yield return new Check("Globalization", "Comparison", () =>
        {
            var english = string.Compare("a", "B", new CultureInfo("en-US"), CompareOptions.None);
            var german = string.Compare("ä", "b", new CultureInfo("de-DE"), CompareOptions.None);
            return CheckResult.Now(english < 0 && german < 0 && string.CompareOrdinal("a", "B") > 0,
                $"en-US a/B {english}, de-DE ä/b {german}");
        });
        yield return new Check("Globalization", "Casing", () =>
        {
            var turkish = new CultureInfo("tr-TR");
            var text = "i".ToUpper(turkish) + "I".ToLower(turkish);
            return CheckResult.Now(text == "\u0130\u0131", string.Join(' ', text.Select(character => $"U+{(int)character:X4}")));
        });
        yield return new Check("Globalization", "Normalization", () =>
        {
            var composed = "e\u0301".Normalize(NormalizationForm.FormC);
            return CheckResult.Now(composed == "\u00e9", string.Join(' ', composed.Select(character => $"U+{(int)character:X4}")));
        });
        yield return new Check("Globalization", "Idn", () =>
        {
            var ascii = new IdnMapping().GetAscii("bücher.example");
            return CheckResult.Now(ascii == "xn--bcher-kva.example", ascii);
        });
        yield return new Check("Globalization", "Region", () =>
        {
            var region = new RegionInfo("FR");
            return CheckResult.Now(region.CurrencySymbol == "€" && region.ISOCurrencySymbol == "EUR",
                $"{region.CurrencySymbol} {region.ISOCurrencySymbol}");
        });
    }
}
