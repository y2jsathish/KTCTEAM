namespace Ktc.Application.Models;

// ---- Cash Forecasting menu. Everything here except the counts themselves is an ESTIMATE. ----

/// <summary>
/// One dispensing cash unit with its usage estimate. NotesPerDay comes from RateSource ("Daily snapshots", "Notes since last reset",
/// "Withdrawal amounts", or none). DaysLeft / RunOutUtc are estimates; Status is Out, Critical (1 day or less), Warning (3 days or less), OK or No usage data.
/// </summary>
public record CashUnitForecast(string Device, string Unit, string Type, decimal? Denomination, long Notes, decimal? Amount,
    double? NotesPerDay, string RateSource, double RateDays, string Confidence, double? DaysLeft, DateTime? RunOutUtc, string Status);

/// <summary>Suggested load for one cassette: notes expected at the visit, notes to have after it (capped by capacity) and notes to add.</summary>
public record CashLoadLine(string Unit, decimal? Denomination, long NotesNow, long ExpectedAtVisit, long TargetNotes, long AddNotes, bool UsageKnown);

/// <summary>Suggested replenishment for one ATM (estimate): visit before VisitByUtc, load per cassette.</summary>
public record CashReplenishmentSuggestion(DateTime VisitByUtc, string Reason, IReadOnlyList<CashLoadLine> Loads, decimal TargetAmount, decimal AddAmount);

/// <summary>Cash position and run-out estimate of one ATM.</summary>
public record AtmCashForecast(string AtmId, string AtmName, string Client, DateTime GeneratedUtc, DateTime? LastReplenishmentUtc,
    decimal CashAmount, long Notes, IReadOnlyList<CashUnitForecast> Units, DateTime? EarliestRunOutUtc, double? DaysLeft,
    string Status, string Confidence, bool WeekdayPattern, CashReplenishmentSuggestion? Suggestion, IReadOnlyList<string> Warnings,
    double[]? WeekdayFactors = null);

/// <summary>Cash moved by successful customer transactions on one portal-local day (actual, not an estimate). Partial = the day is not over.</summary>
public record CashDailyPoint(DateTime Day, int Withdrawals, decimal Withdrawn, int Deposits, decimal Deposited, bool Partial);

/// <summary>Notes in one cash unit at the daily snapshot.</summary>
public record CashSnapshotPoint(DateTime Day, string Unit, decimal? Denomination, long Notes);

/// <summary>The whole estate (stored after each run). Daily is the estate's actual cash withdrawn per day from the stored history.</summary>
/// <param name="LowPct">Low-cash threshold the results use, as a percent of cassette capacity; LowNotes is the same in notes.</param>
public record EstateCashForecast(DateTime GeneratedUtc, string Trigger, int HistoryDays, IReadOnlyList<AtmCashForecast> Atms,
    IReadOnlyList<CashDailyPoint> Daily, IReadOnlyList<string> Warnings, double? LowPct = null, long? LowNotes = null);

/// <summary>
/// One note value compared across the sources: XFS = notes in the cash units now (KTC CurrentCashUnitStatus), EJ = the latest counter
/// report the ATM printed, App = notes dispensed by transactions since the reset (KTC per-transaction counters).
/// Status: Match, Differs, or Not comparable (reset / report times too far apart, or a source missing).
/// </summary>
public record CashReconRow(decimal? Denomination, long? XfsNotes, long? EjNotes, long? AppDispensed, long? EjDispensed, string Status, string Detail);

public record CashReconciliation(DateTime? AppResetUtc, DateTime? EjReportUtc, DateTime? EjLastClearedUtc, DateTime? XfsUpdatedUtc,
    long? XfsDispensedTotal, long? AppDispensedTotal, long? EjDispensedTotal, IReadOnlyList<CashReconRow> Rows, string Status, IReadOnlyList<string> Notes);

/// <summary>ATM forecast page: the forecast, the reconciliation, the ATM's daily cash history and snapshots, and what was read.</summary>
public record AtmCashForecastDetail(AtmCashForecast Forecast, CashReconciliation Reconciliation, IReadOnlyList<CashDailyPoint> Daily,
    IReadOnlyList<CashSnapshotPoint> Snapshots, IReadOnlyList<string> Diagnostics, double? LowPct = null, long? LowNotes = null);

/// <summary>Everything the forecast of one ATM is built from.</summary>
/// <param name="History">Daily snapshots of this ATM's cash units, any order.</param>
/// <param name="NotesSinceReset">Notes dispensed / accepted per note value since <paramref name="ResetUtc"/> (null when not read).</param>
/// <param name="WithdrawnPerDay">Average amount withdrawn per day from the transactions (fallback when nothing better is known).</param>
/// <param name="Daily">Actual cash per day of this ATM, for the weekday pattern.</param>
public record CashForecastInput(string AtmId, string AtmName, string Client, DateTime NowUtc, DateTime NowLocal,
    IReadOnlyList<CashUnitLevel> Units, DateTime? LastReplenishmentUtc, IReadOnlyList<CashSnapshotPoint> History,
    IReadOnlyList<NotesByDenomination>? NotesSinceReset, DateTime? ResetUtc, decimal? WithdrawnPerDay, IReadOnlyList<CashDailyPoint> Daily);
