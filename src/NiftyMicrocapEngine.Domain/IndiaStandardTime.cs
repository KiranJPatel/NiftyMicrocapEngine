namespace NiftyMicrocapEngine.Domain;

/// <summary>
/// Cross-platform IST (India Standard Time) resolution. TimeZoneInfo.FindSystemTimeZoneById
/// behaves differently across OSes: Linux/macOS ship the IANA "Asia/Kolkata" id, while
/// some Windows configurations only recognize "India Standard Time". This tries the IANA
/// id first and falls back — per build spec §3.3. All candle timestamps are stored/compared
/// in this timezone consistently, never in server-local time or unqualified UTC.
/// </summary>
public static class IndiaStandardTime
{
    private const string IanaId = "Asia/Kolkata";
    private const string WindowsId = "India Standard Time";

    private static readonly Lazy<TimeZoneInfo> _instance = new(Resolve);

    public static TimeZoneInfo TimeZone => _instance.Value;

    private static TimeZoneInfo Resolve()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(IanaId);
        }
        catch (TimeZoneNotFoundException)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(WindowsId);
            }
            catch (TimeZoneNotFoundException ex)
            {
                throw new InvalidOperationException(
                    $"Could not resolve IST using either '{IanaId}' or '{WindowsId}' — " +
                    "the host OS's timezone database appears to be missing both ids.", ex);
            }
        }
    }

    public static DateTimeOffset ToIst(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, TimeZone);

    public static DateTimeOffset ConvertFromIst(DateTime istWallClockTime) =>
        new(DateTime.SpecifyKind(istWallClockTime, DateTimeKind.Unspecified), TimeZone.GetUtcOffset(istWallClockTime));
}
