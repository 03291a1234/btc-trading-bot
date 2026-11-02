using SpxQuant.Application.Abstractions;
using SpxQuant.Application.Services;
using SpxQuant.Domain.Common;
using SpxQuant.Domain.Market;
using SpxQuant.Domain.Options;
using SpxQuant.Domain.Risk;
using SpxQuant.Infrastructure;
using SpxQuant.Infrastructure.Hosting;

namespace SpxQuant.Api;

public static class Endpoints
{
    public const string Version = "8.0.0-phase2";

    public static void MapPlatformEndpoints(this WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        app.MapGet("/health/ready", (IMarketDataProvider market, ITradingCalendarProvider cal, MarketDataHealth health, IClock clock) =>
            Results.Ok(new
            {
                status = "ready",
                dataSource = market.Kind,
                calendarCoverage = new { cal.Calendar.CoverageStart, cal.Calendar.CoverageEnd },
                marketData = health.Snapshot(clock.UtcNow, TimeSpan.FromMinutes(2)),
                note = "Readiness proves process startup and configuration only, not vendor connectivity or entitlements.",
            }));

        var api = app.MapGroup("/api/v8");

        api.MapGet("/system", (IMarketDataProvider market, IClock clock, PlatformOptions platform) => Results.Ok(new
        {
            name = "SPX Quant Scalper Pro",
            version = Version,
            execution = "PAPER_ONLY",
            liveOrderCapability = false,
            dataSource = market.Kind,
            dataProvenance = market.DataProvenance,
            clockUtc = clock.UtcNow,
            clockIsReplay = market.Kind == DataSourceKind.Replay,
            quoteMaxAgeSeconds = platform.MaxQuoteAgeSeconds,
            greeksMaxAgeSeconds = platform.MaxGreeksAgeSeconds,
            storage = platform.Storage,
        }));

        api.MapGet("/risk/limits", (RiskLimits limits) => Results.Ok(new
        {
            effective = limits,
            ceilings = new
            {
                RiskCeilings.MaxPlannedLossPerTrade, RiskCeilings.MaxWorstCaseLossPerTrade, RiskCeilings.MaxSpreadPerShare,
                RiskCeilings.MaxAggregatePremium, RiskCeilings.MaxDailyLoss, RiskCeilings.MaxOpenPositions,
                RiskCeilings.MaxTradesPerDay, RiskCeilings.ContractMultiplier,
            },
            feeProvenance = Provenance.Estimated,
            note = "Configuration may tighten but never loosen ceilings. Fees and slippage are estimates, not a broker schedule.",
        }));

        api.MapGet("/calendar/session", (string? date, ITradingCalendarProvider cal, IClock clock) =>
        {
            DateOnly d;
            if (date is null) d = EasternTime.DateOf(clock.UtcNow);
            else if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", out d)) return Results.BadRequest(new { error = "date must be yyyy-MM-dd" });
            var session = cal.Calendar.SessionFor(d);
            var entryAllowed = cal.Calendar.IsEntryAllowed(clock.UtcNow, out var reason);
            return Results.Ok(new { session, now = clock.UtcNow, entryAllowedNow = entryAllowed, entryReason = reason });
        });

        api.MapGet("/market/index/{symbol}", async (string symbol, IMarketDataProvider market, IClock clock, PlatformOptions platform, MarketDataHealth health, CancellationToken ct) =>
        {
            if (symbol is not ("SPX" or "VIX")) return Results.BadRequest(new { error = "symbol must be SPX or VIX" });
            try
            {
                var q = await market.GetIndexQuoteAsync(symbol, ct);
                health.RecordSuccess(clock.UtcNow);
                if (q is null) return Results.NotFound(new { error = "No quote returned" });
                var problems = QuoteValidator.ValidateIndex(q, clock.UtcNow, TimeSpan.FromSeconds(platform.MaxQuoteAgeSeconds));
                return Results.Ok(new { quote = q, provenance = market.DataProvenance, valid = problems.Count == 0, problems });
            }
            catch (MarketDataUnavailableException ex)
            {
                health.RecordFailure(clock.UtcNow, ex.Message);
                return Results.Problem("Market data unavailable", statusCode: 502);
            }
        });

        api.MapGet("/options/chain/0dte", async (string? right, IMarketDataProvider market, IClock clock, QuoteValidationPolicy policy, MarketDataHealth health, CancellationToken ct) =>
        {
            OptionRight? filter = null;
            if (right is not null) { if (!OptionRightParser.TryParse(right, out var r)) return Results.BadRequest(new { error = "right must be CALL or PUT" }); filter = r; }
            var now = clock.UtcNow; var today = EasternTime.DateOf(now);
            try
            {
                var chain = await market.GetOptionChainAsync(ContractSelectionService.Underlying, today, ct);
                health.RecordSuccess(clock.UtcNow);
                var rows = chain.Where(q => filter is null || q.Right == filter)
                    .OrderBy(q => q.Strike).ThenBy(q => q.Right)
                    .Select(q => new { quote = q, issues = QuoteValidator.Validate(q, now, policy, today) })
                    .ToArray();
                return Results.Ok(new { expiration = today, evaluatedAt = now, provenance = market.DataProvenance, count = rows.Length, rows });
            }
            catch (MarketDataUnavailableException ex)
            {
                health.RecordFailure(clock.UtcNow, ex.Message);
                return Results.Problem("Market data unavailable", statusCode: 502);
            }
        });

        api.MapPost("/options/select", async (SelectRequest request, ContractSelectionService service, CancellationToken ct) =>
        {
            if (!OptionRightParser.TryParse(request.Right, out var right)) return Results.BadRequest(new { error = "right must be CALL or PUT" });
            try { return Results.Ok(SelectionDto.From(await service.EvaluateAsync(right, ct))); }
            catch (MarketDataUnavailableException) { return Results.Problem("Market data unavailable; no selection made", statusCode: 502); }
        });

        api.MapGet("/market/health", (MarketDataHealth health, IClock clock, RecorderStatus recorder, IMarketDataProvider market) => Results.Ok(new
        {
            dataSource = market.Kind,
            evaluatedAt = clock.UtcNow,
            streams = health.All(clock.UtcNow, TimeSpan.FromMinutes(2)),
            recorder = recorder.Snapshot(),
        }));

        api.MapGet("/market/bars/{symbol}", async (string symbol, string? tf, DateTimeOffset? from, DateTimeOffset? to, IServiceProvider sp, IClock clock, CancellationToken ct) =>
        {
            if (sp.GetService<IMarketDataStore>() is not { } store) return Results.Json(new { error = "Market-data store not configured (Platform:Storage=Postgres)" }, statusCode: 503);
            if (symbol is not ("SPX" or "VIX")) return Results.BadRequest(new { error = "symbol must be SPX or VIX" });
            if (!TimeframeExtensions.TryParse(tf ?? "1m", out var timeframe)) return Results.BadRequest(new { error = "tf must be 1m or 5m" });
            var now = clock.UtcNow;
            var session = sp.GetRequiredService<ITradingCalendarProvider>().Calendar.SessionAt(now);
            var start = from ?? session.Open ?? now.AddHours(-8);
            var end = to ?? now;
            if (end <= start || end - start > TimeSpan.FromDays(31)) return Results.BadRequest(new { error = "range must be positive and at most 31 days" });
            var bars = (await store.GetBarsAsync(symbol, timeframe, start, end, ct)).Where(b => b.IsConfirmedAt(now)).ToArray();
            var gaps = timeframe == Timeframe.OneMinute && session.IsTradingDay && start <= session.Open && end >= now
                ? BarValidator.FindGaps(bars, session, now, TimeSpan.FromMinutes(1)) : null;
            return Results.Ok(new { symbol, timeframe = timeframe.Code(), from = start, to = end, provenance = Provenance.Observed, count = bars.Length, gaps, bars });
        });

        api.MapGet("/market/snapshots", async (DateTimeOffset? from, DateTimeOffset? to, int? limit, IServiceProvider sp, IClock clock, CancellationToken ct) =>
        {
            if (sp.GetService<IMarketDataStore>() is not { } store) return Results.Json(new { error = "Market-data store not configured" }, statusCode: 503);
            var end = to ?? clock.UtcNow;
            return Results.Ok(await store.ListSnapshotsAsync(from ?? end.AddDays(-1), end, limit ?? 500, ct));
        });

        // Export in the file-replay format, so recorded OBSERVED data can be replayed without a database.
        api.MapGet("/market/snapshots/{id:long}", async (long id, IServiceProvider sp, CancellationToken ct) =>
        {
            if (sp.GetService<IMarketDataStore>() is not { } store) return Results.Json(new { error = "Market-data store not configured" }, statusCode: 503);
            var s = await store.GetSnapshotAsync(id, ct);
            return s is null ? Results.NotFound() : Results.Ok(new
            {
                schemaVersion = 1, capturedAt = s.CapturedAt, provenance = s.Provenance,
                description = $"Recorded {s.Source} snapshot {id} ({s.Underlying} {s.Expiration:yyyy-MM-dd})", index = s.Index, options = s.Options,
            });
        });

        api.MapPost("/recorder/run", async (string step, string? date, IServiceProvider sp, RecorderStatus status, IClock clock, CancellationToken ct) =>
        {
            if (sp.GetService<MarketDataRecorder>() is not { } recorder)
                return Results.Json(new { error = "Recorder requires DataSource=TradierReadOnly and Storage=Postgres" }, statusCode: 503);
            RecordResult result;
            if (step == "chain") result = await recorder.RecordChainAsync(ct);
            else if (step == "bars" && date is null) result = await recorder.RecordBarsAsync(ct);
            else if (step == "bars" && DateOnly.TryParseExact(date, "yyyy-MM-dd", out var d)) result = await recorder.RecordBarsForDayAsync(d, ct);
            else return Results.BadRequest(new { error = "step must be chain or bars; date (bars only) yyyy-MM-dd" });
            status.Record(clock.UtcNow, result);
            return Results.Json(result, statusCode: result.Ok ? 200 : 502);
        });

        api.MapGet("/calendar/crosscheck", async (int year, int month, IServiceProvider sp, MarketDataHealth health, IClock clock, CancellationToken ct) =>
        {
            if (sp.GetService<CalendarCrossCheck>() is not { } check) return Results.Json(new { error = "Requires DataSource=TradierReadOnly" }, statusCode: 503);
            if (month is < 1 or > 12 || year is < 2020 or > 2100) return Results.BadRequest(new { error = "invalid year/month" });
            try
            {
                var diffs = await check.CheckMonthAsync(year, month, ct);
                health.RecordSuccess(clock.UtcNow, Streams.Calendar);
                return Results.Ok(new { year, month, consistent = diffs.Count == 0, discrepancies = diffs });
            }
            catch (MarketDataUnavailableException ex)
            {
                health.RecordFailure(clock.UtcNow, ex.Message, Streams.Calendar);
                return Results.Problem("Broker calendar unavailable", statusCode: 502);
            }
        });

        api.MapGet("/audit", async (long? after, int? limit, IAuditLog audit, CancellationToken ct) =>
            Results.Ok(await audit.ReadAsync(after ?? 0, limit ?? 100, ct)));
        api.MapGet("/audit/verify", async (IAuditLog audit, CancellationToken ct) =>
            await audit.VerifyAsync(ct) is { } error
                ? Results.Json(new { intact = false, error }, statusCode: 409)
                : Results.Ok(new { intact = true }));
    }
}

