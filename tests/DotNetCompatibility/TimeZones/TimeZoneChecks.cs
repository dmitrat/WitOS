namespace DotNetCompatibility;

/// <summary>
/// Time zones (plan step N4.1): IANA zones found by id, conversions through them, the list of zones, and the local
/// zone's offset agreeing with the clock.
/// </summary>
internal static class TimeZoneChecks
{
    /// <summary>
    /// The area's checks.
    /// </summary>
    /// <returns>The checks.</returns>
    public static IEnumerable<Check> All()
    {
        yield return new Check("TimeZones", "Iana", () =>
        {
            var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
            var winter = berlin.GetUtcOffset(new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc));
            var summer = berlin.GetUtcOffset(new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc));
            return CheckResult.Now(winter == TimeSpan.FromHours(1) && summer == TimeSpan.FromHours(2), $"{winter}, {summer}");
        });
        yield return new Check("TimeZones", "Convert", () =>
        {
            var utc = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
            var tokyo = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(utc, "Asia/Tokyo");
            var back = TimeZoneInfo.ConvertTimeToUtc(tokyo, TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo"));
            var utah = TimeZoneInfo.ConvertTime(new DateTimeOffset(utc), TimeZoneInfo.FindSystemTimeZoneById("America/Denver"));
            return CheckResult.Now(tokyo.Hour == 9 && back == utc && utah.Offset == TimeSpan.FromHours(-7), $"{tokyo:O}, {utah:O}");
        });
        yield return new Check("TimeZones", "Zones", () =>
        {
            var zones = TimeZoneInfo.GetSystemTimeZones();
            var ids = zones.Select(zone => zone.Id).ToHashSet(StringComparer.Ordinal);
            var known = ids.Contains("Europe/Berlin") || ids.Contains("W. Europe Standard Time");
            return CheckResult.Now(zones.Count >= 100 && known, $"{zones.Count} zones");
        });
        yield return new Check("TimeZones", "Local", () =>
        {
            var local = TimeZoneInfo.Local;
            var now = DateTimeOffset.Now;
            var offset = local.GetUtcOffset(now);
            return CheckResult.Now(offset == now.Offset && local.Id.Length > 0, $"{local.Id} {offset}");
        });
    }
}
