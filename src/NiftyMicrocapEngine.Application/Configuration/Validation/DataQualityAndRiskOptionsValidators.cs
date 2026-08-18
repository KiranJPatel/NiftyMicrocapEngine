using Microsoft.Extensions.Options;

namespace NiftyMicrocapEngine.Application.Configuration.Validation;

/// <summary>
/// Validates DataQualityGateOptions: a trailing window of zero or negative
/// makes DataQualityGate.Evaluate's Take() call meaningless, and
/// MinimumNonZeroVolumeDays exceeding TrailingWindowDays makes the gate
/// impossible to pass for ANY symbol — both are configuration mistakes worth
/// catching before the first scan silently excludes the entire universe.
/// </summary>
public sealed class DataQualityGateOptionsValidator : IValidateOptions<DataQualityGateOptions>
{
    public ValidateOptionsResult Validate(string? name, DataQualityGateOptions options)
    {
        var errors = new List<string>();

        if (options.TrailingWindowDays <= 0)
            errors.Add($"DataQualityGate:TrailingWindowDays must be positive (currently {options.TrailingWindowDays}).");

        if (options.MinimumNonZeroVolumeDays < 0)
            errors.Add($"DataQualityGate:MinimumNonZeroVolumeDays must not be negative (currently {options.MinimumNonZeroVolumeDays}).");

        if (options.MinimumNonZeroVolumeDays > options.TrailingWindowDays)
            errors.Add($"DataQualityGate:MinimumNonZeroVolumeDays ({options.MinimumNonZeroVolumeDays}) exceeds TrailingWindowDays ({options.TrailingWindowDays}) — no symbol could ever pass this gate.");

        if (options.MaxConsecutiveNoTradeDays < 0)
            errors.Add($"DataQualityGate:MaxConsecutiveNoTradeDays must not be negative (currently {options.MaxConsecutiveNoTradeDays}).");

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

/// <summary>
/// Validates RiskManagerOptions: a zero or negative RiskPerTradePercent,
/// StopAtrMultiple, or CorrelationThreshold outside [-1,1] would silently
/// corrupt every trade plan and portfolio limit check downstream.
/// </summary>
public sealed class RiskManagerOptionsValidator : IValidateOptions<RiskManagerOptions>
{
    public ValidateOptionsResult Validate(string? name, RiskManagerOptions options)
    {
        var errors = new List<string>();

        if (options.RiskPerTradePercent <= 0)
            errors.Add($"RiskManager:RiskPerTradePercent must be positive (currently {options.RiskPerTradePercent}).");

        if (options.StopAtrMultiple <= 0)
            errors.Add($"RiskManager:StopAtrMultiple must be positive (currently {options.StopAtrMultiple}) — TradePlanBuilder divides risk calculations against this.");

        if (options.MaxConcurrentPositions <= 0)
            errors.Add($"RiskManager:MaxConcurrentPositions must be positive (currently {options.MaxConcurrentPositions}).");

        if (options.MaxSectorConcentrationPercent is <= 0 or > 100)
            errors.Add($"RiskManager:MaxSectorConcentrationPercent must be in (0, 100] (currently {options.MaxSectorConcentrationPercent}).");

        if (options.CorrelationThreshold is < -1m or > 1m)
            errors.Add($"RiskManager:CorrelationThreshold must be a valid correlation coefficient in [-1, 1] (currently {options.CorrelationThreshold}).");

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

/// <summary>Validates ReconciliationOptions: a non-positive lookback or a tolerance outside [0,1] would make the section 6.6 job either a no-op or corrupt every candle it touches.</summary>
public sealed class ReconciliationOptionsValidator : IValidateOptions<ReconciliationOptions>
{
    public ValidateOptionsResult Validate(string? name, ReconciliationOptions options)
    {
        var errors = new List<string>();

        if (options.LookbackDays <= 0)
            errors.Add($"Reconciliation:LookbackDays must be positive (currently {options.LookbackDays}).");

        if (options.AdjCloseToleranceFraction is < 0 or > 1)
            errors.Add($"Reconciliation:AdjCloseToleranceFraction must be in [0, 1] (currently {options.AdjCloseToleranceFraction}) — this is a fraction (0.001 = 0.1%), not a percentage.");

        if (options.ScheduledHourIst is < 0 or > 23)
            errors.Add($"Reconciliation:ScheduledHourIst must be in [0, 23] (currently {options.ScheduledHourIst}).");

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

/// <summary>Validates ScannerOptions — added alongside ScheduledEnabled/ScheduledHourIst (Phase 7's scan-scheduling gap); Stage2ShortlistSize/MaxDegreeOfParallelism were previously unvalidated and stay that way here rather than expanding scope beyond what this pass touched.</summary>
public sealed class ScannerOptionsValidator : IValidateOptions<ScannerOptions>
{
    public ValidateOptionsResult Validate(string? name, ScannerOptions options)
    {
        var errors = new List<string>();

        if (options.ScheduledHourIst is < 0 or > 23)
            errors.Add($"Scanner:ScheduledHourIst must be in [0, 23] (currently {options.ScheduledHourIst}).");

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
