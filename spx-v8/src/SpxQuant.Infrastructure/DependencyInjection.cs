using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SpxQuant.Application.Abstractions;
using SpxQuant.Application.Services;
using SpxQuant.Domain.Market;
using SpxQuant.Domain.Risk;
using SpxQuant.Infrastructure.Audit;
using SpxQuant.Infrastructure.Calendar;
using SpxQuant.Infrastructure.Replay;
using SpxQuant.Infrastructure.Time;
using SpxQuant.Infrastructure.Tradier;

namespace SpxQuant.Infrastructure;

public sealed class PlatformOptions
{
    public DataSourceKind DataSource { get; set; } = DataSourceKind.Replay;
    public string? ReplaySnapshotPath { get; set; }
    public string CalendarPath { get; set; } = "config/calendar/us-equity-2026.json";
    public string AuditLogPath { get; set; } = "data/audit/audit.jsonl";
    public int MaxQuoteAgeSeconds { get; set; } = 15;
    public int MaxGreeksAgeSeconds { get; set; } = 300;
}

public static class DependencyInjection
{
    public static IServiceCollection AddSpxQuantInfrastructure(this IServiceCollection services, IConfiguration config, string contentRoot)
    {
        var platform = config.GetSection("Platform").Get<PlatformOptions>() ?? new PlatformOptions();
        var limits = config.GetSection("Risk").Get<RiskLimits>() ?? new RiskLimits();
        var problems = limits.Validate();
        if (problems.Count > 0) throw new InvalidOperationException("Invalid risk configuration: " + string.Join("; ", problems));
        if (platform.MaxQuoteAgeSeconds is < 1 or > 60 || platform.MaxGreeksAgeSeconds is < 1 or > 7200)
            throw new InvalidOperationException("MaxQuoteAgeSeconds must be 1..60 and MaxGreeksAgeSeconds 1..7200");

        string Resolve(string p) => Path.IsPathRooted(p) ? p : Path.Combine(contentRoot, p);
        services.AddSingleton(platform);
        services.AddSingleton(limits);
        services.AddSingleton(new QuoteValidationPolicy(TimeSpan.FromSeconds(platform.MaxQuoteAgeSeconds),
            TimeSpan.FromSeconds(platform.MaxGreeksAgeSeconds), TimeSpan.FromSeconds(2)));
        services.AddSingleton<ITradingCalendarProvider>(new StaticCalendarProvider(JsonCalendarLoader.Load(Resolve(platform.CalendarPath))));
        services.AddSingleton<MarketDataHealth>();

        if (platform.DataSource == DataSourceKind.Replay)
        {
            if (string.IsNullOrWhiteSpace(platform.ReplaySnapshotPath)) throw new InvalidOperationException("Platform:ReplaySnapshotPath required in Replay mode");
            var snapshot = ReplaySnapshot.Load(Resolve(platform.ReplaySnapshotPath));
            services.AddSingleton<IClock>(new ReplayClock(snapshot.CapturedAt));
            services.AddSingleton<IMarketDataProvider>(new ReplayMarketDataProvider(snapshot));
        }
        else
        {
            var tradier = config.GetSection(TradierOptions.Section).Get<TradierOptions>() ?? new TradierOptions();
            if (string.IsNullOrWhiteSpace(tradier.Token)) throw new InvalidOperationException("Tradier:Token required in TradierReadOnly mode (set via environment, never commit)");
            if (!Uri.TryCreate(tradier.BaseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("Tradier:BaseUrl must be an absolute https URL");
            services.AddSingleton(tradier);
            services.AddSingleton<IClock, SystemClock>();
            services.AddTransient<ReadOnlyMarketDataGuard>();
            services.AddHttpClient<IMarketDataProvider, TradierMarketDataProvider>(http =>
            {
                http.BaseAddress = baseUri;
                http.Timeout = TimeSpan.FromSeconds(Math.Clamp(tradier.TimeoutSeconds, 1, 30));
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tradier.Token.Trim());
                http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            })
            .AddHttpMessageHandler<ReadOnlyMarketDataGuard>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        }
        services.AddSingleton<IAuditLog>(sp => new JsonlAuditLog(Resolve(platform.AuditLogPath), () => DateTimeOffset.UtcNow) /* wall clock, even in replay */);
        services.AddSingleton<ContractSelectionService>();
        return services;
    }
}