public sealed record SelectRequest(string Right);

public static class SelectionDto
{
    public static object From(SelectionReport r) => new
    {
        r.EvaluatedAt, r.Expiration, r.Right, r.EntryWindowOpen, r.EntryWindowReason, r.DataSource, r.GreeksSource, r.EstimatedGreeksCount,
        quoteProvenance = r.QuoteProvenance, riskProvenance = r.RiskProvenance, r.AuditSequence, r.Note,
        received = r.Selection.Received,
        eligibleCount = r.Selection.Ranked.Count,
        summary = r.Selection.Summary,
        selected = r.Selection.Selected is { } s ? Contract(s) : null,
        topEligible = r.Selection.Ranked.Take(5).Select(Contract).ToArray(),
        rejectionCounts = r.Selection.RejectionCounts,
    };

    private static object Contract(ContractEvaluation e) => new
    {
        e.Quote.Symbol, e.Quote.Root, right = e.Quote.Right, e.Quote.Strike, e.Quote.Expiration,
        e.Quote.Bid, e.Quote.Ask, e.Quote.Spread, e.Quote.BidSize, e.Quote.AskSize, e.Quote.Volume, e.Quote.OpenInterest,
        greeks = e.Quote.Greeks, greeksProvenance = e.Quote.GreeksProvenance, e.DeltaDistance, risk = e.Risk,
    };
}
