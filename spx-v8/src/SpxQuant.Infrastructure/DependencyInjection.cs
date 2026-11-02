using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SpxQuant.Application.Abstractions;
using SpxQuant.Application.Services;
using SpxQuant.Domain.Market;
using SpxQuant.Domain.Risk;
using SpxQuant.Infrastructure.Audit;
using Npgsql;
using SpxQuant.Infrastructure.Calendar;
using SpxQuant.Infrastructure.Hosting;
using SpxQuant.Infrastructure.Persistence;
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
    /// <summary>None (JSONL audit, no market-data store) or Postgres (ConnectionStrings:Postgres).</summary>
    public string Storage { get; set; } = "None";
    /// <summary>RecordedReplay only: replay clock start (UTC ISO-8601).</summary>
    public DateTimeOffset? ReplayStart { get; set; }
    /// <summary>RecordedReplay only: advance the replay clock with wall time.</summary>
    public bool ReplayAdvance { get; set; }
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
        services.AddSingleton(config.GetSection("Greeks").Get<GreeksOptions>() ?? new GreeksOptions());
        var recorder = config.GetSection("Recorder").Get<RecorderOptions>() ?? new RecorderOptions();
        if (recorder.ChainIntervalSeconds is < 5 or > 3600 || recorder.BarsIntervalSeconds is < 15 or > 3600)
            throw new InvalidOperationException("Recorder intervals: chain 5..3600 s, bars 15..3600 s (vendor rate limits)");
        services.AddSingleton(recorder);
        services.AddSingleton<RecorderStatus>();

        var usePostgres = platform.Storage.Equals("Postgres", StringComparison.OrdinalIgnoreCase);
        if (!usePostgres && !platform.Storage.Equals("None", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Platform:Storage must be None or Postgres");
        if (usePostgres)
        {
            var cs = config.GetConnectionString("Postgres");
            if (string.IsNullOrWhiteSpace(cs)) throw new InvalidOperationException("ConnectionStrings:Postgres required when Platform:Storage=Postgres");
            var db = NpgsqlDataSource.Create(cs);
            PostgresSchema.MigrateAsync(db, CancellationToken.None).GetAwaiter().GetResult();
            services.AddSingleton(db);
            services.AddSingleton<IMarketDataStore, PostgresMarketDataStore>();
        }
        if (recorder.Enabled && (!usePostgres || platform.DataSource != DataSourceKind.TradierReadOnly))
            throw new InvalidOperationException("Recorder:Enabled requires Platform:DataSource=TradierReadOnly and Platform:Storage=Postgres");

        if (platform.DataSource == DataSourceKind.RecordedReplay)
        {
            if (!usePostgres) throw new InvalidOperationException("RecordedReplay requires Platform:Storage=Postgres");
            if (platform.ReplayStart is not { } start) throw new InvalidOperationException("Platform:ReplayStart required for RecordedReplay");
            services.AddSingleton<IClock>(new ReplayClock(start, platform.ReplayAdvance));
            services.AddSingleton<RecordedReplayProvider>();
            services.AddSingleton<IMarketDataProvider>(sp => sp.GetRequiredService<RecordedReplayProvider>());
            services.AddSingleton<IBarSource>(sp => sp.GetRequiredService<RecordedReplayProvider>());
        }
        else if (platform.DataSource == DataSourceKind.Replay)
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
            services.AddTransient<IBarSource>(sp => (TradierMarketDataProvider)sp.GetRequiredService<IMarketDataProvider>());
            services.AddTransient<IBrokerSessionInfo>(sp => (TradierMarketDataProvider)sp.GetRequiredService<IMarketDataProvider>());
            services.AddTransient<CalendarCrossCheck>();
        }
        // Audit timestamps are wall-clock, even in replay.
        if (usePostgres) services.AddSingleton<IAuditLog>(sp => new PostgresAuditLog(sp.GetRequiredService<NpgsqlDataSource>(), () => DateTimeOffset.UtcNow));
        else services.AddSingleton<IAuditLog>(sp => new JsonlAuditLog(Resolve(platform.AuditLogPath), () => DateTimeOffset.UtcNow));
        if (usePostgres && platform.DataSource == DataSourceKind.TradierReadOnly)
        {
            services.AddTransient<MarketDataRecorder>();
            if (recorder.Enabled) services.AddHostedService<RecorderHostedService>();
        }
        services.AddSingleton<ContractSelectionService>();
        return services;
    }
}
