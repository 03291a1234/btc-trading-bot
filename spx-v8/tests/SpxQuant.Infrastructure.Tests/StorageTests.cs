using SpxQuant.Domain.Calendar;
using SpxQuant.Domain.Common;
using SpxQuant.Infrastructure.Audit;
using SpxQuant.Infrastructure.Calendar;
using SpxQuant.Infrastructure.Replay;
using Xunit;

namespace SpxQuant.Infrastructure.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spxq-" + Guid.NewGuid().ToString("N"));
    private string AuditPath => Path.Combine(_dir, "audit.jsonl");
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact] public async Task AuditLogPersistsAndReloadsIntactChain()
    {
        using (var log = new JsonlAuditLog(AuditPath, () => DateTimeOffset.UnixEpoch))
        {
            await log.AppendAsync("A", "x", new { n = 1 }, default);
            await log.AppendAsync("B", null, new { n = 2 }, default);
            Assert.Null(await log.VerifyAsync(default));
        }
        using var reopened = new JsonlAuditLog(AuditPath, () => DateTimeOffset.UnixEpoch);
        var records = await reopened.ReadAsync(0, 10, default);
        Assert.Equal([1L, 2L], records.Select(r => r.Sequence));
        Assert.Equal(3, (await reopened.AppendAsync("C", null, new { }, default)).Sequence);
        Assert.Single(await reopened.ReadAsync(2, 10, default));
    }

    [Fact] public async Task TamperedAuditFileBlocksStartup()
    {
        using (var log = new JsonlAuditLog(AuditPath, () => DateTimeOffset.UnixEpoch))
        {
            await log.AppendAsync("ENTRY", "x", new { loss = 10 }, default);
            await log.AppendAsync("EXIT", "x", new { loss = 10 }, default);
        }
        File.WriteAllText(AuditPath, File.ReadAllText(AuditPath).Replace("\\u0022loss\\u0022:10", "\\u0022loss\\u0022:1"));
        Assert.Throws<InvalidDataException>(() => new JsonlAuditLog(AuditPath, () => DateTimeOffset.UnixEpoch));
    }

    [Fact] public async Task VerifyDetectsOutOfProcessDeletion()
    {
        using var log = new JsonlAuditLog(AuditPath, () => DateTimeOffset.UnixEpoch);
        await log.AppendAsync("A", null, new { }, default);
        await log.AppendAsync("B", null, new { }, default);
        File.WriteAllLines(AuditPath, File.ReadAllLines(AuditPath).Take(1));
        Assert.NotNull(await log.VerifyAsync(default));
    }

    [Fact] public void RepositoryCalendarLoadsAndMatchesTradierObservations()
    {
        var cal = JsonCalendarLoader.Load(Path.Combine(RepoPaths.Root, "config/calendar/us-equity-2026.json"));
        Assert.Equal(SessionStatus.Closed, cal.SessionFor(new(2026, 7, 3)).Status);
        Assert.Equal(SessionStatus.Open, cal.SessionFor(new(2026, 7, 2)).Status);
        Assert.Equal(SessionStatus.EarlyClose, cal.SessionFor(new(2026, 12, 24)).Status);
        Assert.Equal(SessionStatus.Closed, cal.SessionFor(new(2026, 11, 26)).Status);
        Assert.Equal(SessionStatus.Unknown, cal.SessionFor(new(2027, 1, 4)).Status);
    }

    [Fact] public void SyntheticReplaySnapshotLoadsAndIsLabeled()
    {
        var snap = ReplaySnapshot.Load(Path.Combine(RepoPaths.Root, "config/replay/synthetic-0dte-2026-11-02.json"));
        Assert.Equal(Provenance.Synthetic, snap.Provenance);
        Assert.Contains("SYNTHETIC", snap.Description);
        Assert.True(snap.Options.Count > 100);
    }

    [Fact] public void ReplayRejectsSimulatedProvenance()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "bad.json");
        File.WriteAllText(path, """{"schemaVersion":1,"capturedAt":"2026-11-02T15:00:00Z","provenance":"Simulated","description":"x","options":[]}""");
        Assert.Throws<InvalidDataException>(() => ReplaySnapshot.Load(path));
    }
}
