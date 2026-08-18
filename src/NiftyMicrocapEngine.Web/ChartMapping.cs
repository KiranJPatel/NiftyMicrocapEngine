using NiftyMicrocapEngine.Application.Structure;
using NiftyMicrocapEngine.Domain;

namespace NiftyMicrocapEngine.Web;

/// <summary>
/// §20.2's Charting Terminal, built now that Phase 6 validation (the
/// walk-forward backtester) exists and the Decision Engine's indicator
/// wiring is fixed — the spec's own stated reason for deferring this view
/// was to avoid redoing overlay work if weights changed during validation;
/// that risk window has passed for this pass's purposes.
/// </summary>
public sealed record ChartCandleDto(string Time, decimal Open, decimal High, decimal Low, decimal Close, long Volume);

/// <summary>Matches TradingView Lightweight Charts' setMarkers() shape directly — Shape is one of "circle"/"square"/"arrowUp"/"arrowDown", Position is "aboveBar"/"belowBar"/"inBar".</summary>
public sealed record ChartMarkerDto(string Time, string Position, string Color, string Shape, string Text);

/// <summary>An Order Block or FVG zone (§20.2 names exactly these two overlay types — Supply/Demand zones and structural swing lines are covered separately via markers/price context, not rendered as zones here).</summary>
public sealed record ChartZoneDto(string StartTime, string? EndTime, decimal Upper, decimal Lower, string Kind, string Status);

public sealed record ChartResponseDto(
    int SymbolId, string NseSymbol, string Timeframe, string AsOfDate,
    List<ChartCandleDto> Candles, List<ChartMarkerDto> Markers, List<ChartZoneDto> Zones);

public static class ChartMapping
{
    public static ChartResponseDto ToChartResponse(
        Symbol symbol, Timeframe timeframe, DateOnly asOfDate,
        IReadOnlyList<Candle> candles, StructureAnalysisPipelineFactory.Handles pipeline)
    {
        var candleDtos = candles.Select(c => new ChartCandleDto(
            FormatTime(c.Timestamp), c.Open, c.High, c.Low, c.Close, c.Volume)).ToList();

        var markers = new List<ChartMarkerDto>();

        foreach (var swing in pipeline.SwingPoints.ConfirmedSwings)
        {
            markers.Add(swing.Type == SwingType.High
                ? new ChartMarkerDto(FormatTime(swing.Timestamp), "aboveBar", "#d29922", "arrowDown", "SH")
                : new ChartMarkerDto(FormatTime(swing.Timestamp), "belowBar", "#d29922", "arrowUp", "SL"));
        }

        foreach (var brk in pipeline.StructureBreaks.Breaks)
        {
            var isBullish = brk.NewDirection == TrendDirection.Bullish;
            var color = brk.Kind == StructureBreakKind.CHoCH ? "#f85149" : (isBullish ? "#3fb950" : "#d0483d");
            markers.Add(new ChartMarkerDto(
                FormatTime(brk.Timestamp),
                isBullish ? "belowBar" : "aboveBar",
                color,
                isBullish ? "arrowUp" : "arrowDown",
                brk.Kind.ToString()));
        }

        foreach (var evt in pipeline.SmcEvents.Events)
        {
            markers.Add(new ChartMarkerDto(FormatTime(evt.Timestamp), "inBar", "#7d8798", "circle", evt.Kind.ToString()));
        }

        var zones = pipeline.SmcZones.Zones
            .Where(z => z.Kind is ZoneKind.OrderBlockBullish or ZoneKind.OrderBlockBearish or ZoneKind.FvgBullish or ZoneKind.FvgBearish)
            .Select(z => new ChartZoneDto(
                FormatTime(z.FormedTimestamp),
                z.MitigatedTimestamp is { } mt ? FormatTime(mt) : null,
                z.UpperBound, z.LowerBound, z.Kind.ToString(), z.Status.ToString()))
            .ToList();

        return new ChartResponseDto(
            symbol.SymbolId, symbol.NseSymbol, timeframe.ToString(), asOfDate.ToString("yyyy-MM-dd"),
            candleDtos, markers, zones);
    }

    /// <summary>TradingView Lightweight Charts accepts a plain "yyyy-mm-dd" business-day string for Daily/Weekly series — avoids UNIX-timestamp timezone ambiguity, and matches this codebase's existing AsOfDate string convention (DashboardMapping.ToScanResponse).</summary>
    private static string FormatTime(DateTimeOffset timestamp) => timestamp.UtcDateTime.ToString("yyyy-MM-dd");
}
