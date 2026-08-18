using NiftyMicrocapEngine.Application.Persistence;
using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Tests.Scanning;

public sealed class FakeUniverseRepository : IUniverseRepository
{
    private readonly UniverseSnapshot _snapshot;
    private readonly IReadOnlyList<int> _members;

    public FakeUniverseRepository(UniverseSnapshot snapshot, IReadOnlyList<int> members)
    {
        _snapshot = snapshot;
        _members = members;
    }

    public Task<UniverseSnapshot?> GetLatestSnapshotAsync(CancellationToken ct = default) => Task.FromResult<UniverseSnapshot?>(_snapshot);

    public Task<int> SaveSnapshotAsync(UniverseSnapshot snapshot, IReadOnlyList<int> memberSymbolIds, CancellationToken ct = default) =>
        Task.FromResult(snapshot.UniverseSnapshotId);

    public Task<IReadOnlyList<int>> GetMemberSymbolIdsAsync(int snapshotId, CancellationToken ct = default) =>
        Task.FromResult(_members);
}
