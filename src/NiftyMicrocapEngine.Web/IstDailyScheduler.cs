using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Web;

/// <summary>
/// Extracted from ReconciliationSchedulerHostedService when
/// ScanSchedulerHostedService needed the identical "run once daily at a
/// configured IST hour" logic — a second hand-copied version of this math
/// would have been the kind of thing that quietly drifts (e.g. a DST-edge
/// fix applied to one copy and not the other; IST doesn't observe DST, but
/// the point generalizes).
/// </summary>
internal static class IstDailyScheduler
{
    /// <summary>
    /// Delay until the next occurrence of hourIst:00 IST, computed against
    /// IndiaStandardTime (§3.3's cross-platform Asia/Kolkata resolution —
    /// already used for market-hours logic elsewhere in this codebase, so
    /// scheduling is consistent with how the rest of the system reasons
    /// about "today" in IST). If the target hour has already passed today,
    /// this rolls to tomorrow.
    /// </summary>
    public static TimeSpan TimeUntilNextRun(DateTimeOffset utcNow, int hourIst)
    {
        var nowIst = TimeZoneInfo.ConvertTime(utcNow, IndiaStandardTime.TimeZone);
        var todayTarget = new DateTimeOffset(nowIst.Year, nowIst.Month, nowIst.Day, hourIst, 0, 0, nowIst.Offset);
        var nextTarget = todayTarget > nowIst ? todayTarget : todayTarget.AddDays(1);
        return nextTarget - nowIst;
    }
}
