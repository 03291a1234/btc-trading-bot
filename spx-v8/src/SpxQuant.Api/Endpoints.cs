using SpxQuant.Application.Abstractions;
using SpxQuant.Application.Services;
using SpxQuant.Domain.Common;
using SpxQuant.Domain.Market;
using SpxQuant.Domain.Options;
using SpxQuant.Domain.Risk;
using SpxQuant.Infrastructure;

namespace SpxQuant.Api;

public static class Endpoints
{
    public const string Version = "8.0.0-phase1";

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
        r.EvaluatedAt, r.Expiration, r.Right, r.EntryWindowOpen, r.EntryWindowReason, r.DataSource,
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
        greeks = e.Quote.Greeks, e.DeltaDistance, risk = e.Risk,
    };
}
