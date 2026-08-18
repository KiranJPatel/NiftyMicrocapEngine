using NiftyMicrocapEngine.Application.Persistence;
using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Application.Tests.Scanning;

public sealed class FakeSymbolRepository : ISymbolRepository
{
    private readonly Dictionary<int, Symbol> _symbols;

    public FakeSymbolRepository(IEnumerable<Symbol> symbols)
    {
        _symbols = symbols.ToDictionary(s => s.SymbolId);
    }

    public Task<Symbol?> GetBySymbolIdAsync(int symbolId, CancellationToken ct = default) =>
        Task.FromResult(_symbols.GetValueOrDefault(symbolId));

    public Task<Symbol?> GetByNseSymbolAsync(string nseSymbol, CancellationToken ct = default) =>
        Task.FromResult(_symbols.Values.FirstOrDefault(s => s.NseSymbol == nseSymbol));

    public Task<IReadOnlyList<Symbol>> GetAllActiveAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Symbol>>(_symbols.Values.Where(s => s.IsActive).ToList());

    public Task<int> UpsertAsync(Symbol symbol, CancellationToken ct = default)
    {
        _symbols[symbol.SymbolId] = symbol;
        return Task.FromResult(symbol.SymbolId);
    }

    public Task SaveMappingAsync(SymbolMapping mapping, CancellationToken ct = default) => Task.CompletedTask;

    public Task<SymbolMapping?> GetActiveMappingAsync(int symbolId, DataProviderKind provider, DateOnly asOf, CancellationToken ct = default) =>
        Task.FromResult<SymbolMapping?>(null);
}
