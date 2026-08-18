using NiftyMicrocapEngine.Application.Persistence;
using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Tests.Scanning;

public sealed class NoSnapshotUniverseRepository : IUniverseRepository
{
    public Task<UniverseSnapshot?> GetLatestSnapshotAsync(CancellationToken ct = default) => Task.FromResult<UniverseSnapshot?>(null);
    public Task<int> SaveSnapshotAsync(UniverseSnapshot snapshot, IReadOnlyList<int> memberSymbolIds, CancellationToken ct = default) => Task.FromResult(0);
    public Task<IReadOnlyList<int>> GetMemberSymbolIdsAsync(int snapshotId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>());
}
