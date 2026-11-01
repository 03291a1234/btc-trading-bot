using System.Globalization;
using System.Text.Json;
using SpxQuant.Domain.Market;

namespace SpxQuant.Infrastructure.Tradier;

/// <summary>Pure Tradier JSON → domain mapping. Malformed rows are skipped and counted, never repaired.</summary>
public static class TradierJson
{
    public static IndexQuote? ParseIndexQuote(string json, string symbol, DateTimeOffset receivedAt)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("quotes", out var quotes) || quotes.ValueKind != JsonValueKind.Object ||
            !quotes.TryGetProperty("quote", out var q)) return null;
        if (q.ValueKind == JsonValueKind.Array)
            q = q.EnumerateArray().FirstOrDefault(x => Str(x, "symbol") == symbol);
        if (q.ValueKind != JsonValueKind.Object || Str(q, "symbol") != symbol) return null;
        return new IndexQuote(symbol, Num(q, "last"), EpochMs(q, "trade_date"), receivedAt);
    }

    public static (IReadOnlyList<OptionQuote> Quotes, int Skipped) ParseChain(string json, DateOnly expiration, TimeZoneInfo greeksZone)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Object ||
            !options.TryGetProperty("option", out var raw) || raw.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return ([], 0);
        var items = raw.ValueKind == JsonValueKind.Array ? raw.EnumerateArray().ToArray() : [raw];
        var result = new List<OptionQuote>(items.Length);
        var skipped = 0;
        foreach (var x in items)
        {
            var symbol = Str(x, "symbol"); var root = Str(x, "root_symbol"); var strike = Num(x, "strike");
            if (symbol is null || root is null || strike is null || !OptionRightParser.TryParse(Str(x, "option_type"), out var right) ||
                !DateOnly.TryParseExact(Str(x, "expiration_date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var exp) ||
                exp != expiration)
            { skipped++; continue; }
            OptionGreeks? greeks = null;
            if (x.TryGetProperty("greeks", out var g) && g.ValueKind == JsonValueKind.Object)
                greeks = new(Num(g, "delta"), Num(g, "gamma"), Num(g, "theta"), Num(g, "vega"), Num(g, "mid_iv"), LocalStamp(g, "updated_at", greeksZone));
            result.Add(new OptionQuote(symbol, root, right, exp, strike.Value, Num(x, "bid"), Num(x, "ask"),
                Long(x, "bidsize") ?? 0, Long(x, "asksize") ?? 0, EpochMs(x, "bid_date"), EpochMs(x, "ask_date"),
                Long(x, "volume"), Long(x, "open_interest"), greeks));
        }
        return (result, skipped);
    }

    private static string? Str(JsonElement x, string key) => x.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static decimal? Num(JsonElement x, string key) => x.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var n) ? n : null;
    private static long? Long(JsonElement x, string key) => Num(x, key) is { } d && d >= 0 && d <= long.MaxValue ? (long)d : null;

    private static DateTimeOffset? EpochMs(JsonElement x, string key)
    {
        if (Num(x, key) is not { } n || n <= 0 || n > 253402300799999m) return null;
        return DateTimeOffset.FromUnixTimeMilliseconds((long)n);
    }

    private static DateTimeOffset? LocalStamp(JsonElement x, string key, TimeZoneInfo zone)
    {
        if (!DateTime.TryParseExact(Str(x, key), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) return null;
        if (zone.IsInvalidTime(local)) return null;
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}
