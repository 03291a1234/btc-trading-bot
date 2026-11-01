using System.Text.Json;
using SpxQuant.Application.Abstractions;
using SpxQuant.Domain.Audit;

namespace SpxQuant.Infrastructure.Audit;

/// <summary>
/// Append-only, hash-chained JSON-lines audit log. The chain is verified on open; a broken chain blocks startup
/// rather than silently continuing. Single-process writer. Phase 2 moves this to PostgreSQL with the same hash chain.
/// </summary>
public sealed class JsonlAuditLog : IAuditLog, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<DateTimeOffset> _now;
    private readonly List<AuditRecord> _records;

    public JsonlAuditLog(string path, Func<DateTimeOffset> now)
    {
        _path = Path.GetFullPath(path);
        _now = now;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _records = File.Exists(_path)
            ? File.ReadLines(_path).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<AuditRecord>(l, Json)
                ?? throw new InvalidDataException("Null audit record")).ToList()
            : [];
        if (AuditRecord.VerifyChain(_records) is { } error)
            throw new InvalidDataException($"Audit log integrity failure: {error}. Preserve the file and investigate; do not delete it.");
    }

    public async Task<AuditRecord> AppendAsync(string kind, string? subject, object payload, CancellationToken ct)
    {
        var payloadJson = JsonSerializer.Serialize(payload, Json);
        await _gate.WaitAsync(ct);
        try
        {
            var record = AuditRecord.Create(_records.LastOrDefault(), _now(), kind, subject, payloadJson);
            var line = JsonSerializer.Serialize(record, Json) + "\n";
            await using (var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(line), CancellationToken.None);
                stream.Flush(flushToDisk: true);
            }
            _records.Add(record); // only after the durable write succeeded
            return record;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<AuditRecord>> ReadAsync(long afterSequence, int limit, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return _records.Where(r => r.Sequence > afterSequence).Take(Math.Clamp(limit, 1, 1000)).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<string?> VerifyAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // Re-read from disk so out-of-process edits are detected, not just in-memory state.
            var onDisk = File.Exists(_path)
                ? File.ReadLines(_path).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<AuditRecord>(l, Json)!).ToList()
                : [];
            if (onDisk.Count != _records.Count) return $"Record count changed on disk ({onDisk.Count} vs {_records.Count} in memory)";
            return AuditRecord.VerifyChain(onDisk);
        }
        catch (JsonException ex) { return $"Unreadable audit line: {ex.Message}"; }
        finally { _gate.Release(); }
    }

    public void Dispose() => _gate.Dispose();
}
