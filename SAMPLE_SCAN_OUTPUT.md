# Sample Scan Output — Decision Engine Explainability Chain

Companion to README.md — the §25 deliverable "Sample scan output showing
the full explainability chain for at least one Strong Buy, one Hold, and
one No Trade (hard-gate) case."

## What this is, and isn't

**This is NOT output from an actual scan run.** This sandbox has no
network access to Yahoo/NSE and no .NET SDK, so there is no real market
data to run the Scanner against, and no compiler to run it with. Producing
a document that *looked* like real scan output without either of those
would misrepresent itself.

**What this IS**: the layer scores, hard-gate results, and price levels
below are invented for illustration — clearly marked as such throughout.
But the *arithmetic* connecting them to a final outcome, and the exact
wording of the reasoning text, are not invented — they're worked by hand
against the real formulas in `DecisionEngine.cs` and
`DecisionEngine.Reasoning.cs` (quoted inline below each case), the same
way `RsiGoldenFileTests.cs` hand-verifies a formula against an external
reference rather than trusting a plausible-looking number. If you feed the
real Decision Engine these exact `LayerScore` values, it produces exactly
the outcome and reasoning text shown — that part is a claim about the
code, not a guess.

Layer weights and thresholds below are the real, current values from
`appsettings.json`'s `DecisionEngine` section — not invented:

```
LayerWeights:  Structure 25, Trend 20, Momentum 15, Volume 15,
               Volatility 10, Psychology 5, SupportResistance 5,
               RelativeStrengthRegime 5   (sums to 100)
Thresholds:    StrongBuy >= 80, Buy >= 65, Watch >= 50, Hold >= 35,
               Sell >= 20, else StrongSellExit
```

---

## Case 1 — Strong Buy

**Illustrative layer scores** (symbol/date/values invented for this example):

| Layer | Max | Contribution | Reasoning fact (invented, illustrative) |
|---|---|---|---|
| Structure | 25 | 22 | "Bullish BOS confirmed on Daily, no CHoCH against direction within lookback" |
| Trend | 20 | 17 | "EMA_20 above EMA_50, ADX_14 above 25 (trending)" |
| Momentum | 15 | 12 | "RSI_14 above 55 and rising, MACD bullish crossover" |
| Volume | 15 | 11 | "Volume spike on breakout candle, OBV rising" |
| Volatility | 10 | 7 | "ATR regime favorable for breakout continuation, not overextended" |
| Psychology | 5 | 4 | "Bullish engulfing candle at demand zone" |
| SupportResistance | 5 | 4 | "Price cleared prior resistance zone cleanly" |
| RelativeStrengthRegime | 5 | 4 | "Outperforming Nifty Microcap 250 benchmark; broad market regime Bullish" |

**Hard gates**: all pass (DataQuality, CircuitLocked, RegimeSuppressed,
StructureBreakAgainstDirection) — invented for this example as "no issues."

**Confidence arithmetic** (`DecisionEngine.Evaluate`: `confidence =
layerScores.Sum(l => l.ClampedContribution)`, each contribution here
already within its layer's max so `ClampedContribution` equals
`Contribution` unchanged):

    22 + 17 + 12 + 11 + 7 + 4 + 4 + 4 = 81

**Outcome mapping** (`MapConfidenceToOutcome`): 81 >= 80 (StrongBuy
threshold) → **StrongBuy**.

**Reasoning text** (`BuildReasoningText`: concatenates every layer's
`ReasoningFacts` in order, each terminated with a period, then since no
hard gate failed: `"Confidence {confidence:F0}%. Outcome:
{DescribeOutcome(outcome)}."`):

> Bullish BOS confirmed on Daily, no CHoCH against direction within lookback. EMA_20 above EMA_50, ADX_14 above 25 (trending). RSI_14 above 55 and rising, MACD bullish crossover. Volume spike on breakout candle, OBV rising. ATR regime favorable for breakout continuation, not overextended. Bullish engulfing candle at demand zone. Price cleared prior resistance zone cleanly. Outperforming Nifty Microcap 250 benchmark; broad market regime Bullish. Confidence 81%. Outcome: Strong Buy.

**Illustrative trade plan** (a StrongBuy/Buy outcome triggers
`TradePlanBuilder` — price levels below are invented but internally
consistent 1R/2R/3R structure, Risk = Entry − StopLoss = 10.50):

