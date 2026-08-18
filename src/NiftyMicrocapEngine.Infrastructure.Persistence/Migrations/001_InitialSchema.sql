-- 001_InitialSchema.sql
-- Nifty Microcap Engine — schema per build spec §18, verbatim.
-- IndicatorValues is a key-value table deliberately — keeps the Phase-2 extended
-- indicator set (§7) purely additive with no schema migration required per new indicator.

PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS SchemaMigrations (
    MigrationId     TEXT NOT NULL PRIMARY KEY,
    AppliedAtUtc    TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS Symbols (
    SymbolId INTEGER PRIMARY KEY,
    NseSymbol TEXT NOT NULL,
    CompanyName TEXT,
    Sector TEXT,
    IsActive INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE IF NOT EXISTS SymbolMapping (
    SymbolId INTEGER NOT NULL REFERENCES Symbols(SymbolId),
    Provider TEXT NOT NULL,
    ExternalId TEXT NOT NULL,
    EffectiveFrom TEXT NOT NULL,
    EffectiveTo TEXT NULL
);

CREATE TABLE IF NOT EXISTS UniverseSnapshots (
    SnapshotId INTEGER PRIMARY KEY,
    EffectiveDate TEXT NOT NULL,
    IndexName TEXT NOT NULL DEFAULT 'NIFTY_MICROCAP_250',
    SourceDocument TEXT
);

CREATE TABLE IF NOT EXISTS UniverseSnapshotMembers (
    SnapshotId INTEGER NOT NULL REFERENCES UniverseSnapshots(SnapshotId),
    SymbolId INTEGER NOT NULL REFERENCES Symbols(SymbolId),
    PRIMARY KEY (SnapshotId, SymbolId)
);

CREATE TABLE IF NOT EXISTS Candles (
    SymbolId INTEGER NOT NULL REFERENCES Symbols(SymbolId),
    Timeframe TEXT NOT NULL,
    Timestamp TEXT NOT NULL,
    Open REAL NOT NULL, High REAL NOT NULL, Low REAL NOT NULL,
    Close REAL NOT NULL, AdjClose REAL NOT NULL, Volume INTEGER NOT NULL,
    PRIMARY KEY (SymbolId, Timeframe, Timestamp)
);

CREATE TABLE IF NOT EXISTS DataQualityFlags (
    SymbolId INTEGER NOT NULL REFERENCES Symbols(SymbolId),
    AsOfDate TEXT NOT NULL,
    FlagType TEXT NOT NULL,
    Detail TEXT,
    PRIMARY KEY (SymbolId, AsOfDate, FlagType)
);

CREATE TABLE IF NOT EXISTS IndicatorValues (
    SymbolId INTEGER NOT NULL REFERENCES Symbols(SymbolId),
    Timeframe TEXT NOT NULL,
    Timestamp TEXT NOT NULL,
    IndicatorKey TEXT NOT NULL,
    Value REAL,
    SignalState TEXT,
    PRIMARY KEY (SymbolId, Timeframe, Timestamp, IndicatorKey)
);

CREATE TABLE IF NOT EXISTS MarketStructureEvents (
    EventId INTEGER PRIMARY KEY,
    SymbolId INTEGER NOT NULL REFERENCES Symbols(SymbolId),
    Timeframe TEXT NOT NULL,
    Timestamp TEXT NOT NULL,
    EventType TEXT NOT NULL,
    Detail TEXT
);

CREATE TABLE IF NOT EXISTS Analysis (
    AnalysisId INTEGER PRIMARY KEY,
    SymbolId INTEGER NOT NULL REFERENCES Symbols(SymbolId),
    AsOfDate TEXT NOT NULL,
    Decision TEXT NOT NULL,
    Confidence REAL NOT NULL,
    LayerScoresJson TEXT NOT NULL,
    ReasoningText TEXT NOT NULL,
    HardGateFailed TEXT NULL
);

CREATE TABLE IF NOT EXISTS TradeSignals (
    SignalId INTEGER PRIMARY KEY,
    AnalysisId INTEGER NOT NULL REFERENCES Analysis(AnalysisId),
    Entry REAL, StopLoss REAL, Target1 REAL, Target2 REAL, Target3 REAL,
    RiskPercent REAL, RiskRewardRatio REAL, InvalidationLevel TEXT
);

CREATE TABLE IF NOT EXISTS ScanHistory (
    ScanId INTEGER PRIMARY KEY,
    RunAt TEXT NOT NULL,
    Stage1Count INTEGER, Stage2Count INTEGER,
    Stage1DurationMs INTEGER, Stage2DurationMs INTEGER
);

CREATE INDEX IF NOT EXISTS IX_Candles_SymbolId_Timeframe_Timestamp ON Candles (SymbolId, Timeframe, Timestamp DESC);
CREATE INDEX IF NOT EXISTS IX_IndicatorValues_SymbolId_Timeframe_Timestamp ON IndicatorValues (SymbolId, Timeframe, Timestamp DESC);
CREATE INDEX IF NOT EXISTS IX_MarketStructureEvents_SymbolId_Timeframe ON MarketStructureEvents (SymbolId, Timeframe, Timestamp DESC);
CREATE INDEX IF NOT EXISTS IX_Analysis_SymbolId_AsOfDate ON Analysis (SymbolId, AsOfDate DESC);
CREATE INDEX IF NOT EXISTS IX_SymbolMapping_SymbolId_Provider ON SymbolMapping (SymbolId, Provider);
