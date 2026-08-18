namespace NiftyMicrocapEngine.Domain;

public static class DateOnlyExtensions
{
    /// <summary>
    /// FIXES A REAL BUG found while auditing for production readiness, not a
    /// stylistic preference: throughout this codebase (UniverseScanner.Stage1/
    /// Stage2.cs, BroadMarketContextProvider.cs, WalkForwardBacktester.cs,
    /// DashboardEndpoints.GetChart), query boundaries were built as
    /// `someDateOnly.ToDateTime(TimeOnly.MinValue/MaxValue)` passed directly
    /// wherever a DateTimeOffset was expected.
    ///
    /// DateOnly.ToDateTime(TimeOnly) returns a DateTime with
    /// Kind=Unspecified. The implicit DateTime→DateTimeOffset conversion for
    /// Unspecified Kind uses TimeZoneInfo.Local's offset — i.e. the RUNNING
    /// PROCESS'S SYSTEM TIMEZONE, not UTC. Every candle timestamp this
    /// system actually stores comes from
    /// DateTimeOffset.FromUnixTimeSeconds(...) in
    /// YahooFinanceMarketDataProvider, which is always +00:00.
    /// SqliteCandleRepository stores and compares Timestamp as ISO-8601
    /// TEXT (Timestamp.ToString("O")) via a plain SQL >= / &lt;= — a
    /// LEXICOGRAPHIC string comparison, not a timezone-aware one. A
    /// boundary built with any offset other than +00:00 (e.g. +05:30 if
    /// this runs on an IST-configured machine — plausible for local
    /// dev/testing) sorts WRONG relative to the +00:00-stored candles: e.g.
    /// "...T00:00:00+05:30" lexicographically sorts AFTER
    /// "...T00:00:00+00:00" even though the +05:30 instant is chronologically
    /// EARLIER. On a UTC-configured deployment machine this bug is
    /// invisible (Local offset happens to equal +00:00), which is exactly
    /// why it could ship unnoticed — this is a real risk for any developer
    /// running this locally from an IST machine, not a hypothetical.
    ///
    /// One place in this codebase already did this correctly —
    /// SqliteUniverseRepository.cs's `new DateTimeOffset(dateTime,
    /// TimeSpan.Zero)` — this extension generalizes that same fix so every
    /// call site is fixed the same way instead of leaving it to be
    /// remembered per call site.
    /// </summary>
    public static DateTimeOffset ToUtcDateTimeOffset(this DateOnly date, TimeOnly time) =>
        new(date.ToDateTime(time), TimeSpan.Zero);
}
