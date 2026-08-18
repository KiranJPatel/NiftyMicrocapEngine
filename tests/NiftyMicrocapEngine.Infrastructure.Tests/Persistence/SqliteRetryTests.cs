using Microsoft.Data.Sqlite;
using NiftyMicrocapEngine.Infrastructure.Persistence.Resilience;
using Xunit;

namespace NiftyMicrocapEngine.Infrastructure.Tests.Persistence;

public class SqliteRetryTests
{
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int SqliteConstraint = 19; // a genuine data error — must never be retried

    [Fact]
    public async Task ExecuteAsync_TransientBusyError_RetriesAndEventuallySucceeds()
    {
        var attempts = 0;

        var result = await SqliteRetry.ExecuteAsync(() =>
        {
            attempts++;
            if (attempts < 3) throw new SqliteException("database is locked", SqliteBusy);
            return Task.FromResult(42);
        }, maxRetries: 5);

        Assert.Equal(42, result);
        Assert.Equal(3, attempts); // failed twice, succeeded on the third
    }

    [Fact]
    public async Task ExecuteAsync_LockedError_IsAlsoRetried()
    {
        var attempts = 0;

        await SqliteRetry.ExecuteAsync(() =>
        {
            attempts++;
            if (attempts < 2) throw new SqliteException("database table is locked", SqliteLocked);
            return Task.CompletedTask;
        }, maxRetries: 5);

        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_NonTransientSqliteError_IsNeverRetried()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<SqliteException>(() => SqliteRetry.ExecuteAsync<int>(() =>
        {
            attempts++;
            throw new SqliteException("UNIQUE constraint failed", SqliteConstraint);
        }, maxRetries: 5));

        Assert.Equal(1, attempts); // no retry attempted — a constraint violation retrying wouldn't fix anything
    }

    [Fact]
    public async Task ExecuteAsync_ExceedsMaxRetries_RethrowsTheLastException()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<SqliteException>(() => SqliteRetry.ExecuteAsync<int>(() =>
        {
            attempts++;
            throw new SqliteException("database is locked", SqliteBusy);
        }, maxRetries: 2));

        Assert.Equal(3, attempts); // the initial attempt plus 2 retries, then gives up
    }

    [Fact]
    public async Task ExecuteAsync_NonSqliteException_PropagatesImmediately()
    {
        // A non-SqliteException (e.g. a bug in the caller's own mapping code)
        // must never be swallowed or retried — only SQLite's own transient
        // write-contention codes are this helper's concern.
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => SqliteRetry.ExecuteAsync<int>(() =>
        {
            attempts++;
            throw new InvalidOperationException("not a SQLite problem");
        }, maxRetries: 5));

        Assert.Equal(1, attempts);
    }
}
