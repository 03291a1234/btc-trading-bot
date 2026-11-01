namespace SpxQuant.Infrastructure.Tradier;

public sealed class TradierOptions
{
    public const string Section = "Tradier";
    public string BaseUrl { get; set; } = "https://api.tradier.com";
    /// <summary>Supply via environment (Tradier__Token) or a secret store. Never commit.</summary>
    public string? Token { get; set; }
    public int TimeoutSeconds { get; set; } = 10;
    /// <summary>
    /// Time zone of greeks.updated_at ("yyyy-MM-dd HH:mm:ss", no offset). UNVERIFIED: confirm during a live session.
    /// Either wrong interpretation makes Greeks look stale or future-dated and therefore rejected (fail closed).
    /// </summary>
    public string GreeksTimestampZone { get; set; } = "America/New_York";
}
