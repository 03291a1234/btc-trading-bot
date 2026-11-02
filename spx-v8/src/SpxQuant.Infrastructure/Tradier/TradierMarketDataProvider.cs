using System.Globalization;
using Microsoft.Extensions.Logging;
using SpxQuant.Application.Abstractions;
using SpxQuant.Domain.Common;
using SpxQuant.Domain.Market;

namespace SpxQuant.Infrastructure.Tradier;

/// <summary>
/// Single consolidated Tradier client (v7 had two with different base-URL conventions).
/// Every request passes through <see cref="ReadOnlyMarketDataGuard"/>. Response bodies are never logged.
/// </summary>
public sealed class TradierMarketDataProvider(HttpClient http, TradierOptions options, IClock clock, ILogger<TradierMarketDataProvider> logger)
    : IMarketDataProvider, IBarSource, IBrokerSessionInfo
{
    private readonly TimeZoneInfo _greeksZone = TimeZoneInfo.FindSystemTimeZoneById(options.GreeksTimestampZone);
    public DataSourceKind Kind => DataSourceKind.TradierReadOnly;
    public Provenance DataProvenance => Provenance.Observed;

    public async Task<IndexQuote?> GetIndexQuoteAsync(string symbol, CancellationToken ct)
    {
        var body = await GetAsync($"/v1/markets/quotes?symbols={Uri.EscapeDataString(symbol)}&greeks=false", ct);
        return TradierJson.ParseIndexQuote(body, symbol, clock.UtcNow);
    }

    public async Task<IReadOnlyList<OptionQuote>> GetOptionChainAsync(string underlying, DateOnly expiration, CancellationToken ct)
    {
        var date = expiration.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var body = await GetAsync($"/v1/markets/options/chains?symbol={Uri.EscapeDataString(underlying)}&expiration={date}&greeks=true", ct);
        var (quotes, skipped) = TradierJson.ParseChain(body, expiration, _greeksZone);
        if (skipped > 0) logger.LogWarning("Tradier chain: skipped {Skipped} malformed or mismatched rows for {Expiration}", skipped, date);
        return quotes;
    }

    public async Task<IReadOnlyList<Bar>> GetOneMinuteBarsAsync(string symbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        // Tradier expects Eastern wall-clock "yyyy-MM-dd HH:mm". session_filter=open limits to regular hours.
        string Et(DateTimeOffset t) => EasternTime.ToEastern(t).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var body = await GetAsync($"/v1/markets/timesales?symbol={Uri.EscapeDataString(symbol)}&interval=1min&start={Uri.EscapeDataString(Et(from))}&end={Uri.EscapeDataString(Et(to))}&session_filter=open", ct);
        var (bars, skipped) = TradierBrokerJson.ParseTimeSales(body, TimeSpan.FromMinutes(1));
        if (skipped > 0) logger.LogWarning("Tradier timesales: skipped {Skipped} malformed rows for {Symbol}", skipped, symbol);
        return bars;
    }

    public async Task<BrokerClock> GetClockAsync(CancellationToken ct) =>
        TradierBrokerJson.ParseClock(await GetAsync("/v1/markets/clock", ct), clock.UtcNow)
        ?? throw new MarketDataUnavailableException("Tradier clock response unreadable");

    public async Task<IReadOnlyList<Domain.Calendar.CalendarDay>> GetCalendarMonthAsync(int year, int month, CancellationToken ct) =>
        TradierBrokerJson.ParseCalendar(await GetAsync($"/v1/markets/calendar?month={month:00}&year={year}", ct));

    private async Task<string> GetAsync(string pathAndQuery, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(pathAndQuery, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Tradier market-data GET failed with HTTP {Status}", (int)response.StatusCode);
                throw new MarketDataUnavailableException($"Tradier HTTP {(int)response.StatusCode}");
            }
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException ex) { throw new MarketDataUnavailableException("Tradier request failed", ex); }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { throw new MarketDataUnavailableException("Tradier request timed out", ex); }
    }
}