| Field | Value | Check |
|---|---|---|
| Entry | 245.50 | — |
| StopLoss | 235.00 | Risk = 10.50 |
| Target1 | 256.00 | (256.00−245.50)/10.50 = **1.00R** |
| Target2 | 266.50 | (266.50−245.50)/10.50 = **2.00R** |
| Target3 | 277.00 | (277.00−245.50)/10.50 = **3.00R** |

---

## Case 2 — Hold

**Illustrative layer scores**:

| Layer | Max | Contribution | Reasoning fact (invented, illustrative) |
|---|---|---|---|
| Structure | 25 | 10 | "Range-bound structure, no confirmed BOS in either direction" |
| Trend | 20 | 8 | "EMA_20/EMA_50 roughly flat, ADX_14 below 20 (non-trending)" |
| Momentum | 15 | 6 | "RSI_14 near 50, MACD flat near the zero line" |
| Volume | 15 | 5 | "Volume below its 20-day average, no conviction either direction" |
| Volatility | 10 | 4 | "ATR compressed relative to its own recent history" |
| Psychology | 5 | 2 | "No significant candle pattern detected" |
| SupportResistance | 5 | 2 | "Price mid-range between nearest support and resistance zones" |
| RelativeStrengthRegime | 5 | 2 | "Performing roughly in line with the benchmark; regime Neutral" |

**Hard gates**: all pass.

**Confidence arithmetic**:

    10 + 8 + 6 + 5 + 4 + 2 + 2 + 2 = 39

**Outcome mapping**: 39 < 50 (Watch threshold) but >= 35 (Hold threshold)
→ **Hold**.

**Reasoning text**:

> Range-bound structure, no confirmed BOS in either direction. EMA_20/EMA_50 roughly flat, ADX_14 below 20 (non-trending). RSI_14 near 50, MACD flat near the zero line. Volume below its 20-day average, no conviction either direction. ATR compressed relative to its own recent history. No significant candle pattern detected. Price mid-range between nearest support and resistance zones. Performing roughly in line with the benchmark; regime Neutral. Confidence 39%. Outcome: Hold.

No trade plan is built for Hold — `TradePlanBuilder` is only invoked for
Buy/StrongBuy outcomes (see `UniverseScanner.Stage1.cs`/`Stage2.cs`'s
`if (decisionResult.Outcome is Buy or StrongBuy)` gate).

---

## Case 3 — No Trade (hard gate)

This case reuses **Case 1's exact layer scores** (confidence would compute
to 81, comfortably StrongBuy territory) specifically to illustrate *why*
hard gates exist: a high weighted score never overrides one.

**Hard gates**: CircuitLocked **fails** this time (invented for this
example — the symbol is at its upper circuit against a proposed Bullish
direction). Per `EvaluateHardGates`, the reasoning text for a failed
CircuitLocked gate is generated by this exact line:

    input.IsCircuitLockedAgainstDirection
        ? $"Symbol is circuit-locked against the proposed {input.ProposedDirection} direction."
        : "No circuit lock against the proposed direction."

producing: `"Symbol is circuit-locked against the proposed Bullish direction."`

**Confidence arithmetic**: identical to Case 1 — **81** — computed and
carried through even though a gate failed (`DecisionEngine.cs`'s own
comment: *"the weighted score is still computed below (for
explainability/audit purposes) but never used to override a failed
gate's NoTrade outcome, no matter how high it scores"*).

**Outcome**: `failedGate` is not null and its `Kind` is `CircuitLocked`,
not `RegimeSuppressed` — so the RegimeSuppressed override-confidence
special case doesn't apply. `outcome = DecisionOutcome.NoTrade` directly,
`hardGateFailed = HardGateKind.CircuitLocked`. **No Trade**, unconditionally.

**Reasoning text** (`BuildReasoningText`'s hard-gate-failed branch —
notice this REPLACES the "Confidence X%. Outcome: Y." sentence entirely,
it does not follow it):

> Bullish BOS confirmed on Daily, no CHoCH against direction within lookback. EMA_20 above EMA_50, ADX_14 above 25 (trending). RSI_14 above 55 and rising, MACD bullish crossover. Volume spike on breakout candle, OBV rising. ATR regime favorable for breakout continuation, not overextended. Bullish engulfing candle at demand zone. Price cleared prior resistance zone cleanly. Outperforming Nifty Microcap 250 benchmark; broad market regime Bullish. Hard gate failed: CircuitLocked. Outcome forced to No Trade regardless of the 81% weighted confidence that would otherwise apply.

This is the case worth internalizing first when reading this system's
output: a StrongBuy-caliber weighted score and a "No Trade" outcome are
not a contradiction — the reasoning text says exactly why.
