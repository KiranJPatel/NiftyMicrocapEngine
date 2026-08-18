namespace NiftyMicrocapEngine.Infrastructure.BrokerData;

/// <summary>
/// Supplies an active broker access token. NOT implemented here — per JP's decision
/// this engine reuses existing Zerodha/Upstox credentials from the sibling NiftySMC-
/// family systems rather than re-implementing auth. Wire the concrete implementation
/// at integration time; this interface is the seam. Do NOT hardcode a token here.
/// </summary>
public interface IBrokerCredentialProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken ct = default);
    BrokerKind Kind { get; }
}

public enum BrokerKind { Zerodha, Upstox }

/// <summary>
/// Placeholder registered by default so DI resolves cleanly during scaffolding/testing.
/// Throws clearly if actually invoked. Replace with a real IBrokerCredentialProvider
/// (reusing NiftySMC/NiftyOptionsSMC's existing token store) before running anything
/// that needs live broker data.
/// </summary>
public sealed class NotConfiguredBrokerCredentialProvider : IBrokerCredentialProvider
{
    public BrokerKind Kind => BrokerKind.Zerodha;

    public Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        throw new InvalidOperationException(
            "No real IBrokerCredentialProvider is registered. This placeholder exists only so DI resolves " +
            "during scaffolding. Wire the real credential source (reusing existing Zerodha/Upstox credentials " +
            "from NiftySMC/NiftyOptionsSMC) before running anything that needs broker data.");
    }
}
