using System.Security.Cryptography;
using System.Text;
using SpxQuant.Domain.Common;

namespace SpxQuant.Domain.Market;

/// <summary>A recorded chain at one instant. <see cref="CapturedAt"/> is when the response arrived (not request start).</summary>
public sealed record ChainSnapshot(
    string Underlying,
    DateOnly Expiration,
    DateTimeOffset CapturedAt,
    string Source,
    Provenance Provenance,
    IndexQuote? Index,
    IReadOnlyList<OptionQuote> Options)
{
    public static string Sha256(string canonicalJson) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson))).ToLowerInvariant();
}
