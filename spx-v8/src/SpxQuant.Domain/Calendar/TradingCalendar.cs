using SpxQuant.Domain.Common;

namespace SpxQuant.Domain.Calendar;

public enum SessionStatus { Open, EarlyClose, Closed, Unknown }

public sealed record CalendarDay(DateOnly Date, SessionStatus Status, TimeOnly? Close = null, string? Description = null);

public sealed record TradingSession(
    DateOnly Date,
    SessionStatus Status,
    DateTimeOffset? Open,
    DateTimeOffset? Close,
    DateTimeOffset? EntryCutoff,
    DateTimeOffset? LiquidationAt,
    string Source)
{
    public bool IsTradingDay => Status is SessionStatus.Open or SessionStatus.EarlyClose;
}

public sealed record SessionRules(TimeOnly RegularOpen, TimeOnly RegularClose, TimeSpan EntryDelayAfterOpen, TimeSpan EntryCutoffBeforeClose, TimeSpan LiquidateBeforeClose)
{
    /// <summary>v7 decisions: entries 09:35 to close-30m; liquidation 5 minutes before close. Cash-index session hours.</summary>
    public static SessionRules Default { get; } = new(new(9, 30), new(16, 0), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(5));
}

/// <summary>
/// Explicit, data-driven exchange calendar. Dates outside the loaded coverage are <see cref="SessionStatus.Unknown"/>
/// and are treated as non-trading (fail closed) rather than guessed.
/// </summary>
public sealed class TradingCalendar
{
    private readonly Dictionary<DateOnly, CalendarDay> _exceptions;
    public DateOnly CoverageStart { get; }
    public DateOnly CoverageEnd { get; }
    public string Source { get; }
    public SessionRules Rules { get; }

    public TradingCalendar(DateOnly coverageStart, DateOnly coverageEnd, IEnumerable<CalendarDay> exceptions, string source, SessionRules? rules = null)
    {
        if (coverageEnd < coverageStart) throw new ArgumentException("Calendar coverage end precedes start");
        CoverageStart = coverageStart; CoverageEnd = coverageEnd; Source = source; Rules = rules ?? SessionRules.Default;
        _exceptions = new();
        foreach (var d in exceptions)
        {
            if (d.Date < coverageStart || d.Date > coverageEnd) throw new ArgumentException($"Exception {d.Date} outside coverage");
            if (d.Status == SessionStatus.EarlyClose && d.Close is null) throw new ArgumentException($"Early close {d.Date} needs a close time");
            if (d.Status == SessionStatus.Unknown) throw new ArgumentException("Unknown is not a valid calendar entry");
            if (!_exceptions.TryAdd(d.Date, d)) throw new ArgumentException($"Duplicate calendar date {d.Date}");
        }
    }

    public TradingSession SessionFor(DateOnly date)
    {
        if (date < CoverageStart || date > CoverageEnd)
            return new(date, SessionStatus.Unknown, null, null, null, null, $"{Source} (date outside coverage)");
        var status = SessionStatus.Open;
        var close = Rules.RegularClose;
        if (_exceptions.TryGetValue(date, out var ex)) { status = ex.Status; close = ex.Close ?? close; }
        else if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) status = SessionStatus.Closed;
        if (status == SessionStatus.Closed) return new(date, status, null, null, null, null, Source);
        var openAt = EasternTime.At(date, Rules.RegularOpen);
        var closeAt = EasternTime.At(date, close);
        return new(date, status, openAt, closeAt, closeAt - Rules.EntryCutoffBeforeClose, closeAt - Rules.LiquidateBeforeClose, Source);
    }

    public TradingSession SessionAt(DateTimeOffset instant) => SessionFor(EasternTime.DateOf(instant));

    public bool IsEntryAllowed(DateTimeOffset now, out string reason)
    {
        var s = SessionAt(now);
        if (!s.IsTradingDay) { reason = s.Status == SessionStatus.Unknown ? "CALENDAR_UNKNOWN" : "MARKET_CLOSED"; return false; }
        if (now < s.Open!.Value + Rules.EntryDelayAfterOpen) { reason = "BEFORE_ENTRY_WINDOW"; return false; }
        if (now >= s.EntryCutoff!.Value) { reason = "AFTER_ENTRY_CUTOFF"; return false; }
        reason = "OK"; return true;
    }
}
