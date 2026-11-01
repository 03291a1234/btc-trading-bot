using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace SpxQuant.Api.Tests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string Key = "test-key-0123456789abcdef0123456789abcdef";
    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "spxq-api-" + Guid.NewGuid().ToString("N"));
    private Dictionary<string, string?> _overrides = [];

    public static ApiFactory With(Dictionary<string, string?> overrides) => new() { _overrides = overrides };

    public static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SpxQuant.sln"))) dir = dir.Parent;
            return dir!.FullName;
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Api:Key"] = Key,
            ["Platform:DataSource"] = "Replay",
            ["Platform:ReplaySnapshotPath"] = Path.Combine(Root, "config/replay/synthetic-0dte-2026-11-02.json"),
            ["Platform:CalendarPath"] = Path.Combine(Root, "config/calendar/us-equity-2026.json"),
            ["Platform:AuditLogPath"] = Path.Combine(DataDir, "audit.jsonl"),
        };
        foreach (var kv in _overrides) settings[kv.Key] = kv.Value;
        // Program reads Api:Key before Build(), so supply settings as host configuration.
        foreach (var kv in settings) builder.UseSetting(kv.Key, kv.Value);
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(settings));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (Directory.Exists(DataDir)) Directory.Delete(DataDir, true);
    }

    public HttpClient Authed()
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Add("X-Api-Key", Key);
        return c;
    }
}
