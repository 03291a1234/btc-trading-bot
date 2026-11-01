using SpxQuant.Domain.Calendar;
using SpxQuant.Domain.Common;
using Xunit;

namespace SpxQuant.Domain.Tests;

public class CalendarTests
{
    private static TradingCalendar Cal() => new(new(2026, 1, 1), new(2026, 12, 31),
        [new(new(2026, 11, 26), SessionStatus.Closed), new(new(2026, 11, 27), SessionStatus.EarlyClose, new TimeOnly(13, 0))], "test");

    [Fact] public void RegularSessionTimesRespectDst()
    {
        var summer = Cal().SessionFor(new(2026, 10, 30)); // EDT
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 13, 30, 0, TimeSpan.Zero), summer.Open!.Value.ToUniversalTime());
        var winter = Cal().SessionFor(new(2026, 11, 2)); // EST after Nov 1 change
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 14, 30, 0, TimeSpan.Zero), winter.Open!.Value.ToUniversalTime());
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 21, 0, 0, TimeSpan.Zero), winter.Close!.Value.ToUniversalTime());
    }
    [Fact] public void HolidaysAndWeekendsClosed()
    {
        Assert.Equal(SessionStatus.Closed, Cal().SessionFor(new(2026, 11, 26)).Status);
        Assert.Equal(SessionStatus.Closed, Cal().SessionFor(new(2026, 11, 1)).Status);
    }
    [Fact] public void EarlyCloseMovesCutoffs()
    {
        var s = Cal().SessionFor(new(2026, 11, 27));
        Assert.Equal(SessionStatus.EarlyClose, s.Status);
        Assert.Equal(EasternTime.At(new(2026, 11, 27), new(12, 30)), s.EntryCutoff);
        Assert.Equal(EasternTime.At(new(2026, 11, 27), new(12, 55)), s.LiquidationAt);
    }
    [Fact] public void OutsideCoverageIsUnknownAndBlocksEntry()
    {
        Assert.Equal(SessionStatus.Unknown, Cal().SessionFor(new(2027, 1, 4)).Status);
        Assert.False(Cal().IsEntryAllowed(EasternTime.At(new(2027, 1, 4), new(10, 0)), out var reason));
        Assert.Equal("CALENDAR_UNKNOWN", reason);
    }
    [Theory]
    [InlineData(9, 34, false, "BEFORE_ENTRY_WINDOW")]
    [InlineData(9, 35, true, "OK")]
    [InlineData(15, 29, true, "OK")]
    [InlineData(15, 30, false, "AFTER_ENTRY_CUTOFF")]
    public void EntryWindowBoundaries(int h, int m, bool allowed, string expected)
    {
        Assert.Equal(allowed, Cal().IsEntryAllowed(EasternTime.At(new(2026, 11, 2), new(h, m)), out var reason));
        Assert.Equal(expected, reason);
    }
    [Fact] public void InvalidCalendarDataRejected()
    {
        Assert.Throws<ArgumentException>(() => new TradingCalendar(new(2026, 1, 1), new(2026, 12, 31),
            [new(new(2026, 5, 1), SessionStatus.Closed), new(new(2026, 5, 1), SessionStatus.Closed)], "dup"));
        Assert.Throws<ArgumentException>(() => new TradingCalendar(new(2026, 1, 1), new(2026, 12, 31),
            [new(new(2026, 5, 1), SessionStatus.EarlyClose)], "no close"));
        Assert.Throws<ArgumentException>(() => new TradingCalendar(new(2026, 1, 1), new(2026, 12, 31),
            [new(new(2027, 5, 1), SessionStatus.Closed)], "outside"));
    }
}
