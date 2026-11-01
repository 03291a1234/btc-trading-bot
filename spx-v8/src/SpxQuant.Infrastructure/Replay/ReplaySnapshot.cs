using System.Text.Json;
using System.Text.Json.Serialization;
using SpxQuant.Application.Abstractions;
using SpxQuant.Domain.Common;
using SpxQuant.Domain.Market;

namespace SpxQuant.Infrastructure.Replay;

public sealed record ReplaySnapshot(
    int SchemaVersion,
    DateTimeOffset CapturedAt,
    Provenance Provenance,
    string Description,
    IndexQuote? Index,
    IReadOnlyList<OptionQuote> Options)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    public static ReplaySnapshot Load(string path)
    {
        var snap = JsonSerializer.Deserialize<ReplaySnapshot>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("Empty replay snapshot");
        if (snap.SchemaVersion != 1) throw new InvalidDataException($"Unsupported replay schema {snap.SchemaVersion}");
        if (snap.Options is null) throw new InvalidDataException("Replay snapshot has no options array");
        if (snap.Provenance is not (Provenance.Observed or Provenance.Synthetic))
            throw new InvalidDataException("Replay provenance must be Observed (recorded vendor data) or Synthetic");
        return snap;
    }
}

/// <summary>Serves one recorded or synthetic snapshot. Phase 2 extends this to time-indexed bar/quote streams.</summary>
public sealed class ReplayMarketDataProvider(ReplaySnapshot snapshot) : IMarketDataProvider
{
    public DataSourceKind Kind => DataSourceKind.Replay;
    public Provenance DataProvenance => snapshot.Provenance;
    public ReplaySnapshot Snapshot => snapshot;

    public Task<IndexQuote?> GetIndexQuoteAsync(string symbol, CancellationToken ct) =>
        Task.FromResult(snapshot.Index is { } i && i.Symbol == symbol ? i : null);

    public Task<IReadOnlyList<OptionQuote>> GetOptionChainAsync(string underlying, DateOnly expiration, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<OptionQuote>>(snapshot.Options.Where(o => o.Expiration == expiration).ToArray());
}
