using SpxQuant.Domain.Calendar;
using SpxQuant.Infrastructure.Tradier;
using Xunit;

namespace SpxQuant.Infrastructure.Tests;

public class TradierBrokerJsonTests
{
    [Fact] public void ParsesTimeSalesUsingEpochTimestamp()
    {
        var json = """
        {"series":{"data":[
          {"time":"2026-11-02T09:30:00","timestamp":1793629800,"price":7580.1,"open":7579,"high":7582,"low":7578.5,"close":7581,"volume":0,"vwap":7580},
          {"time":"2026-11-02T09:31:00","timestamp":1793629860,"open":null,"high":7582,"low":7578.5,"close":7581}
                ]}}
        """;
        var (bars, skipped) = TradierBrokerJson.ParseTimeSales(json, TimeSpan.FromMinutes(1));
        Assert.Single(bars);
        Assert.Equal(1, skipped);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 14, 30, 0, TimeSpan.Zero), bars[0].Start);
        Assert.Equal(7582m, bars[0].High);
    }

    [Fact] public void TimeSalesFallsBackToEasternLocalTime()
    {
        var json = """{"series":{"data":{"time":"2026-11-02T09:30:00","open":1,"high":2,"low":0.5,"close":1.5}}}""";
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 14, 30, 0, TimeSpan.Zero), TradierBrokerJson.ParseTimeSales(json, TimeSpan.FromMinutes(1)).Bars[0].Start);
        Assert.Empty(TradierBrokerJson.ParseTimeSales("""{"series":null}""", TimeSpan.FromMinutes(1)).Bars);
    }

    [Fact] public void ParsesClock()
    {
        var c = TradierBrokerJson.ParseClock("""{"clock":{"date":"2026-11-02","description":"open","state":"open","timestamp":1,"next_change":"16:00","next_state":"postmarket"}}""", DateTimeOffset.UnixEpoch)!;
        Assert.Equal(new DateOnly(2026, 11, 2), c.Date);
        Assert.Equal("open", c.State);
        Assert.Equal("16:00", c.NextChange);
    }

    [Fact] public void ParsesCalendarIncludingEarlyClose()
    {
        var json = """
        {"calendar":{"month":11,"year":2026,"days":{"day":[
          {"date":"2026-11-26","status":"closed","description":"Thanksgiving"},
          {"date":"2026-11-27","status":"open","open":{"start":"09:30","end":"13:00"}},
          {"date":"2026-11-30","status":"open","open":{"start":"09:30","end":"16:00"}}]}}}
        """;
        var days = TradierBrokerJson.ParseCalendar(json);
        Assert.Equal(SessionStatus.Closed, days[0].Status);
        Assert.Equal(SessionStatus.EarlyClose, days[1].Status);
        Assert.Equal(new TimeOnly(13, 0), days[1].Close);
        Assert.Equal(SessionStatus.Open, days[2].Status);
    }
}
