using Microsoft.Data.Sqlite;

namespace NiftyMicrocapEngine.Infrastructure.Persistence.Resilience;

/// <summary>
/// Closes a gap this README flagged since an earlier pass: SQLite serializes
/// writers — a second writer attempting to commit while another write
/// transaction is in flight gets SQLITE_BUSY (5) or SQLITE_LOCKED (6)
/// immediately, not a queued wait, unless a busy_timeout is configured.
/// This wraps WRITE operations only (every Save*/Upsert* method across the
/// repository classes) with a small bounded retry — same BCL-only,
/// exponential-backoff-with-jitter shape as RetryHandler in
/// Infrastructure.YahooFinance, for consistency, rather than introducing a
/// Polly dependency for this one narrow case.
///
/// Reads are deliberately NOT wrapped: SQLite's default journal mode allows
/// concurrent readers without blocking each other or a writer's snapshot,
/// so BUSY/LOCKED on a pure SELECT is rare enough that retrying it would
/// mostly mask a genuinely stuck writer holding the lock too long, rather
/// than smoothing over ordinary contention.
///
/// No ILogger parameter, deliberately: none of the eight repository classes
/// this wraps currently take a logger dependency, and adding one just for
/// retry visibility would mean threading a new constructor parameter through
/// every repository, every DI registration, and every test that constructs
/// one directly — out of proportion to what this fix needs. A caller that
/// wants retry visibility can catch and log around the call site instead.
/// </summary>
public static class SqliteRetry
{
    public static Task ExecuteAsync(Func<Task> operation, int maxRetries = 3) =>
        ExecuteAsync(async () => { await operation(); return true; }, maxRetries);

    public static async Task<T> ExecuteAsync<T>(Func<Task<T>> operation, int maxRetries = 3)
    {
        var jitterSource = new Random();

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (SqliteException ex) when (attempt < maxRetries && IsTransientWriteContention(ex))
            {
                await Task.Delay(ComputeBackoffDelay(attempt, jitterSource));
            }
        }
    }

    private static bool IsTransientWriteContention(SqliteException ex) => ex.SqliteErrorCode is 5 or 6; // SQLITE_BUSY, SQLITE_LOCKED

    private static TimeSpan ComputeBackoffDelay(int attempt, Random jitterSource)
    {
        var baseDelayMs = Math.Pow(2, attempt) * 100; // 100ms, 200ms, 400ms, 800ms...
        var jitterMs = jitterSource.Next(0, 100);
        return TimeSpan.FromMilliseconds(baseDelayMs + jitterMs);
    }
}
