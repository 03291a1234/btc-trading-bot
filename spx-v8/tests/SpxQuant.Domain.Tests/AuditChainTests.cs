using SpxQuant.Domain.Audit;
using Xunit;

namespace SpxQuant.Domain.Tests;

public class AuditChainTests
{
    private static List<AuditRecord> Chain(int n)
    {
        var list = new List<AuditRecord>();
        for (var i = 0; i < n; i++) list.Add(AuditRecord.Create(list.LastOrDefault(), Fixtures.Now.AddSeconds(i), "K", $"s{i}", $"{{\"i\":{i}}}"));
        return list;
    }

    [Fact] public void IntactChainVerifies() => Assert.Null(AuditRecord.VerifyChain(Chain(5)));
    [Fact] public void EditedPayloadDetected()
    {
        var c = Chain(3); c[1] = c[1] with { PayloadJson = "{\"i\":99}" };
        Assert.Contains("Hash mismatch at 2", AuditRecord.VerifyChain(c));
    }
    [Fact] public void DeletionDetected()
    {
        var c = Chain(3); c.RemoveAt(1);
        Assert.NotNull(AuditRecord.VerifyChain(c));
    }
    [Fact] public void ReorderDetected()
    {
        var c = Chain(3); (c[0], c[1]) = (c[1], c[0]);
        Assert.NotNull(AuditRecord.VerifyChain(c));
    }
    [Fact] public void TimestampsAreTruncatedToMicroseconds()
    {
        var r = AuditRecord.Create(null, Fixtures.Now.AddTicks(1234567), "K", null, "{}");
        Assert.Equal(0, r.At.Ticks % 10);
        Assert.Null(AuditRecord.VerifyChain([r]));
    }
    [Fact] public void FirstRecordLinksToGenesis() => Assert.Equal(AuditRecord.GenesisHash, Chain(1)[0].PreviousHash);
}
