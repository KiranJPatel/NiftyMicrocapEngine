namespace NiftyMicrocapEngine.Domain;

/// <summary>
/// Supported timeframes. Deliberately no Monthly, no 1m/5m — see build spec §2 items 1-3.
/// Daily/Weekly are primary (routed to Yahoo, §6.4); H1/M30/M15 are confirmation-only
/// (routed to the broker provider, since Yahoo's intraday retention is too short — §6.2).
/// </summary>
public enum Timeframe
{
    Daily,
    Weekly,
    H1,
    M30,
    M15
}

public static class TimeframeExtensions
{
    public static bool IsPrimary(this Timeframe timeframe) =>
        timeframe is Timeframe.Daily or Timeframe.Weekly;

    public static bool IsConfirmationOnly(this Timeframe timeframe) =>
        timeframe is Timeframe.H1 or Timeframe.M30 or Timeframe.M15;
}
