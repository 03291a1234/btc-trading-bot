using System.Security.Cryptography;
using System.Text;

namespace SpxQuant.Domain.Audit;

/// <summary>
/// One append-only audit entry. Hash = SHA256(prevHash | sequence | at | kind | subject | payloadJson).
/// The chain makes edits, deletions and reordering detectable (tamper-evident); it is not tamper-proof:
/// someone with write access can rewrite the whole file. External anchoring is Phase 10 work.
/// </summary>
public sealed record AuditRecord(long Sequence, DateTimeOffset At, string Kind, string? Subject, string PayloadJson, string PreviousHash, string Hash)
{
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    public static string ComputeHash(string previousHash, long sequence, DateTimeOffset at, string kind, string? subject, string payloadJson)
    {
        var text = string.Join('\u001f', previousHash, sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            at.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture), kind, subject ?? "", payloadJson);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    public static AuditRecord Create(AuditRecord? previous, DateTimeOffset at, string kind, string? subject, string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(kind)) throw new ArgumentException("Audit kind required", nameof(kind));
        var seq = (previous?.Sequence ?? 0) + 1;
        var prev = previous?.Hash ?? GenesisHash;
        // Truncate to whole microseconds: PostgreSQL timestamptz stores microseconds, and the hash must survive a round trip.
        var utc = at.ToUniversalTime();
        utc = utc.AddTicks(-(utc.Ticks % 10));
        return new(seq, utc, kind, subject, payloadJson, prev, ComputeHash(prev, seq, utc, kind, subject, payloadJson));
    }

    /// <summary>Returns null when the chain is intact, else a description of the first break.</summary>
    public static string? VerifyChain(IEnumerable<AuditRecord> records)
    {
        var prev = GenesisHash; long expected = 1;
        foreach (var r in records)
        {
            if (r.Sequence != expected) return $"Sequence gap at {expected} (found {r.Sequence})";
            if (r.PreviousHash != prev) return $"Previous-hash mismatch at {r.Sequence}";
            if (ComputeHash(r.PreviousHash, r.Sequence, r.At, r.Kind, r.Subject, r.PayloadJson) != r.Hash) return $"Hash mismatch at {r.Sequence}";
            prev = r.Hash; expected++;
        }
        return null;
    }
}
