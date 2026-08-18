using NiftyMicrocapEngine.Infrastructure.BrokerData.Nse;
using Xunit;

namespace NiftyMicrocapEngine.Infrastructure.Tests.BrokerData;

/// <summary>
/// The sample CSV text below is a small, verbatim-shaped excerpt matching
/// the real structure fetched from
/// https://nsearchives.nseindia.com/content/equities/sec_list.csv while
/// building this feature (see NseCircuitBandProvider's doc comment) — same
/// header, same column order, same quoting convention for Remarks, same
/// observed phenomenon of one symbol appearing under multiple Series at
/// different bands (AAREYDRUGS: BE at 2%, later EQ at 5%, in the real
/// file). Only Parse() is tested here — GetCircuitBandsAsync's HTTP
/// fetch/cache logic needs a live network call to exercise for real, which
/// this sandbox doesn't have; that part is reviewed by inspection only.
/// </summary>
public class NseCircuitBandProviderParseTests
{
    private const string SampleCsv = """
        Symbol,Series,Security Name,Band,Remarks
        21STCENMGM,EQ,21ST CENTURY MANAGEMENT SERVICES LIMITED,2,"-"
        AAREYDRUGS,BE,AAREY DRUGS & PHARMACEUTICALS LIMITED,2,"-"
        ANSALAPI,BZ,ANSAL PROPERTIES & INFRASTRUCTURE LIMITED,2,"GSM STAGE - I"
        RELIANCE,EQ,RELIANCE INDUSTRIES LIMITED,20,"-"
        TCS,EQ,TATA CONSULTANCY SERVICES LIMITED,20,"-"
        AAREYDRUGS,EQ,AAREY DRUGS & PHARMACEUTICALS LIMITED,5,"-"

        MALFORMEDROW,EQ,BAD ROW WITH NO BAND,,"-"
        """;

    [Fact]
    public void Parse_StandardRow_ConvertsPercentToFraction()
    {
        var result = NseCircuitBandProvider.Parse(SampleCsv);

        Assert.Equal(0.20m, result["RELIANCE"]);
        Assert.Equal(0.20m, result["TCS"]);
        Assert.Equal(0.02m, result["21STCENMGM"]);
    }

    [Fact]
    public void Parse_SymbolWithBothEqAndNonEqRows_PrefersTheEqSeriesRow()
    {
        // AAREYDRUGS appears as BE-series at 2% AND EQ-series at 5% in the
        // sample — EQ should win regardless of row order, since EQ is what
        // this system's OHLCV data (via Yahoo) actually reflects.
        var result = NseCircuitBandProvider.Parse(SampleCsv);

        Assert.Equal(0.05m, result["AAREYDRUGS"]);
    }

    [Fact]
    public void Parse_NonEqSeriesOnly_FallsBackToWhicheverRowExists()
    {
        var result = NseCircuitBandProvider.Parse(SampleCsv);

        Assert.Equal(0.02m, result["ANSALAPI"]); // only a BZ-series row exists for this one
    }

    [Fact]
    public void Parse_MalformedRowWithNoBandValue_IsSkippedWithoutThrowing()
    {
        var result = NseCircuitBandProvider.Parse(SampleCsv);

        Assert.False(result.ContainsKey("MALFORMEDROW"));
    }

    [Fact]
    public void Parse_DuplicateSymbolAcrossSeries_CollapsesToOneEntry()
    {
        // 6 data rows in the sample, but AAREYDRUGS appears twice (BE + EQ)
        // and MALFORMEDROW is skipped — 5 unique, valid symbols remain:
        // 21STCENMGM, AAREYDRUGS, ANSALAPI, RELIANCE, TCS. Also confirms the
        // blank line between rows doesn't throw off row counting.
        var result = NseCircuitBandProvider.Parse(SampleCsv);

        Assert.Equal(5, result.Count);
    }

    [Fact]
    public void Parse_EmptyInput_ReturnsEmptyDictionary_RatherThanThrowing()
    {
        var result = NseCircuitBandProvider.Parse("Symbol,Series,Security Name,Band,Remarks");

        Assert.Empty(result);
    }

    [Fact]
    public void Parse_SymbolLookup_IsCaseInsensitive()
    {
        var result = NseCircuitBandProvider.Parse(SampleCsv);

        Assert.True(result.ContainsKey("reliance"));
        Assert.Equal(0.20m, result["reliance"]);
    }
}
