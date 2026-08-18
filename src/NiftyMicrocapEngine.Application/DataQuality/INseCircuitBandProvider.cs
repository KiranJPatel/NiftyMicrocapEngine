namespace NiftyMicrocapEngine.Application.DataQuality;

/// <summary>
/// §6.8's "real NSE circuit-band feed" — closes the one item from Phase 7's
/// own checklist ("circuit-band awareness end-to-end") this build had left
/// as a heuristic-only placeholder. Verified against NSE's actual live data
/// before building this: https://nsearchives.nseindia.com/content/equities/sec_list.csv
/// is a publicly reachable (no auth, no session/anti-bot friction unlike
/// nseindia.com's main site — see §6.5's note on that) CSV published by NSE
/// itself, columns "Symbol,Series,Security Name,Band,Remarks", Band being
/// the symbol's current price-band percentage (2/5/10/20, observed directly
/// — not assumed). NSE revises these per-symbol via surveillance actions
/// (confirmed via a 2013 Business Standard report on a mass revision), so
/// this is fetched live and cached briefly rather than hardcoded.
///
/// Feeds ICircuitBandTracker's band-aware overload, not the hard-gate
/// decision itself — this interface only reports what NSE currently says a
/// symbol's band is, nothing about whether today's price actually hit it.
/// </summary>
public interface INseCircuitBandProvider
{
    /// <summary>
    /// NseSymbol -> circuit band as a fraction (e.g. 0.05 for a 5% band),
    /// for every symbol currently published in NSE's price-band list.
    /// Implementations should cache this internally (the feed changes
    /// infrequently) — callers can call this every scan without worrying
    /// about re-fetching per symbol.
    /// </summary>
    Task<IReadOnlyDictionary<string, decimal>> GetCircuitBandsAsync(CancellationToken ct = default);
}
