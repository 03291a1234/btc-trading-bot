using System.Globalization;
using System.Text.Json;
using SpxQuant.Application.Abstractions;
using SpxQuant.Domain.Calendar;
using SpxQuant.Domain.Common;
using SpxQuant.Domain.Market;

namespace SpxQuant.Infrastructure.Tradier;

/// <summary>Pure parsers for Tradier time-sales, clock and calendar responses (shapes per Tradier's public docs).</summary>
public static class TradierBrokerJson
{
    /// <summary>
    /// Parses /v1/markets/timesales. Uses the epoch-seconds "timestamp" when present, else "time" interpreted as US/Eastern.
    /// Rows with missing or non-positive OHLC are skipped (counted), never repaired. SPX is an index: volume is reported but meaningless.
    /// </summary>
    public static (IReadOnlyList<Bar> Bars, int Skipped) ParseTimeSales(string json, TimeSpan duration)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("series", out var series) || series.ValueKind != JsonValueKind.Object ||
            !series.TryGetProperty("data", out var data) || data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return ([], 0);
        var rows = data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().ToArray() : [data];
        var bars = new List<Bar>(rows.Length);
        var skipped = 0;
        foreach (var r in rows)
        {
            DateTimeOffset? start = Num(r, "timestamp") is { } ts && ts > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)ts) : null;
            if (start is null && DateTime.TryParseExact(Str(r, "time"), "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
                start = new DateTimeOffset(local, EasternTime.Zone.GetUtcOffset(local));
            var (o, h, l, c) = (Num(r, "open"), Num(r, "high"), Num(r, "low"), Num(r, "close"));
            if (start is null || o is not > 0 || h is not > 0 || l is not > 0 || c is not > 0) { skipped++; continue; }
            var vol = Num(r, "volume") is { } v && v >= 0 ? (long?)v : null;
            bars.Add(new Bar(start.Value.ToUniversalTime(), duration, o.Value, h.Value, l.Value, c.Value, vol));
        }
        return (bars, skipped);
    }

    public static BrokerClock? ParseClock(string json, DateTimeOffset receivedAt)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("clock", out var c) || c.ValueKind != JsonValueKind.Object ||
            !DateOnly.TryParseExact(Str(c, "date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return null;
        return new BrokerClock(date, Str(c, "state") ?? "unknown", Str(c, "next_change"), Str(c, "next_state"), receivedAt);
    }

    public static IReadOnlyList<CalendarDay> ParseCalendar(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("calendar", out var cal) || !cal.TryGetProperty("days", out var days) ||
            !days.TryGetProperty("day", out var day)) return [];
        var items = day.ValueKind == JsonValueKind.Array ? day.EnumerateArray().ToArray() : [day];
        var result = new List<CalendarDay>();
        foreach (var d in items)
        {
            if (!DateOnly.TryParseExact(Str(d, "date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            if (Str(d, "status") != "open") { result.Add(new(date, SessionStatus.Closed, null, Str(d, "description"))); continue; }
            TimeOnly? close = d.TryGetProperty("open", out var open) && open.ValueKind == JsonValueKind.Object &&
                TimeOnly.TryParseExact(Str(open, "end"), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end) ? end : null;
            var early = close is { } t && t < new TimeOnly(16, 0);
            result.Add(new(date, early ? SessionStatus.EarlyClose : SessionStatus.Open, close, Str(d, "description")));
        }
        return result;
    }

    private static string? Str(JsonElement x, string key) => x.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static decimal? Num(JsonElement x, string key) => x.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var n) ? n : null;
}
